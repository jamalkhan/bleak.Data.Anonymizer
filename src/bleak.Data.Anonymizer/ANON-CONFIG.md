# Anonymization Config Schema (`bleak.Data.Anonymizer`)

A single `.anonconfig` file describes a **PII anonymization run**: for each input file, which columns to
replace with obviously-fake values, and where to write the renamed result. It is consumed by the
`AnonymizePii` console command (which lists the bundled configs in `data/anonconfig` to pick from — or
accepts a path to your own) and by `AnonymizeEngine.Run(...)`. `.anonconfig` files are JSON; comments
(`//`, `/* */`) and trailing commas are allowed (jsonc-style).

## Design principles

- **Opt-in, column by column.** Only the columns you list are touched. Everything else — including all
  join keys (`customer_key`, `email_key`, `household_key`, …) — passes through **unchanged**, so the
  anonymized files still join to each other exactly as before.
- **Obviously fake.** Names, usernames, and e-mails are drawn from recurring **Simpsons** characters
  (Bart Simpson, Ned Flanders, …); phone numbers use the reserved-for-fiction **555-01XX** range.
- **Random per occurrence.** Each cell is independently randomized (the same real value may map to
  different fakes in different rows).
- **Originals are never touched.** Output goes to a separate folder and each file is renamed
  (`src_*` → `anonymized_*` by default). Writing on top of a source is refused.
- **Scales to any size.** Files with no configured columns are byte-copied (fast path). Files that do get
  anonymized are streamed one row at a time, so memory stays flat on 20 GB+ inputs.

## Top level

```jsonc
{
  "version": 1,
  "run": { "onError": "skip", "inputPrefix": "src_", "outputPrefix": "anonymized_" },
  "inputFolder": "./input",             // base for relative file paths (rel. to this config)
  "outputFolder": "./output", // where renamed outputs land (rel. to this config)
  "files": [ { ...FileJob... } ]
}
```

Paths (`inputFolder`, `outputFolder`, and a job's `input`/`output`) support a leading `~` (home) and
resolve relative to the **config file's folder** when not absolute.

### `run`
| field | values | default | meaning |
|---|---|---|---|
| `onError` | `skip` \| `abort` | `skip` | On a per-cell anonymizer exception. **skip**: leave the cell unchanged, log it, keep going. **abort**: stop the run. |
| `seed` | integer | *(none)* | Optional RNG seed. When set, the run is reproducible (same fakes every time); still random per occurrence. |
| `inputPrefix` | string | `src_` | Prefix stripped from the input filename when deriving the output name. |
| `outputPrefix` | string | `anonymized_` | Prefix prepended in its place. |
| `emailDomains` | string[] | `["springfield.example"]` | Pool of fake domains `simpsonsEmail` draws from (a column can override with `domain`). |

## `files` — one job per input

```jsonc
{
  "input": "src_customers.csv", // rel. to inputFolder (absolute allowed)
  "output": "anonymized_corp.csv",   // optional; bare name -> output folder, rooted path -> verbatim.
                                     // omit to auto-derive (inputPrefix -> outputPrefix)
  "delimiter": "csv",                // csv|comma "," · tsv|tab "\t" · pipe "|" · semicolon ";" · or any single char
  "quote": "\"",
  "hasHeader": true,
  "columns": [ { ...ColumnRule... } ]  // EMPTY  => byte-copy the file to the renamed output
}
```

### `columns[]` — ColumnRule
| field | meaning |
|---|---|
| `name` | The column header to anonymize (case-insensitive). Unmatched names are logged and skipped. |
| `method` | Which anonymizer to use (see below). |
| `options` | Free-form per-method options object. |
| `whenEmpty` | `passthrough` (default — leave empty cells empty; don't fabricate data) or `generate` (produce a fake even for an empty source cell). |

## Anonymizer methods

| method | options | produces |
|---|---|---|
| `simpsonsName` | `part`: `first`\|`middle`\|`last`\|`full` (default `full`); `group` | A Simpsons character's name part. Columns sharing a `group` describe **one person** within the row (e.g. first/last/full agree). |
| `simpsonsEmail` | `domain` (default from `run.emailDomains`); `group` | `bart.simpson47@springfield.example`. Share a `group` with name columns to make the local-part match the row's person. |
| `fakePhone` | `format`: `digits` (default, `55555501NN`) \| `dashes` (`555-555-01NN`) | A number in the 555-01XX fictional range. |
| `last4` | `digits` (default 4) | A random zero-padded numeric string (card / phone "last 4"). |
| `randomHex` | `length` (default 64) | A random lowercase hex string (replaces hash columns so they can't fingerprint the original). |
| `fakeDate` | `format` (default `yyyy-MM-dd`); `minYear` (1940); `maxYear` (2005) | A random date in range — for birth/other sensitive dates. |
| `guid` | `format`: `d` (default, lowercase dashed) \| `n` (32 hex) \| `upper` | A fresh random GUID. |
| `fakeAddress` | `part`: `line1`\|`line2`\|`line3`\|`city`\|`state`\|`province`\|`postal`\|`postalExt`; `group` | A coherent Springfield-themed mailing address; `line2`/`line3` blank out. Share a `group` across a row's address columns. |
| `constant` | `value` (default empty) | A fixed literal (e.g. a fake `email_domain` or a constant `area_code`). |

Add your own with `AnonymizerRegistry.Register(myAnonymizer)` before calling `AnonymizeEngine.Run(...)`
(mirrors the ETL engine's `TransformRegistry`).

## How output names are chosen

1. Job `output` is a **rooted path** → used verbatim.
2. Job `output` is a **bare filename** → placed in the run's output folder.
3. No `output` → derived from the input name by swapping `inputPrefix` for `outputPrefix`
   (`src_foo.csv` → `anonymized_foo.csv`).

The output folder comes from `outputFolder` in the config, or — when omitted — the folder the command
prompts for. The engine refuses to write a file on top of its own source.
