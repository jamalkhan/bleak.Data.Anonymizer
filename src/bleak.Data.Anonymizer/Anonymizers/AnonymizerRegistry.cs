namespace bleak.Data.Anonymizer.Anonymizers;

/// <summary>
/// Maps a config <c>method</c> key to its <see cref="IAnonymizer"/>. Pre-populated with the built-ins;
/// callers can <see cref="Register"/> their own before compiling a config. Mirrors the ETL engine's
/// <c>TransformRegistry</c> so the two engines extend the same way.
/// </summary>
public sealed class AnonymizerRegistry
{
    private readonly Dictionary<string, IAnonymizer> _byKey = new(StringComparer.OrdinalIgnoreCase);

    private AnonymizerRegistry() { }

    /// <summary>A fresh registry preloaded with every built-in anonymizer.</summary>
    public static AnonymizerRegistry CreateDefault()
    {
        var r = new AnonymizerRegistry();
        foreach (var a in BuiltinAnonymizers.All)
        {
            r.Register(a);
        }
        return r;
    }

    /// <summary>Adds or replaces the anonymizer registered under <see cref="IAnonymizer.Key"/>.</summary>
    public AnonymizerRegistry Register(IAnonymizer anonymizer)
    {
        _byKey[anonymizer.Key] = anonymizer;
        return this;
    }

    public bool TryGet(string key, out IAnonymizer anonymizer) => _byKey.TryGetValue(key, out anonymizer!);

    public IAnonymizer Get(string key)
        => TryGet(key, out var a)
            ? a
            : throw new KeyNotFoundException(
                $"Unknown anonymizer method '{key}'. Known methods: {string.Join(", ", _byKey.Keys.OrderBy(k => k))}.");
}
