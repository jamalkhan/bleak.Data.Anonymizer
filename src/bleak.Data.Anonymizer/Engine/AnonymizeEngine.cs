using System.Text;
using bleak.Data.Anonymizer.Anonymizers;
using bleak.Data.Anonymizer.Config;
using bleak.Data.InfiniteDataLoader;

namespace bleak.Data.Anonymizer.Engine;

/// <summary>
/// Runs an <see cref="AnonConfig"/>: for each file it replaces the configured PII columns with fake values
/// and writes the result to a renamed file in the output folder. Everything else — keys, metrics, unlisted
/// columns — passes through unchanged, and the original files are never modified or deleted.
///
/// Files with no configured columns are byte-copied (fast path), so multi-GB PII-free inputs cost only a
/// disk copy. Files that do have columns are streamed one record at a time via <see cref="CsvReader"/>, so
/// memory stays flat regardless of input size (the 20 GB+ inputs process without buffering).
/// </summary>
public static class AnonymizeEngine
{
    /// <summary>One compiled column rule: the resolved header index, its anonymizer, and parsed options.</summary>
    private readonly record struct CompiledRule(int Index, string Name, IAnonymizer Anonymizer, AnonOptions Options, ColumnWhenEmpty WhenEmpty);

    /// <summary>
    /// Executes every file job. <paramref name="configDir"/> anchors relative input/output paths.
    /// <paramref name="outputFolderOverride"/> supplies the output folder when the config leaves it unset.
    /// </summary>
    public static IReadOnlyList<FileResult> Run(
        AnonConfig config,
        string configDir,
        string? outputFolderOverride,
        AnonymizerRegistry? registry = null,
        Action<string>? log = null)
    {
        registry ??= AnonymizerRegistry.CreateDefault();
        log ??= _ => { };

        var rng = config.Run.Seed is { } seed ? new Random(seed) : new Random();
        var ctx = new AnonContext(rng, config.Run.EmailDomains);

        var inputBase = string.IsNullOrWhiteSpace(config.InputFolder)
            ? configDir
            : PathUtil.Resolve(config.InputFolder!, configDir);

        var defaultOutputFolder = ResolveOutputFolder(config, configDir, outputFolderOverride);

        var results = new List<FileResult>();
        foreach (var job in config.Files)
        {
            var result = RunFile(job, config, configDir, inputBase, defaultOutputFolder, registry, ctx, log);
            results.Add(result);
        }
        return results;
    }

    private static string? ResolveOutputFolder(AnonConfig config, string configDir, string? outputFolderOverride)
    {
        if (!string.IsNullOrWhiteSpace(config.OutputFolder))
        {
            return PathUtil.Resolve(config.OutputFolder!, configDir);
        }
        if (!string.IsNullOrWhiteSpace(outputFolderOverride))
        {
            return PathUtil.Resolve(outputFolderOverride!, configDir);
        }
        return null;
    }

