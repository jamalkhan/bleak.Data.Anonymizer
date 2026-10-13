using System.Text.Json;
using bleak.Data.Anonymizer.Config;
using bleak.Data.Anonymizer.Engine;

namespace bleak.Data.Anonymizer.Tests;

[TestClass]
public class AnonymizeEngineTests
{
    private string _dir = "";
    private string _inDir = "";
    private string _outDir = "";

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anon-tests", Guid.NewGuid().ToString("N"));
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

    private string WriteInput(string name, params string[] lines)
    {
        var path = Path.Combine(_inDir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private AnonConfig Config(FileJobConfig job) => new()
    {
        Run = new AnonRunConfig { Seed = 42 },
        InputFolder = _inDir,
        OutputFolder = _outDir,
        Files = { job },
    };

    [TestMethod]
    public void Run_AnonymizesPii_PreservesKeysAndUnlistedColumns_AndRenames()
    {
        var inputName = "src_people_20260101.csv";
        WriteInput(inputName,
            "customer_key,first_name,last_name,full_name,email,notes",
            "12345,Jane,Doe,Jane Doe,jane.doe@example.com,keepme");

        var job = new FileJobConfig
        {
            Input = inputName,
            Columns =
            {
                new ColumnRule { Name = "first_name", Method = "simpsonsName", Options = Opt("""{ "part": "first", "group": "p" }""") },
                new ColumnRule { Name = "last_name",  Method = "simpsonsName", Options = Opt("""{ "part": "last",  "group": "p" }""") },
                new ColumnRule { Name = "full_name",  Method = "simpsonsName", Options = Opt("""{ "part": "full",  "group": "p" }""") },
                new ColumnRule { Name = "email",      Method = "simpsonsEmail" },
            },
        };

        var results = AnonymizeEngine.Run(Config(job), _dir, null);
        Assert.AreEqual(1, results.Count);

        var outPath = Path.Combine(_outDir, "anonymized_people_20260101.csv");
        Assert.IsTrue(File.Exists(outPath), "renamed output should exist");

        var lines = File.ReadAllLines(outPath);
        Assert.AreEqual("customer_key,first_name,last_name,full_name,email,notes", lines[0], "header unchanged");

        var cells = lines[1].Split(',');
        Assert.AreEqual("12345", cells[0], "join key must be preserved");
        Assert.AreEqual("keepme", cells[5], "unlisted column must be preserved");
        Assert.AreNotEqual("Jane", cells[1], "first_name should be anonymized");
        Assert.AreEqual($"{cells[1]} {cells[2]}", cells[3], "full_name must agree with faked first/last");
        StringAssert.EndsWith(cells[4], "@springfield.example");
    }

    [TestMethod]
    public void Run_EmptyCell_StaysEmpty_ByDefault()
    {
        var inputName = "src_people_20260101.csv";
        WriteInput(inputName,
            "customer_key,first_name",
            "1,",
            "2,RealName");

        var job = new FileJobConfig
        {
            Input = inputName,
            Columns = { new ColumnRule { Name = "first_name", Method = "simpsonsName", Options = Opt("""{ "part": "first" }""") } },
        };

        AnonymizeEngine.Run(Config(job), _dir, null);

        var lines = File.ReadAllLines(Path.Combine(_outDir, "anonymized_people_20260101.csv"));
        Assert.AreEqual("1,", lines[1], "empty cell should not be fabricated");
        Assert.AreNotEqual("2,RealName", lines[2], "non-empty cell should be anonymized");
    }

    [TestMethod]
    public void Run_NoColumns_ByteCopiesFileVerbatim()
    {
        var inputName = "src_product_20260101.csv";
        var inPath = WriteInput(inputName,
            "sku,description",
            "A1,\"Blue, Large\"",
            "B2,Green");

        var job = new FileJobConfig { Input = inputName, Columns = { } };
        var results = AnonymizeEngine.Run(Config(job), _dir, null);

        Assert.IsTrue(results[0].Copied);
        var outPath = Path.Combine(_outDir, "anonymized_product_20260101.csv");
        CollectionAssert.AreEqual(File.ReadAllBytes(inPath), File.ReadAllBytes(outPath), "copy must be byte-identical");
    }

    [TestMethod]
    public void Run_LeavesOriginalUntouched()
    {
        var inputName = "src_people_20260101.csv";
        var inPath = WriteInput(inputName,
            "customer_key,first_name",
            "1,Jane");
        var before = File.ReadAllBytes(inPath);

        var job = new FileJobConfig
        {
            Input = inputName,
            Columns = { new ColumnRule { Name = "first_name", Method = "simpsonsName" } },
        };
        AnonymizeEngine.Run(Config(job), _dir, null);

        CollectionAssert.AreEqual(before, File.ReadAllBytes(inPath), "the source file must never be modified");
    }

    [TestMethod]
    public void Run_MissingConfiguredColumn_IsRecordedNotFatal()
    {
        var inputName = "src_people_20260101.csv";
        WriteInput(inputName, "customer_key,first_name", "1,Jane");

        var job = new FileJobConfig
        {
            Input = inputName,
            Columns = { new ColumnRule { Name = "does_not_exist", Method = "simpsonsName" } },
        };
        var results = AnonymizeEngine.Run(Config(job), _dir, null);

        CollectionAssert.Contains(results[0].MissingColumns, "does_not_exist");
        Assert.AreEqual(1, results[0].Rows);
    }

    [TestMethod]
    public void Run_RefusesToOverwriteSource()
    {
        var inputName = "src_people_20260101.csv";
        WriteInput(inputName, "customer_key,first_name", "1,Jane");

        // Force the output onto the exact input path.
        var job = new FileJobConfig
        {
            Input = inputName,
            Output = Path.Combine(_inDir, inputName),
            Columns = { new ColumnRule { Name = "first_name", Method = "simpsonsName" } },
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => AnonymizeEngine.Run(Config(job), _dir, null));
    }

    [TestMethod]
    public void Run_MissingInput_UnderSkip_IsReportedNotThrown()
    {
        var job = new FileJobConfig
        {
            Input = "src_absent.csv",
            Columns = { new ColumnRule { Name = "x", Method = "simpsonsName" } },
        };
        var cfg = Config(job);
        cfg.Run.OnError = AnonErrorPolicy.Skip;

        var results = AnonymizeEngine.Run(cfg, _dir, null);
        Assert.IsNotNull(results[0].Skipped);
    }

    [TestMethod]
    public void Run_SeededConfig_IsReproducible()
    {
        var inputName = "src_people_20260101.csv";
        WriteInput(inputName, "customer_key,email", "1,real@example.com", "2,other@example.com");
        var job = new FileJobConfig
        {
            Input = inputName,
            Columns = { new ColumnRule { Name = "email", Method = "simpsonsEmail" } },
        };

        AnonymizeEngine.Run(Config(job), _dir, null);
        var first = File.ReadAllLines(Path.Combine(_outDir, "anonymized_people_20260101.csv"));

        Directory.Delete(_outDir, recursive: true);
        AnonymizeEngine.Run(Config(job), _dir, null);
        var second = File.ReadAllLines(Path.Combine(_outDir, "anonymized_people_20260101.csv"));

        CollectionAssert.AreEqual(first, second, "same seed should produce identical output");
    }
}
