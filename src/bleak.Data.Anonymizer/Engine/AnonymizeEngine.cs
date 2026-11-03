using System.Diagnostics;
using System.Text;
using bleak.Data.Anonymizer.Anonymizers;
using bleak.Data.Anonymizer.Config;
using bleak.Data.InfiniteDataLoader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace bleak.Data.Anonymizer.Engine;

/// <summary>
/// Runs an <see cref="AnonConfig"/>: for each file it replaces the configured PII columns with fake values
/// and writes the result to a renamed file in the output folder. Everything else — keys, metrics, unlisted
/// columns — passes through unchanged, and the original files are never modified or deleted.
///
/// Files with no configured columns are byte-copied (fast path), so multi-GB PII-free inputs cost only a
/// disk copy. Files that do have columns are streamed one record at a time via <see cref="CsvReader"/>, so
/// memory stays flat regardless of input size (the 20 GB+ inputs process without buffering).
///
/// Logging: only metadata (counts, sizes, durations, file and column names) is ever logged. Cell values,
/// original or anonymized, are never logged at any level, and neither are anonymizer exception messages
/// (they could quote the cell); only the exception type is reported for a failed cell.
/// </summary>
public static partial class AnonymizeEngine
{
    /// <summary>At most this many failed cells per file are described (row number + column, no values) at Debug.</summary>
    private const int MaxCellErrorExamples = 5;

    /// <summary>One compiled column rule: the resolved header index, its anonymizer, and parsed options.</summary>
    private readonly record struct CompiledRule(int Index, string Name, string Method, IAnonymizer Anonymizer, AnonOptions Options, ColumnWhenEmpty WhenEmpty);

    /// <summary>Per-column counters for one file. Errors are always counted; the rest only while Debug is enabled.</summary>
    private struct ColumnStats
    {
        public long Cells;
        public long Skipped;
        public long Errors;
        public long Ticks;
    }

    /// <summary>Run-wide per-method aggregate, collected only while Debug is enabled.</summary>
    private sealed class MethodStats
    {
        public long Cells;
        public long Errors;
        public long Ticks;
        public int Columns;
    }

    /// <summary>Shared per-run state handed down to the per-file helpers.</summary>
    private sealed class RunState(ILogger logger, ILogger<CsvReader> readerLogger, Dictionary<string, MethodStats>? methodStats)
    {
        public ILogger Logger { get; } = logger;
        public ILogger<CsvReader> ReaderLogger { get; } = readerLogger;
        public Dictionary<string, MethodStats>? MethodStats { get; } = methodStats;

        /// <summary>Set once a failure has already been logged, so the file-level handler doesn't log it twice.</summary>
        public bool FailureLogged { get; set; }
    }

