using System.Text.Json;

namespace bleak.Data.Anonymizer.Config;

/// <summary>
/// The deserialized anonymization configuration: run-wide settings plus one <see cref="FileJobConfig"/>
/// per input file. This is a plain DTO mirror of the JSON; it is validated and compiled into a runtime
/// plan by the engine. Per-column <see cref="ColumnRule.Options"/> are kept as raw <see cref="JsonElement"/>
/// and interpreted by each anonymizer at run time.
/// </summary>
public sealed class AnonConfig
{
    public int Version { get; set; } = 1;

    public AnonRunConfig Run { get; set; } = new();

    /// <summary>
    /// Base folder for relative <see cref="FileJobConfig.Input"/> paths, resolved relative to the config
    /// file's folder (absolute also allowed; leading <c>~</c> expands to home). When omitted, inputs
    /// resolve directly against the config folder.
    /// </summary>
    public string? InputFolder { get; set; }

    /// <summary>
    /// Default output folder for every file, resolved relative to the config file's folder. A file may
    /// override the folder by giving <see cref="FileJobConfig.Output"/> a rooted path. When omitted here,
    /// the command prompts for one. This must never be the same directory as the input — originals are
    /// never modified or deleted.
    /// </summary>
    public string? OutputFolder { get; set; }

    /// <summary>One job per input file.</summary>
    public List<FileJobConfig> Files { get; set; } = new();
}

/// <summary>Run-wide settings shared by every file job.</summary>
public sealed class AnonRunConfig
{
    public AnonErrorPolicy OnError { get; set; } = AnonErrorPolicy.Skip;

    /// <summary>
    /// Optional RNG seed. When set, a run is reproducible (same fakes every time) — useful for tests.
    /// When null, fakes are freshly random each run. Values are still random per occurrence either way.
    /// </summary>
    public int? Seed { get; set; }

    /// <summary>Filename prefix stripped from the input when deriving the output name. Default <c>src_</c>.</summary>
    public string InputPrefix { get; set; } = "src_";

    /// <summary>Prefix used for the derived output name in place of <see cref="InputPrefix"/>. Default <c>anonymized_</c>.</summary>
    public string OutputPrefix { get; set; } = "anonymized_";

    /// <summary>
    /// Pool of obviously-fake e-mail domains the <c>simpsonsEmail</c> anonymizer draws from (Springfield-themed
    /// by default). A column can override with its own <c>domain</c> option.
    /// </summary>
    public List<string> EmailDomains { get; set; } = new() { "springfield.example" };
}

/// <summary>One input file: how to read it and which columns to anonymize.</summary>
public sealed class FileJobConfig
{
    /// <summary>Input file path, resolved relative to <see cref="AnonConfig.InputFolder"/> (or the config folder).</summary>
    public string Input { get; set; } = "";

    /// <summary>
    /// Optional explicit output. A bare filename lands in the run's output folder; a rooted path is used
    /// verbatim. When omitted, the output name is derived by swapping <c>InputPrefix</c> for <c>OutputPrefix</c>.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>Delimiter shorthand: <c>csv</c>/<c>comma</c>, <c>tsv</c>/<c>tab</c>, <c>pipe</c>, <c>semicolon</c>, or a single char.</summary>
    public string Delimiter { get; set; } = "csv";

    public string Quote { get; set; } = "\"";

    public bool HasHeader { get; set; } = true;

    /// <summary>
    /// The columns to anonymize. Every other column (keys included) passes through unchanged. When this
    /// list is empty the file is copied byte-for-byte to the renamed output (fast path for huge PII-free files).
    /// </summary>
    public List<ColumnRule> Columns { get; set; } = new();
}

/// <summary>A single "anonymize column X with method Y" rule.</summary>
public sealed class ColumnRule
{
    /// <summary>The column header name (case-insensitive).</summary>
    public string Name { get; set; } = "";

    /// <summary>The anonymizer key (e.g. <c>simpsonsName</c>, <c>simpsonsEmail</c>, <c>fakePhone</c>).</summary>
    public string Method { get; set; } = "";

    /// <summary>Free-form per-method options (e.g. <c>part</c>, <c>group</c>, <c>format</c>, <c>length</c>, <c>value</c>).</summary>
    public JsonElement? Options { get; set; }

    /// <summary>What to do when the source cell is empty. Default: leave it empty.</summary>
    public ColumnWhenEmpty WhenEmpty { get; set; } = ColumnWhenEmpty.Passthrough;
}
