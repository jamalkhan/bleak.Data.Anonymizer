namespace bleak.Data.Anonymizer.Anonymizers;

/// <summary>
/// Per-run randomness plus per-row scratch state shared across the anonymizers running on one record.
///
/// Fakes are <b>random per occurrence</b> (each cell is independently randomized), but several columns in
/// the <i>same row</i> sometimes need to agree — e.g. <c>first_name</c>/<c>last_name</c>/<c>full_name</c>
/// must describe one person. Anonymizers coordinate that through <see cref="GetRowValue{T}"/>, keyed by a
/// caller-chosen group id. The cache is cleared between rows by <see cref="NewRow"/>.
/// </summary>
public sealed class AnonContext
{
    private readonly Dictionary<string, object> _row = new(StringComparer.Ordinal);

    public AnonContext(Random rng, IReadOnlyList<string> emailDomains)
    {
        Rng = rng;
        EmailDomains = emailDomains.Count > 0 ? emailDomains : new[] { "springfield.example" };
    }

    /// <summary>The run's RNG. Seeded runs are reproducible; unseeded runs vary each time.</summary>
    public Random Rng { get; }

    /// <summary>The obviously-fake e-mail domains <c>simpsonsEmail</c> draws from when a column gives no <c>domain</c>.</summary>
    public IReadOnlyList<string> EmailDomains { get; }

    /// <summary>Clears the per-row shared state. Called by the engine once per record.</summary>
    public void NewRow() => _row.Clear();

    /// <summary>
    /// Returns the value stored under <paramref name="key"/> for the current row, creating it with
    /// <paramref name="factory"/> on first use. This is how columns in the same row share a coherent
    /// persona/address (same key ⇒ same generated value within the row).
    /// </summary>
    public T GetRowValue<T>(string key, Func<Random, T> factory)
    {
        if (_row.TryGetValue(key, out var existing))
        {
            return (T)existing;
        }

        var created = factory(Rng)!;
        _row[key] = created;
        return created;
    }
}
