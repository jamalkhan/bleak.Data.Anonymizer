namespace bleak.Data.Anonymizer.Anonymizers;

/// <summary>
/// Produces a fake replacement for a single PII cell. Implementations must be stateless and thread-safe
/// with respect to their own fields — all per-run/per-row state lives in <see cref="AnonContext"/>.
/// </summary>
public interface IAnonymizer
{
    /// <summary>The config key that selects this anonymizer (e.g. <c>simpsonsEmail</c>).</summary>
    string Key { get; }

    /// <summary>
    /// Returns the anonymized value for <paramref name="original"/>. The engine only calls this for
    /// non-empty cells unless the column opted into generating over empties.
    /// </summary>
    string Generate(string original, AnonContext ctx, AnonOptions options);
}
