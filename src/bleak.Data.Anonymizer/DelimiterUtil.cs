namespace bleak.Data.Anonymizer;

/// <summary>Resolves the delimiter/quote shorthands used in a file job's config to actual chars.</summary>
public static class DelimiterUtil
{
    /// <summary>
    /// Accepts <c>csv</c>/<c>comma</c> (,), <c>tsv</c>/<c>tab</c> (\t), <c>pipe</c> (|), <c>semicolon</c> (;),
    /// or any single literal character.
    /// </summary>
    public static char ResolveDelimiter(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return ',';
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "csv" or "comma" => ',',
            "tsv" or "tab" or "\\t" => '\t',
            "pipe" => '|',
            "semicolon" => ';',
            _ when value.Length == 1 => value[0],
            _ => throw new ArgumentException($"Unrecognized delimiter '{value}'. Use csv|tsv|pipe|semicolon or a single char."),
        };
    }

    public static char ResolveQuote(string value)
        => string.IsNullOrEmpty(value) ? '"' : value[0];
}
