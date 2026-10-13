namespace bleak.Data.Anonymizer.Engine;

/// <summary>The outcome of anonymizing (or copying) one input file.</summary>
public sealed class FileResult
{
    public required string Input { get; init; }
    public required string Output { get; init; }

    /// <summary>True when the file had no configured columns and was byte-copied to the renamed output.</summary>
    public bool Copied { get; init; }

    /// <summary>Data rows processed (header excluded). Zero for a raw copy.</summary>
    public long Rows { get; set; }

    /// <summary>Total cells replaced across all rows.</summary>
    public long CellsAnonymized { get; set; }

    /// <summary>Per-cell anonymizer errors that were tolerated (OnError=skip). Zero when clean.</summary>
    public long Errors { get; set; }

    /// <summary>Set when the file was skipped entirely (e.g. missing input under OnError=skip).</summary>
    public string? Skipped { get; set; }

    /// <summary>Configured column names that were not found in the file header (logged, not fatal).</summary>
    public List<string> MissingColumns { get; } = new();
}
