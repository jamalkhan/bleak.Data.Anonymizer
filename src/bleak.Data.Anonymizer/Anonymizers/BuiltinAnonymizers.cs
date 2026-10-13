using System.Globalization;

namespace bleak.Data.Anonymizer.Anonymizers;

/// <summary>
/// The built-in anonymizers. Each replaces a PII cell with an obviously-fake value; name/username/email
/// fakes are sourced from recurring Simpsons characters (see <see cref="SimpsonsData"/>). All are random
/// per occurrence; columns that must agree within a row (first/last/full name, address parts) coordinate
/// through a shared <c>group</c> via <see cref="AnonContext.GetRowValue{T}"/>.
/// </summary>
internal static class BuiltinAnonymizers
{
    public static IEnumerable<IAnonymizer> All => new IAnonymizer[]
    {
        new SimpsonsNameAnonymizer(),
        new SimpsonsEmailAnonymizer(),
        new FakePhoneAnonymizer(),
        new Last4Anonymizer(),
        new RandomHexAnonymizer(),
        new FakeDateAnonymizer(),
        new GuidAnonymizer(),
        new FakeAddressAnonymizer(),
        new ConstantAnonymizer(),
    };
}

/// <summary>first / middle / last / full name from a Simpsons character. Use the same <c>group</c> across
/// a row's name columns to keep them describing one person.</summary>
internal sealed class SimpsonsNameAnonymizer : IAnonymizer
{
    public string Key => "simpsonsName";

    public string Generate(string original, AnonContext ctx, AnonOptions options)
    {
        var group = options.GetString("group");
        var persona = group is null
            ? SimpsonsData.RandomPersona(ctx.Rng)
            : ctx.GetRowValue("name:" + group, SimpsonsData.RandomPersona);

        return options.GetStringOr("part", "full").ToLowerInvariant() switch
        {
            "first" => persona.First,
            "middle" => persona.Middle,
            "last" => persona.Last,
            _ => persona.Full,
        };
    }
}

/// <summary>A Springfield-themed e-mail address (e.g. bart.simpson47@springfield.example). Share a
/// <c>group</c> with name columns to make the local-part match the row's person.</summary>
internal sealed class SimpsonsEmailAnonymizer : IAnonymizer
{
    public string Key => "simpsonsEmail";

    public string Generate(string original, AnonContext ctx, AnonOptions options)
    {
        var group = options.GetString("group");
        var persona = group is null
            ? SimpsonsData.RandomPersona(ctx.Rng)
            : ctx.GetRowValue("name:" + group, SimpsonsData.RandomPersona);

        var local = SimpsonsData.EmailLocalPart(persona, ctx.Rng);
        var domain = options.GetString("domain") ?? ctx.EmailDomains[ctx.Rng.Next(ctx.EmailDomains.Count)];
        return $"{local}@{domain}";
    }
}

/// <summary>A phone number from the reserved-for-fiction 555-01XX range, so it is obviously fake.
/// <c>format</c>: <c>digits</c> (default, "55555501NN") or <c>dashes</c> ("555-555-01NN").</summary>
internal sealed class FakePhoneAnonymizer : IAnonymizer
{
    public string Key => "fakePhone";

    public string Generate(string original, AnonContext ctx, AnonOptions options)
    {
        var line = 100 + ctx.Rng.Next(0, 100); // 0100..0199 in the reserved fictional block
        var dashes = string.Equals(options.GetString("format"), "dashes", StringComparison.OrdinalIgnoreCase);
        return dashes ? $"555-555-0{line}" : $"5555550{line}";
    }
}

/// <summary>A random N-digit numeric string (default 4), zero-padded — e.g. a card/phone "last 4".</summary>
internal sealed class Last4Anonymizer : IAnonymizer
{
    public string Key => "last4";

