using System.Text.Json;
using System.Text.Json.Serialization;

namespace bleak.Data.Anonymizer.Config;

/// <summary>
/// Loads an <see cref="AnonConfig"/> from JSON. Comments (<c>//</c>, <c>/* */</c>) and trailing commas are
/// allowed so the human/AI-authored <c>.anonconfig</c> can be annotated (jsonc-style). Mirrors the ETL
/// config loader so both engines behave the same way.
/// </summary>
public static class AnonConfigLoader
{
    public static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return o;
    }

    public static AnonConfig FromJson(string json)
        => JsonSerializer.Deserialize<AnonConfig>(json, Options)
           ?? throw new InvalidOperationException("Config deserialized to null.");

    public static AnonConfig FromFile(string path)
        => FromJson(File.ReadAllText(path));
}
