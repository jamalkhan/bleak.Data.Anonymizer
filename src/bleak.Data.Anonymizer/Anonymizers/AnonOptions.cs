using System.Globalization;
using System.Text.Json;

namespace bleak.Data.Anonymizer.Anonymizers;

/// <summary>
/// A thin, typed reader over a column rule's free-form <c>options</c> JSON object. Missing keys and a
/// missing options object both return the caller's default, so anonymizers can read options defensively.
/// </summary>
public sealed class AnonOptions
{
    public static readonly AnonOptions Empty = new(null);

    private readonly JsonElement? _e;

    public AnonOptions(JsonElement? e)
    {
        // Only object payloads expose named options; anything else behaves as "no options".
        _e = e is { ValueKind: JsonValueKind.Object } ? e : null;
    }

    public string? GetString(string name)
        => _e is { } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public string GetStringOr(string name, string fallback) => GetString(name) ?? fallback;

    public int GetIntOr(string name, int fallback)
    {
        if (_e is { } e && e.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            {
                return n;
            }
            if (v.ValueKind == JsonValueKind.String
                && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            {
                return s;
            }
        }
        return fallback;
    }

    public bool GetBoolOr(string name, bool fallback)
    {
        if (_e is { } e && e.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return fallback;
    }
}
