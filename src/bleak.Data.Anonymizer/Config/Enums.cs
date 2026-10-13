namespace bleak.Data.Anonymizer.Config;

/// <summary>What the engine does when anonymizing a single cell raises an exception.</summary>
public enum AnonErrorPolicy
{
    /// <summary>Log the failure, leave the offending cell unchanged, and keep going.</summary>
    Skip,

    /// <summary>Stop the whole run immediately.</summary>
    Abort,
}

/// <summary>What to do when the original cell value is null/empty.</summary>
public enum ColumnWhenEmpty
{
    /// <summary>Leave an empty cell empty — do not fabricate a value where none existed. Default.</summary>
    Passthrough,

    /// <summary>Generate a fake value even when the source cell was empty.</summary>
    Generate,
}
