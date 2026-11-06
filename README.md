# bleak.Data.Anonymizer

[![NuGet](https://img.shields.io/nuget/v/bleak.Data.Anonymizer.svg)](https://www.nuget.org/packages/bleak.Data.Anonymizer)

Config-driven PII anonymizer for large delimited files: fake names, emails, phones, dates, addresses, GUIDs and more.

- Usage, the logging reference and upgrade notes: [src/bleak.Data.Anonymizer/README.md](src/bleak.Data.Anonymizer/README.md)
- `.anonconfig` reference: [src/bleak.Data.Anonymizer/ANON-CONFIG.md](src/bleak.Data.Anonymizer/ANON-CONFIG.md)

## Install

```sh
dotnet add package bleak.Data.Anonymizer
```

## Requirements

- .NET 10
- [`bleak.Data.InfiniteDataLoader`](https://github.com/jamalkhan/bleak.Data.InfiniteDataLoader) 2.1.x and
  `Microsoft.Extensions.Logging.Abstractions` 10.0.x (pulled in automatically)

## Build and test

```sh
dotnet build
dotnet test
```

## Releasing

CI (`.github/workflows/ci.yml`) builds and tests every push and pull request. Publishing to nuget.org runs only
when a `v*` tag is pushed. The package version comes from the tag (`v2.0.2` → `2.0.2`), so keep `<Version>` in the
csproj in step with it:

```sh
git tag -a v2.0.2 -m "v2.0.2" && git push origin v2.0.2
```

## License

[The Unlicense](LICENSE) (public domain).
