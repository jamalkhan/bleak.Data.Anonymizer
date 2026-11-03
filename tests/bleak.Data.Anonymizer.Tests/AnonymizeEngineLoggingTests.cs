using System.Text.Json;
using bleak.Data.Anonymizer.Anonymizers;
using bleak.Data.Anonymizer.Config;
using bleak.Data.Anonymizer.Engine;
using Microsoft.Extensions.Logging;

namespace bleak.Data.Anonymizer.Tests;

[TestClass]
public class AnonymizeEngineLoggingTests
{
    // Distinctive sample values: if any of these shows up in a log message or property, PII leaked.
    private static readonly string[] SampleValues =
    [
        "Zebulon", "Quackenbush", "Zebulon Quackenbush", "zeb.quack@leakcheck.example", "8675309123", "1979-03-14",
    ];

    private string _dir = "";
    private string _inDir = "";
    private string _outDir = "";

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anon-log-tests", Guid.NewGuid().ToString("N"));
        _inDir = Path.Combine(_dir, "in");
        _outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_inDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static JsonElement Opt(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>An anonymizer that fails on purpose and quotes the original value in its exception message.</summary>
    private sealed class LeakyThrowingAnonymizer : IAnonymizer
    {
        public string Key => "leakyThrow";

        public string Generate(string original, AnonContext ctx, AnonOptions options)
            => throw new FormatException($"cannot anonymize '{original}'");
    }

    private (AnonConfig Config, AnonymizerRegistry Registry) RepresentativeRun()
    {
        File.WriteAllLines(Path.Combine(_inDir, "src_people.csv"),
        [
            "customer_key,first_name,last_name,full_name,email,phone,birth_date",
            "1,Zebulon,Quackenbush,Zebulon Quackenbush,zeb.quack@leakcheck.example,8675309123,1979-03-14",
            "2,Zebulon,Quackenbush,Zebulon Quackenbush,zeb.quack@leakcheck.example,8675309123,1979-03-14",
            "3,,,,,,",
        ]);
        File.WriteAllLines(Path.Combine(_inDir, "src_product.csv"), ["sku,description", "A1,Blue"]);

        var config = new AnonConfig
        {
            Name = "logging-test",
            Run = new AnonRunConfig { Seed = 7, OnError = AnonErrorPolicy.Skip },
            InputFolder = _inDir,
            OutputFolder = _outDir,
            Files =
            {
                new FileJobConfig
                {
                    Input = "src_people.csv",
                    Columns =
                    {
                        new ColumnRule { Name = "first_name", Method = "simpsonsName", Options = Opt("""{ "part": "first", "group": "p" }""") },
                        new ColumnRule { Name = "last_name", Method = "simpsonsName", Options = Opt("""{ "part": "last", "group": "p" }""") },
                        new ColumnRule { Name = "full_name", Method = "simpsonsName", Options = Opt("""{ "part": "full", "group": "p" }""") },
                        new ColumnRule { Name = "email", Method = "simpsonsEmail" },
                        new ColumnRule { Name = "phone", Method = "fakePhone" },
                        // Fails on every non-empty cell and puts the original value in ex.Message.
                        new ColumnRule { Name = "birth_date", Method = "leakyThrow" },
                        new ColumnRule { Name = "not_a_column", Method = "guid" },
                    },
                },
                new FileJobConfig { Input = "src_product.csv" },
                new FileJobConfig { Input = "src_absent.csv", Columns = { new ColumnRule { Name = "x", Method = "guid" } } },
            },
        };

        var registry = AnonymizerRegistry.CreateDefault().Register(new LeakyThrowingAnonymizer());
        return (config, registry);
    }

    [TestMethod]
    public void Run_LogsCompletedEvents_WithDurationAndCounters()
    {
        var (config, registry) = RepresentativeRun();
        using var factory = new CapturingLoggerFactory(LogLevel.Debug);

        var results = AnonymizeEngine.Run(config, _dir, null, registry, factory);
        var entries = factory.Entries.Where(e => e.Category == typeof(AnonymizeEngine).FullName).ToList();

        var started = entries.Single(e => e.EventId.Id == 2000);
        Assert.AreEqual(LogLevel.Information, started.Level);
        Assert.AreEqual("logging-test", started["ConfigName"]);
        Assert.AreEqual(3, started["JobCount"]);

        var completed = entries.Single(e => e.EventId.Id == 2002);
        Assert.AreEqual(LogLevel.Information, completed.Level);
        Assert.IsTrue(completed.Has("DurationMs"));
        Assert.IsTrue(completed.Has("RecordsPerSecond"));
        Assert.AreEqual(3L, completed["RowCount"]);
        Assert.AreEqual(2, completed["FileCount"]);
        Assert.AreEqual(1, completed["SkippedCount"]);
        Assert.AreEqual(2L, completed["ErrorCount"]);
        Assert.AreEqual(results.Sum(r => r.CellsAnonymized), completed["CellCount"]);

        var fileDone = entries.Single(e => e.EventId.Id == 2010);
        Assert.AreEqual(LogLevel.Information, fileDone.Level);
        Assert.AreEqual("src_people.csv", fileDone["FileName"]);
        Assert.AreEqual(3L, fileDone["RowCount"]);
        Assert.IsTrue((long)fileDone["ByteCount"]! > 0);
        Assert.IsTrue((long)fileDone["OutputByteCount"]! > 0);
        Assert.IsTrue(fileDone.Has("DurationMs"));

        Assert.AreEqual(1, entries.Count(e => e.EventId.Id == 2011), "byte-copied file is logged");

        // Warnings are aggregates: one per problem per file.
        Assert.AreEqual(LogLevel.Warning, entries.Single(e => e.EventId.Id == 2030).Level, "missing input");
        Assert.AreEqual("not_a_column", entries.Single(e => e.EventId.Id == 2031)["ColumnNames"]);
        var badCells = entries.Single(e => e.EventId.Id == 2032);
        Assert.AreEqual(2L, badCells["ErrorCount"]);
        StringAssert.Contains((string)badCells["ColumnNames"]!, "birth_date");

        // Debug: phase timings and per-column / per-method aggregates.
        CollectionAssert.AreEquivalent(new[] { "Open", "Process", "Write" },
            entries.Where(e => e.EventId.Id is >= 2020 and <= 2022).Select(e => (string)e["Phase"]!).ToArray());
        Assert.AreEqual(6, entries.Count(e => e.EventId.Id == 2023), "one summary per resolved column");
        var nameMethod = entries.Single(e => e.EventId.Id == 2003 && (string)e["Method"]! == "simpsonsName");
        Assert.AreEqual(3, nameMethod["ColumnCount"]);
        Assert.AreEqual(6L, nameMethod["CellCount"]);
    }

    [TestMethod]
    public void Run_NeverLogsCellValues_AtAnyLevel()
    {
        var (config, registry) = RepresentativeRun();
        using var factory = new CapturingLoggerFactory(LogLevel.Trace);

        AnonymizeEngine.Run(config, _dir, null, registry, factory);

        // The anonymized values written for the first data row must not leak either.
        var anonymizedRow = File.ReadAllLines(Path.Combine(_outDir, "anonymized_people.csv"))[1].Split(',');
        var forbidden = SampleValues.Concat(anonymizedRow.Skip(1).Take(5)).Where(v => v.Length > 0).ToArray();

        var entries = factory.Entries; // every category, CsvReader included
        Assert.IsTrue(entries.Count > 10, "sanity: logging actually happened");
        Assert.IsTrue(entries.Any(e => e.EventId.Id == 2033), "the leaky anonymizer's failure was reported");
        foreach (var entry in entries)
        {
            var text = entry.AllText();
            foreach (var value in forbidden)
            {
                Assert.IsFalse(text.Contains(value, StringComparison.OrdinalIgnoreCase),
                    $"cell value '{value}' leaked into {entry.Level} event {entry.EventId.Id} ({entry.Category}): {entry.Message}");
            }
        }
    }

    [TestMethod]
    public void Run_CellFailureUnderAbort_LogsErrorWithoutExceptionMessage()
    {
        var (config, registry) = RepresentativeRun();
        config.Run.OnError = AnonErrorPolicy.Abort;
        config.Files.RemoveAt(2); // the missing input would abort first
        using var factory = new CapturingLoggerFactory(LogLevel.Trace);

        Assert.ThrowsExactly<FormatException>(() => AnonymizeEngine.Run(config, _dir, null, registry, factory));

        var error = factory.Entries.Single(e => e.Level == LogLevel.Error);
        Assert.AreEqual(2040, error.EventId.Id);
        Assert.IsNull(error.Exception, "the exception (whose message quotes the cell) is not attached");
        Assert.IsFalse(error.AllText().Contains("1979-03-14"));
    }
}