    private static FileResult RunFile(
        FileJobConfig job,
        AnonConfig config,
        string configDir,
        string inputBase,
        string? defaultOutputFolder,
        AnonymizerRegistry registry,
        AnonContext ctx,
        Action<string> log)
    {
        var inputPath = PathUtil.Resolve(job.Input, inputBase);
        var outputPath = ResolveOutputPath(job, config, configDir, defaultOutputFolder, inputPath);

        var result = new FileResult { Input = inputPath, Output = outputPath, Copied = job.Columns.Count == 0 };

        if (!File.Exists(inputPath))
        {
            var msg = $"input not found: {inputPath}";
            if (config.Run.OnError == AnonErrorPolicy.Abort)
            {
                throw new FileNotFoundException(msg, inputPath);
            }
            log($"[skip] {Path.GetFileName(inputPath)}: {msg}");
            result.Skipped = msg;
            return result;
        }

        // Never write on top of the source — that would corrupt the original we were told to preserve.
        if (PathsEqual(inputPath, outputPath))
        {
            throw new InvalidOperationException(
                $"Output path equals input path for '{inputPath}'. Configure a distinct outputFolder — originals must be preserved.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (job.Columns.Count == 0)
        {
            log($"[copy] {Path.GetFileName(inputPath)} -> {Path.GetFileName(outputPath)} (no PII columns)");
            File.Copy(inputPath, outputPath, overwrite: true);
            return result;
        }

        StreamFile(job, config, inputPath, outputPath, registry, ctx, result, log);
        return result;
    }

    private static void StreamFile(
        FileJobConfig job,
        AnonConfig config,
        string inputPath,
        string outputPath,
        AnonymizerRegistry registry,
        AnonContext ctx,
        FileResult result,
        Action<string> log)
    {
        var delimiter = DelimiterUtil.ResolveDelimiter(job.Delimiter);
        var quote = DelimiterUtil.ResolveQuote(job.Quote);

        using var reader = new CsvReader(inputPath, delimiter, quote);
        var compiled = Compile(job, reader, registry, result, log);

        // UTF-8 without a BOM, large buffer — matches typical CSV and keeps syscalls down on big files.
        using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new StreamWriter(outStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1 << 20);

        var sb = new StringBuilder(4096);
        if (job.HasHeader && reader.Headers.Count > 0)
        {
            sb.Clear();
            CsvWriter.AppendRecord(sb, reader.Headers, delimiter, quote);
            writer.Write(sb);
            writer.Write('\n');
        }

        foreach (var record in reader.ReadRecords())
        {
            ctx.NewRow();
            foreach (var rule in compiled)
            {
                if (rule.Index >= record.Length)
                {
                    continue; // short row: nothing to anonymize at this position
                }

                var original = record[rule.Index];
                if (string.IsNullOrEmpty(original) && rule.WhenEmpty == ColumnWhenEmpty.Passthrough)
                {
                    continue; // don't fabricate data where the source had none
                }

                try
                {
                    record[rule.Index] = rule.Anonymizer.Generate(original, ctx, rule.Options);
                    result.CellsAnonymized++;
                }
                catch (Exception ex)
                {
                    if (config.Run.OnError == AnonErrorPolicy.Abort)
                    {
                        throw;
                    }
                    result.Errors++;
                    log($"[warn] {Path.GetFileName(inputPath)} row {result.Rows + 1} column '{rule.Name}': {ex.Message} (left unchanged)");
                }
            }

            sb.Clear();
            CsvWriter.AppendRecord(sb, record, delimiter, quote);
            writer.Write(sb);
            writer.Write('\n');
            result.Rows++;
        }

        log($"[anon] {Path.GetFileName(inputPath)} -> {Path.GetFileName(outputPath)}: "
            + $"{result.Rows:N0} rows, {result.CellsAnonymized:N0} cells across {compiled.Count} column(s)"
            + (result.Errors > 0 ? $", {result.Errors:N0} error(s)" : ""));
    }

    private static IReadOnlyList<CompiledRule> Compile(
        FileJobConfig job, CsvReader reader, AnonymizerRegistry registry, FileResult result, Action<string> log)
    {
        var compiled = new List<CompiledRule>(job.Columns.Count);
        foreach (var col in job.Columns)
        {
            var index = reader.IndexOf(col.Name);
            if (index < 0)
            {
                result.MissingColumns.Add(col.Name);
                log($"[warn] {Path.GetFileName(result.Input)}: column '{col.Name}' not found in header — skipped.");
                continue;
            }

            var anonymizer = registry.Get(col.Method); // throws early on an unknown method
            compiled.Add(new CompiledRule(index, col.Name, anonymizer, new AnonOptions(col.Options), col.WhenEmpty));
        }
        return compiled;
    }

    private static string ResolveOutputPath(
        FileJobConfig job, AnonConfig config, string configDir, string? defaultOutputFolder, string inputPath)
    {
        // An explicit rooted Output wins outright; a bare Output filename lands in the run's output folder.
        if (!string.IsNullOrWhiteSpace(job.Output))
        {
            var expanded = PathUtil.ExpandHome(job.Output!);
            if (Path.IsPathRooted(expanded) || expanded.Contains('/') || expanded.Contains('\\'))
            {
                return PathUtil.Resolve(job.Output!, configDir);
            }
            var folderForNamed = RequireOutputFolder(defaultOutputFolder, inputPath);
            return Path.Combine(folderForNamed, expanded);
        }

        var folder = RequireOutputFolder(defaultOutputFolder, inputPath);
        return Path.Combine(folder, DeriveOutputName(Path.GetFileName(inputPath), config.Run));
    }

    private static string RequireOutputFolder(string? defaultOutputFolder, string inputPath)
        => defaultOutputFolder
           ?? throw new InvalidOperationException(
               $"No output folder configured and none supplied for '{inputPath}'. Set 'outputFolder' in the config or pass one to the command.");

    /// <summary>Swaps the configured input prefix (e.g. <c>src_</c>) for the output prefix (<c>anonymized_</c>).</summary>
    internal static string DeriveOutputName(string inputFileName, AnonRunConfig run)
    {
        if (!string.IsNullOrEmpty(run.InputPrefix)
            && inputFileName.StartsWith(run.InputPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return run.OutputPrefix + inputFileName[run.InputPrefix.Length..];
        }
        return run.OutputPrefix + inputFileName;
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
