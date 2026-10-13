using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using bleak.Data.Anonymizer.Anonymizers;

namespace bleak.Data.Anonymizer.Tests;

[TestClass]
public class AnonymizerTests
{
    private static AnonContext Ctx(int seed = 1234, params string[] domains)
        => new(new Random(seed), domains.Length > 0 ? domains : new[] { "springfield.example" });

    private static AnonOptions Opt(string json)
        => new(JsonDocument.Parse(json).RootElement.Clone());

    private static IAnonymizer Get(string key) => AnonymizerRegistry.CreateDefault().Get(key);

    [TestMethod]
    public void SimpsonsName_FirstLastFull_AreCoherentWithinRowViaGroup()
    {
        var ctx = Ctx();
        var name = Get("simpsonsName");

        var first = name.Generate("", ctx, Opt("""{ "part": "first", "group": "p" }"""));
        var last = name.Generate("", ctx, Opt("""{ "part": "last",  "group": "p" }"""));
        var full = name.Generate("", ctx, Opt("""{ "part": "full",  "group": "p" }"""));

        Assert.AreEqual($"{first} {last}", full, "first/last/full must describe one person within the row");
    }

    [TestMethod]
    public void SimpsonsName_NewRow_PicksAFreshPersona()
    {
        var ctx = Ctx();
        var name = Get("simpsonsName");
        var full1 = name.Generate("", ctx, Opt("""{ "part": "full", "group": "p" }"""));

        // Same group without a NewRow() should stay stable...
        var full1Again = name.Generate("", ctx, Opt("""{ "part": "full", "group": "p" }"""));
        Assert.AreEqual(full1, full1Again);

        // ...and the cache resets per row.
        ctx.NewRow();
        var full2 = name.Generate("", ctx, Opt("""{ "part": "full", "group": "p" }"""));
        Assert.IsFalse(string.IsNullOrWhiteSpace(full2));
    }

    [TestMethod]
    public void SimpsonsName_IsSourcedFromKnownCharacters()
    {
        var ctx = Ctx();
        var name = Get("simpsonsName");
        var lastNames = SimpsonsData.Characters.Select(c => c.Last).ToHashSet();

        for (var i = 0; i < 200; i++)
        {
            ctx.NewRow();
            var last = name.Generate("", ctx, Opt("""{ "part": "last" }"""));
            Assert.IsTrue(lastNames.Contains(last), $"'{last}' is not a known Simpsons surname");
        }
    }

    [TestMethod]
    public void SimpsonsEmail_UsesConfiguredDomain_AndCleanLocalPart()
    {
        var ctx = Ctx();
        var email = Get("simpsonsEmail").Generate("real@example.com", ctx, Opt("""{ "domain": "springfield.example" }"""));

        StringAssert.EndsWith(email, "@springfield.example");
        var local = email.Split('@')[0];
        Assert.IsTrue(Regex.IsMatch(local, "^[a-z0-9.]+$"), $"local part '{local}' should be lowercase alnum/dot");
        Assert.IsFalse(local.Contains(' '));
    }

    [TestMethod]
    public void SimpsonsEmail_DefaultsToRunDomainPool()
    {
        var ctx = Ctx(seed: 7, "acme.example");
        var email = Get("simpsonsEmail").Generate("x@y.com", ctx, AnonOptions.Empty);
        StringAssert.EndsWith(email, "@acme.example");
    }

    [TestMethod]
    public void FakePhone_UsesReservedFictionalRange()
    {
        var ctx = Ctx();
        var phone = Get("fakePhone");
        for (var i = 0; i < 100; i++)
        {
            var digits = phone.Generate("5555550100", ctx, AnonOptions.Empty);
            Assert.IsTrue(Regex.IsMatch(digits, "^55555501[0-9]{2}$"), $"'{digits}' not in 555-01XX range");
        }

        var dashed = phone.Generate("", ctx, Opt("""{ "format": "dashes" }"""));
        Assert.IsTrue(Regex.IsMatch(dashed, "^555-555-01[0-9]{2}$"), dashed);
    }

    [TestMethod]
    public void Last4_ProducesZeroPaddedDigits()
    {
        var ctx = Ctx();
        var last4 = Get("last4");
        for (var i = 0; i < 100; i++)
        {
            var v = last4.Generate("1234", ctx, AnonOptions.Empty);
            Assert.AreEqual(4, v.Length);
            Assert.IsTrue(v.All(char.IsDigit), v);
        }
    }

    [TestMethod]
    public void RandomHex_MatchesRequestedLengthAndCharset()
    {
        var ctx = Ctx();
        var hex = Get("randomHex").Generate("deadbeef", ctx, Opt("""{ "length": 64 }"""));
        Assert.AreEqual(64, hex.Length);
        Assert.IsTrue(Regex.IsMatch(hex, "^[0-9a-f]{64}$"), hex);
    }

    [TestMethod]
    public void FakeDate_ParsesInFormatAndFallsInRange()
    {
        var ctx = Ctx();
        var date = Get("fakeDate");
        for (var i = 0; i < 100; i++)
        {
            var v = date.Generate("1980-05-04", ctx, Opt("""{ "format": "yyyy-MM-dd", "minYear": 1950, "maxYear": 2000 }"""));
            Assert.IsTrue(DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d), v);
            Assert.IsTrue(d.Year is >= 1950 and <= 2000, v);
        }
    }

    [TestMethod]
    public void Guid_RespectsFormat()
    {
        var ctx = Ctx();
        var guid = Get("guid");
        var d = guid.Generate("00000000-0000-0000-0000-000000000000", ctx, AnonOptions.Empty);
        Assert.IsTrue(Guid.TryParse(d, out _));
        Assert.IsTrue(d.Contains('-') && d == d.ToLowerInvariant());

        var n = guid.Generate("", ctx, Opt("""{ "format": "n" }"""));
        Assert.AreEqual(32, n.Length);
        Assert.IsFalse(n.Contains('-'));
    }

    [TestMethod]
    public void FakeAddress_PartsAreCoherent_AndLine2Line3Blank()
    {
        var ctx = Ctx();
        var addr = Get("fakeAddress");
        var line1 = addr.Generate("123 MAIN ST", ctx, Opt("""{ "part": "line1", "group": "a" }"""));
        var city = addr.Generate("ANYTOWN", ctx, Opt("""{ "part": "city", "group": "a" }"""));
        var line2 = addr.Generate("APT 5", ctx, Opt("""{ "part": "line2", "group": "a" }"""));

        Assert.IsTrue(Regex.IsMatch(line1, @"^\d+ .+"), line1);
        Assert.IsTrue(SimpsonsData.Cities.Contains(city), city);
        Assert.AreEqual(string.Empty, line2);
    }

    [TestMethod]
    public void Constant_ReturnsConfiguredValue()
    {
        var ctx = Ctx();
        Assert.AreEqual("555", Get("constant").Generate("917", ctx, Opt("""{ "value": "555" }""")));
    }

    [TestMethod]
    public void Registry_UnknownMethod_Throws()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(() => AnonymizerRegistry.CreateDefault().Get("nope"));
    }
}
