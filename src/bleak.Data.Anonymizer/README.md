# bleak.Data.Anonymizer

Config-driven PII anonymizer for large delimited files: fake names, emails, phones, dates, addresses, GUIDs and more.

Describe in a JSON `.anonconfig` which columns of which delimited files to anonymize and with which
method (fake names, e-mail addresses, phone numbers, dates, addresses, hex tokens, GUIDs, constants, and so on).
Files are processed as streams, so inputs can be larger than RAM.

See [ANON-CONFIG.md](https://github.com/jamalkhan/bleak.Data.Anonymizer/blob/main/src/bleak.Data.Anonymizer/ANON-CONFIG.md) for the full config reference.

## Install

```sh
dotnet add package bleak.Data.Anonymizer
```

## Usage

```csharp
using bleak.Data.Anonymizer.Config;
using bleak.Data.Anonymizer.Engine;
using Microsoft.Extensions.Logging;

ILoggerFactory loggerFactory = /* from DI, or LoggerFactory.Create(b => b.AddConsole()) */;

var config = AnonConfigLoader.FromFile("customers.anonconfig");
var results = AnonymizeEngine.Run(config, configDir: ".", outputFolderOverride: "./out",
    loggerFactory: loggerFactory);
```

Each `FileResult` reports rows, cells anonymized, tolerated cell errors, missing columns, input/output bytes and
`DurationMs`.

## Logging

`AnonymizeEngine.Run` takes an optional `ILoggerFactory? loggerFactory` as its **last** parameter. It defaults to
`null`, which means no logging. The library depends only on `Microsoft.Extensions.Logging.Abstractions`, so any
provider works. The same factory is passed down to `bleak.Data.InfiniteDataLoader`'s `CsvReader`.

| Category | Information | Debug | Warning | Error |
|---|---|---|---|---|
| `bleak.Data.Anonymizer.Engine.AnonymizeEngine` | run started (config name, job count); run completed (files, copied, skipped, rows, cells, bad cells, missing columns, bytes in/out, `DurationMs`, `RecordsPerSecond`); one line per file anonymized (rows, cells, bytes in/out, `DurationMs`) or byte-copied (bytes, `DurationMs`) | run settings (folders, `onError`, seeded); per file the `Open` (reader + header resolution), `Process` (row loop, split into read / anonymize / format+write ms) and `Write` (final flush) phases with `DurationMs`; per column: cells, empty/absent, errors, cumulative ms; per anonymizer method across the run: column rules, cells, errors, cumulative ms; the first 5 failed cells per file (data row number, column, method, exception type) | input file not found (skipped); configured columns missing from a header (one line per file listing the names); cells left unchanged after an anonymizer error (one line per file with a per-column count) | a file that failed (with the exception); a cell failure under `onError: abort` (row number, column, method, exception type) |
| `bleak.Data.InfiniteDataLoader.CsvReader` | - | reader opened (column count); closed (rows, bytes read, `DurationMs`) | - | - |

Nothing is logged at Trace. Event IDs: 2000-2099 (`bleak.Data.InfiniteDataLoader` uses 1000-1499).

Notes for performance work:

- Nothing is ever logged per row; failed cells are counted and summarized. Messages use source-generated
  `[LoggerMessage]` methods, so a disabled level costs nothing.
- The per-column, per-method and `Process` phase breakdowns read a timestamp around each anonymized cell and
  each written row. That only happens while Debug is enabled for the engine category; at Information the hot
  loop does no timing.
- `ReadMs` in the `Process` line is the loop time not spent anonymizing or writing, i.e. CSV parsing plus loop
  overhead.

**PII guarantee:** at every level, Trace included, the anonymizer logs only metadata: counts, byte sizes,
durations, config, file and column names, method names, data row numbers and exception type names. Cell values,
original or anonymized, are never logged, and neither are anonymizer exception messages (they could quote the
cell). This is covered by a test that runs with a Trace-level logger and checks that no sample value appears in any
message or property.

## Upgrading from 1.x

2.0.0 replaces the `Action<string>? log` callback with `Microsoft.Extensions.Logging`:

| 1.x | 2.0 |
|---|---|
| `AnonymizeEngine.Run(config, configDir, outputFolderOverride, registry = null, Action<string>? log = null)` | `AnonymizeEngine.Run(config, configDir, outputFolderOverride, registry = null, ILoggerFactory? loggerFactory = null)` |

Also new: `AnonConfig.Name` (optional `name` in the config; `AnonConfigLoader.FromFile` defaults it to the file
name) and `FileResult.InputBytes`, `OutputBytes` and `DurationMs`. The dependency on `bleak.Data.InfiniteDataLoader`
is now 2.0.0.

## License

[The Unlicense](LICENSE) (public domain).