    /// <summary>
    /// Executes every file job. <paramref name="configDir"/> anchors relative input/output paths.
    /// <paramref name="outputFolderOverride"/> supplies the output folder when the config leaves it unset.
    /// <paramref name="loggerFactory"/> (optional) receives the engine's log events under the
    /// <c>bleak.Data.Anonymizer.Engine.AnonymizeEngine</c> category and is also used to create the
    /// <see cref="CsvReader"/> loggers.
    /// </summary>
    public static IReadOnlyList<FileResult> Run(
        AnonConfig config,
        string configDir,
        string? outputFolderOverride,
        AnonymizerRegistry? registry = null,
        ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(typeof(AnonymizeEngine));
        long runStart = Stopwatch.GetTimestamp();

        registry ??= AnonymizerRegistry.CreateDefault();

        var configName = string.IsNullOrWhiteSpace(config.Name) ? "(unnamed)" : config.Name!;
        Log.RunStarted(logger, configName, config.Files.Count);

        var rng = config.Run.Seed is { } seed ? new Random(seed) : new Random();
        var ctx = new AnonContext(rng, config.Run.EmailDomains);

        var inputBase = string.IsNullOrWhiteSpace(config.InputFolder)
            ? configDir
            : PathUtil.Resolve(config.InputFolder!, configDir);

        var defaultOutputFolder = ResolveOutputFolder(config, configDir, outputFolderOverride);
        Log.RunSettings(logger, inputBase, defaultOutputFolder ?? "(none)", config.Run.OnError, config.Run.Seed.HasValue);

        var state = new RunState(
            logger,
            loggerFactory.CreateLogger<CsvReader>(),
            logger.IsEnabled(LogLevel.Debug) ? new Dictionary<string, MethodStats>(StringComparer.OrdinalIgnoreCase) : null);

        var results = new List<FileResult>();
        foreach (var job in config.Files)
        {
            state.FailureLogged = false;
            FileResult result;
            try
            {
                result = RunFile(job, config, configDir, inputBase, defaultOutputFolder, registry, ctx, state);
            }
            catch (Exception ex)
            {
                if (!state.FailureLogged)
                {
                    Log.FileFailed(logger, ex, Path.GetFileName(job.Input));
                }
                throw;
            }
            results.Add(result);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            long rows = 0, cells = 0, errors = 0, bytesIn = 0, bytesOut = 0;
            int written = 0, copied = 0, skipped = 0, missingColumns = 0;
            foreach (var r in results)
            {
                rows += r.Rows;
                cells += r.CellsAnonymized;
                errors += r.Errors;
                bytesIn += r.InputBytes;
                bytesOut += r.OutputBytes;
                missingColumns += r.MissingColumns.Count;
                if (r.Skipped is not null) { skipped++; }
                else { written++; if (r.Copied) { copied++; } }
            }

            var durationMs = Stopwatch.GetElapsedTime(runStart).TotalMilliseconds;
            var recordsPerSecond = durationMs > 0 ? Math.Round(rows / (durationMs / 1000d), 1) : 0d;
            Log.RunCompleted(logger, configName, written, copied, skipped, rows, cells, errors, missingColumns,
                bytesIn, bytesOut, durationMs, recordsPerSecond);
        }

        if (state.MethodStats is { } methodStats)
        {
            foreach (var (method, m) in methodStats.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                Log.MethodSummary(logger, method, m.Columns, m.Cells, m.Errors, Stopwatch.GetElapsedTime(0, m.Ticks).TotalMilliseconds);
            }
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
        RunState state)
    {
        long fileStart = Stopwatch.GetTimestamp();
        var inputPath = PathUtil.Resolve(job.Input, inputBase);
        var outputPath = ResolveOutputPath(job, config, configDir, defaultOutputFolder, inputPath);
        var inputName = Path.GetFileName(inputPath);
        var outputName = Path.GetFileName(outputPath);

        var result = new FileResult { Input = inputPath, Output = outputPath, Copied = job.Columns.Count == 0 };

        if (!File.Exists(inputPath))
        {
            var msg = $"input not found: {inputPath}";
            if (config.Run.OnError == AnonErrorPolicy.Abort)
            {
                throw new FileNotFoundException(msg, inputPath);
            }
            Log.InputMissing(state.Logger, inputName, inputPath);
            result.Skipped = msg;
            result.DurationMs = Stopwatch.GetElapsedTime(fileStart).TotalMilliseconds;
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
            File.Copy(inputPath, outputPath, overwrite: true);
            result.InputBytes = new FileInfo(inputPath).Length;
            result.OutputBytes = result.InputBytes;
            result.DurationMs = Stopwatch.GetElapsedTime(fileStart).TotalMilliseconds;
            Log.FileCopied(state.Logger, inputName, outputName, result.InputBytes, result.DurationMs);
            return result;
        }

        result.InputBytes = new FileInfo(inputPath).Length;
        StreamFile(job, config, inputPath, outputPath, registry, ctx, result, state, fileStart);
        result.OutputBytes = new FileInfo(outputPath).Length;
        result.DurationMs = Stopwatch.GetElapsedTime(fileStart).TotalMilliseconds;

        Log.FileAnonymized(state.Logger, inputName, outputName, result.Rows, result.CellsAnonymized,
            result.InputBytes, result.OutputBytes, result.DurationMs);
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
        RunState state,
        long openStart)
    {
        var logger = state.Logger;
        var fileName = Path.GetFileName(inputPath);
        var delimiter = DelimiterUtil.ResolveDelimiter(job.Delimiter);
        var quote = DelimiterUtil.ResolveQuote(job.Quote);

        using var reader = new CsvReader(inputPath, delimiter, quote, state.ReaderLogger);
        var compiled = Compile(job, reader, registry, result, logger, fileName);

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

        Log.OpenPhaseFinished(logger, "Open", fileName, compiled.Count, result.MissingColumns.Count,
            Stopwatch.GetElapsedTime(openStart).TotalMilliseconds);

        // Per-cell and per-row timing costs two timestamp reads each, so it only runs while Debug is enabled.
        bool debug = logger.IsEnabled(LogLevel.Debug);
        var stats = new ColumnStats[compiled.Count];
        long writeTicks = 0;
        long processStart = Stopwatch.GetTimestamp();

        foreach (var record in reader.ReadRecords())
        {
            ctx.NewRow();
            for (int i = 0; i < compiled.Count; i++)
            {
                var rule = compiled[i];
                if (rule.Index >= record.Length)
                {
                    if (debug) { stats[i].Skipped++; }
                    continue; // short row: nothing to anonymize at this position
                }

                var original = record[rule.Index];
                if (string.IsNullOrEmpty(original) && rule.WhenEmpty == ColumnWhenEmpty.Passthrough)
                {
                    if (debug) { stats[i].Skipped++; }
                    continue; // don't fabricate data where the source had none
                }

                long cellStart = debug ? Stopwatch.GetTimestamp() : 0;
                try
                {
                    record[rule.Index] = rule.Anonymizer.Generate(original, ctx, rule.Options);
                    result.CellsAnonymized++;
                    if (debug) { stats[i].Cells++; }
                }
                catch (Exception ex)
                {
                    // ex.Message is deliberately never logged: an anonymizer may quote the cell it choked on.
                    if (config.Run.OnError == AnonErrorPolicy.Abort)
                    {
                        Log.CellFailedAbort(logger, fileName, result.Rows + 1, rule.Name, rule.Method, ex.GetType().Name);
                        state.FailureLogged = true;
                        throw;
                    }
                    result.Errors++;
                    stats[i].Errors++;
                    if (result.Errors <= MaxCellErrorExamples)
                    {
                        Log.CellFailedExample(logger, fileName, result.Rows + 1, rule.Name, rule.Method, ex.GetType().Name);
                    }
                }
                if (debug) { stats[i].Ticks += Stopwatch.GetTimestamp() - cellStart; }
            }

            long writeStart = debug ? Stopwatch.GetTimestamp() : 0;
            sb.Clear();
            CsvWriter.AppendRecord(sb, record, delimiter, quote);
            writer.Write(sb);
            writer.Write('\n');
            if (debug) { writeTicks += Stopwatch.GetTimestamp() - writeStart; }
            result.Rows++;
        }

        if (debug)
        {
            var processMs = Stopwatch.GetElapsedTime(processStart).TotalMilliseconds;
            long anonymizeTicks = 0;
            for (int i = 0; i < stats.Length; i++)
            {
                anonymizeTicks += stats[i].Ticks;
            }
            var anonymizeMs = Stopwatch.GetElapsedTime(0, anonymizeTicks).TotalMilliseconds;
            var writeMs = Stopwatch.GetElapsedTime(0, writeTicks).TotalMilliseconds;
            Log.ProcessPhaseFinished(logger, "Process", fileName, result.Rows, result.CellsAnonymized, processMs,
                Math.Max(0, processMs - anonymizeMs - writeMs), anonymizeMs, writeMs);

            for (int i = 0; i < compiled.Count; i++)
            {
                var rule = compiled[i];
                var s = stats[i];
                Log.ColumnSummary(logger, rule.Name, rule.Method, fileName, s.Cells, s.Skipped, s.Errors,
                    Stopwatch.GetElapsedTime(0, s.Ticks).TotalMilliseconds);

                if (state.MethodStats is { } methodStats)
                {
                    if (!methodStats.TryGetValue(rule.Method, out var m))
                    {
                        m = new MethodStats();
                        methodStats[rule.Method] = m;
                    }
                    m.Columns++;
                    m.Cells += s.Cells;
                    m.Errors += s.Errors;
                    m.Ticks += s.Ticks;
                }
            }
        }

        if (result.Errors > 0 && logger.IsEnabled(LogLevel.Warning))
        {
            var columns = new List<string>();
            for (int i = 0; i < compiled.Count; i++)
            {
                if (stats[i].Errors > 0)
                {
                    columns.Add($"{compiled[i].Name} ({stats[i].Errors})");
                }
            }
            Log.CellsLeftUnchanged(logger, result.Errors, fileName, string.Join(", ", columns));
        }

        long flushStart = Stopwatch.GetTimestamp();
        writer.Flush();
        outStream.Flush();
        Log.WritePhaseFinished(logger, "Write", fileName, outStream.Length, Stopwatch.GetElapsedTime(flushStart).TotalMilliseconds);
    }

    private static List<CompiledRule> Compile(
        FileJobConfig job, CsvReader reader, AnonymizerRegistry registry, FileResult result, ILogger logger, string fileName)
    {
        var compiled = new List<CompiledRule>(job.Columns.Count);
        foreach (var col in job.Columns)
        {
            var index = reader.IndexOf(col.Name);
            if (index < 0)
            {
                result.MissingColumns.Add(col.Name);
                continue;
            }

            var anonymizer = registry.Get(col.Method); // throws early on an unknown method
            compiled.Add(new CompiledRule(index, col.Name, anonymizer.Key, anonymizer, new AnonOptions(col.Options), col.WhenEmpty));
        }

        if (result.MissingColumns.Count > 0 && logger.IsEnabled(LogLevel.Warning))
        {
            Log.ColumnsMissing(logger, result.MissingColumns.Count, fileName, string.Join(", ", result.MissingColumns));
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

    /// <summary>
    /// Source-generated log events, EventIds 2000-2099 (bleak.Data.InfiniteDataLoader uses 1000-1499).
    /// None of them takes a cell value: only counts, sizes, durations, file/config names and column names.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
            Message = "Anonymization run started: {ConfigName}, {JobCount} file jobs")]
        public static partial void RunStarted(ILogger logger, string configName, int jobCount);

        [LoggerMessage(EventId = 2001, Level = LogLevel.Debug,
            Message = "Anonymization run settings: input folder {InputFolder}, output folder {OutputFolder}, onError {OnError}, seeded {Seeded}")]
        public static partial void RunSettings(ILogger logger, string inputFolder, string outputFolder, AnonErrorPolicy onError, bool seeded);

        [LoggerMessage(EventId = 2002, Level = LogLevel.Information,
            Message = "Anonymization run completed: {ConfigName}, {FileCount} files written ({CopiedCount} copied), {SkippedCount} skipped, "
                + "{RowCount} rows, {CellCount} cells anonymized, {ErrorCount} bad cells, {MissingColumnCount} missing columns, "
                + "{ByteCount} bytes in, {OutputByteCount} bytes out, {DurationMs} ms, {RecordsPerSecond} rec/s")]
        public static partial void RunCompleted(ILogger logger, string configName, int fileCount, int copiedCount, int skippedCount,
            long rowCount, long cellCount, long errorCount, int missingColumnCount, long byteCount, long outputByteCount,
            double durationMs, double recordsPerSecond);

        [LoggerMessage(EventId = 2003, Level = LogLevel.Debug,
            Message = "Anonymizer method {Method}: {ColumnCount} column rules, {CellCount} cells anonymized, {ErrorCount} errors, {DurationMs} ms")]
        public static partial void MethodSummary(ILogger logger, string method, int columnCount, long cellCount, long errorCount, double durationMs);

        [LoggerMessage(EventId = 2010, Level = LogLevel.Information,
            Message = "Anonymized {FileName} -> {OutputName}: {RowCount} rows, {CellCount} cells, {ByteCount} bytes in, {OutputByteCount} bytes out, {DurationMs} ms")]
        public static partial void FileAnonymized(ILogger logger, string fileName, string outputName, long rowCount, long cellCount,
            long byteCount, long outputByteCount, double durationMs);

        [LoggerMessage(EventId = 2011, Level = LogLevel.Information,
            Message = "Copied {FileName} -> {OutputName} (no PII columns): {ByteCount} bytes, {DurationMs} ms")]
        public static partial void FileCopied(ILogger logger, string fileName, string outputName, long byteCount, double durationMs);

        [LoggerMessage(EventId = 2020, Level = LogLevel.Debug,
            Message = "{Phase} phase finished for {FileName}: {ColumnCount} columns resolved, {MissingColumnCount} missing, {DurationMs} ms")]
        public static partial void OpenPhaseFinished(ILogger logger, string phase, string fileName, int columnCount, int missingColumnCount, double durationMs);

        [LoggerMessage(EventId = 2021, Level = LogLevel.Debug,
            Message = "{Phase} phase finished for {FileName}: {RowCount} rows, {CellCount} cells, {DurationMs} ms "
                + "(read {ReadMs} ms, anonymize {AnonymizeMs} ms, format+write {WriteMs} ms)")]
        public static partial void ProcessPhaseFinished(ILogger logger, string phase, string fileName, long rowCount, long cellCount,
            double durationMs, double readMs, double anonymizeMs, double writeMs);

        [LoggerMessage(EventId = 2022, Level = LogLevel.Debug,
            Message = "{Phase} phase finished for {FileName}: {ByteCount} bytes flushed, {DurationMs} ms")]
        public static partial void WritePhaseFinished(ILogger logger, string phase, string fileName, long byteCount, double durationMs);

        [LoggerMessage(EventId = 2023, Level = LogLevel.Debug,
            Message = "Column {ColumnName} ({Method}) in {FileName}: {CellCount} cells anonymized, {SkippedCount} empty or absent, {ErrorCount} errors, {DurationMs} ms")]
        public static partial void ColumnSummary(ILogger logger, string columnName, string method, string fileName,
            long cellCount, long skippedCount, long errorCount, double durationMs);

        [LoggerMessage(EventId = 2030, Level = LogLevel.Warning,
            Message = "Input file not found, skipped: {FileName} ({FilePath})")]
        public static partial void InputMissing(ILogger logger, string fileName, string filePath);

        [LoggerMessage(EventId = 2031, Level = LogLevel.Warning,
            Message = "{MissingColumnCount} configured columns not found in the header of {FileName} and skipped: {ColumnNames}")]
        public static partial void ColumnsMissing(ILogger logger, int missingColumnCount, string fileName, string columnNames);

        [LoggerMessage(EventId = 2032, Level = LogLevel.Warning,
            Message = "{ErrorCount} cells in {FileName} could not be anonymized and were left unchanged (onError=skip); per column: {ColumnNames}")]
        public static partial void CellsLeftUnchanged(ILogger logger, long errorCount, string fileName, string columnNames);

        [LoggerMessage(EventId = 2033, Level = LogLevel.Debug,
            Message = "Cell not anonymized in {FileName}: data row {DataRow}, column {ColumnName} ({Method}) threw {ExceptionType}; left unchanged")]
        public static partial void CellFailedExample(ILogger logger, string fileName, long dataRow, string columnName, string method, string exceptionType);

        [LoggerMessage(EventId = 2040, Level = LogLevel.Error,
            Message = "Anonymization aborted in {FileName}: data row {DataRow}, column {ColumnName} ({Method}) threw {ExceptionType} (onError=abort)")]
        public static partial void CellFailedAbort(ILogger logger, string fileName, long dataRow, string columnName, string method, string exceptionType);

        [LoggerMessage(EventId = 2041, Level = LogLevel.Error,
            Message = "Anonymization failed for {FileName}")]
        public static partial void FileFailed(ILogger logger, Exception exception, string fileName);
    }
}
