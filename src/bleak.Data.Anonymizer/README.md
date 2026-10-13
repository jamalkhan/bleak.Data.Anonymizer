# bleak.Data.Anonymizer

Config-driven PII anonymizer for large delimited files: fake names, emails, phones, dates, addresses, GUIDs and more.

Describe in a JSON `.anonconfig` which columns of which delimited files to anonymize and with which
method (fake names, e-mail addresses, phone numbers, dates, addresses, hex tokens, GUIDs, constants, and so on).
Files are processed as streams, so inputs can be larger than RAM.

See [ANON-CONFIG.md](ANON-CONFIG.md) for the full config reference.

## Install

```sh
dotnet add package bleak.Data.Anonymizer
```

## License

[The Unlicense](LICENSE) (public domain).