    public string Generate(string original, AnonContext ctx, AnonOptions options)
    {
        var digits = Math.Clamp(options.GetIntOr("digits", 4), 1, 18);
        var max = (int)Math.Pow(10, Math.Min(digits, 9));
        return ctx.Rng.Next(0, max).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}

/// <summary>A random lowercase hex string of <c>length</c> chars (default 64, i.e. a SHA-256-shaped token).
/// Replaces hash columns so they can't fingerprint the original value.</summary>
internal sealed class RandomHexAnonymizer : IAnonymizer
{
    private const string Hex = "0123456789abcdef";
    public string Key => "randomHex";

    public string Generate(string original, AnonContext ctx, AnonOptions options)
    {
        var length = Math.Clamp(options.GetIntOr("length", 64), 1, 1024);
        return string.Create(length, ctx.Rng, static (span, rng) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Hex[rng.Next(16)];
            }
        });
    }
}

/// <summary>A random date formatted with <c>format</c> (default <c>yyyy-MM-dd</c>) between <c>minYear</c>
/// and <c>maxYear</c> (default 1940..2005) — used for birth/other sensitive dates.</summary>
internal sealed class FakeDateAnonymizer : IAnonymizer
{
    public string Key => "fakeDate";

    public string Generate(string original, AnonContext ctx, AnonOptions options)
    {
        var minYear = options.GetIntOr("minYear", 1940);
        var maxYear = Math.Max(minYear, options.GetIntOr("maxYear", 2005));
        var start = new DateTime(minYear, 1, 1);
        var end = new DateTime(maxYear, 12, 31);
        var days = (end - start).Days;
        var date = start.AddDays(ctx.Rng.Next(0, days + 1));
        return date.ToString(options.GetStringOr("format", "yyyy-MM-dd"), CultureInfo.InvariantCulture);
    }
}

/// <summary>A fresh random GUID. <c>format</c>: <c>d</c> (default, lowercase dashed), <c>n</c> (32 hex, no
/// dashes), or <c>upper</c> (dashed uppercase).</summary>
internal sealed class GuidAnonymizer : IAnonymizer
{
    public string Key => "guid";

    public string Generate(string original, AnonContext ctx, AnonOptions options)
    {
        var bytes = new byte[16];
        ctx.Rng.NextBytes(bytes);
        var guid = new Guid(bytes);
        return options.GetStringOr("format", "d").ToLowerInvariant() switch
        {
            "n" => guid.ToString("N"),
            "upper" => guid.ToString("D").ToUpperInvariant(),
            _ => guid.ToString("D"),
        };
    }
}

/// <summary>A coherent fake mailing address. <c>part</c>: line1/line2/line3/city/state/province/postal/
/// postalExt. Share a <c>group</c> across a row's address columns so they describe one address.</summary>
internal sealed class FakeAddressAnonymizer : IAnonymizer
{
    public string Key => "fakeAddress";

    public string Generate(string original, AnonContext ctx, AnonOptions options)
    {
        var group = options.GetString("group");
        var addr = group is null
            ? SimpsonsData.RandomAddress(ctx.Rng)
            : ctx.GetRowValue("addr:" + group, SimpsonsData.RandomAddress);

        return options.GetStringOr("part", "line1").ToLowerInvariant() switch
        {
            "line1" or "street" => addr.Line1,
            "city" => addr.City,
            "state" => addr.State,
            "province" => addr.Province,
            "postal" or "postalcode" or "zip" => addr.Postal,
            "postalext" => addr.PostalExt,
            // line2/line3 (apt/suite) have no analog in a simple fake address — blank them out.
            "line2" or "line3" => string.Empty,
            _ => addr.Line1,
        };
    }
}

/// <summary>Replaces the cell with a fixed literal (<c>value</c>, default empty). Handy for setting a fake
/// e-mail domain column or a constant area code.</summary>
internal sealed class ConstantAnonymizer : IAnonymizer
{
    public string Key => "constant";

    public string Generate(string original, AnonContext ctx, AnonOptions options)
        => options.GetStringOr("value", string.Empty);
}
