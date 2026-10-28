# Contributing

## Before you start

- **Bugs and ideas:** open an issue with the matching template first, so we can agree on the
  approach before you spend time on code.
- **Security problems:** never in a public issue, see [SECURITY.md](SECURITY.md). The app starts
  installers with administrator rights, so these reports come first.

## Build and test

You need Windows 10 or 11, the .NET 10 SDK, Python with Pillow and, for the installer,
Inno Setup 6.

```bat
dotnet build UltimateUpdater.slnx -c Release
dotnet test tests\UltimateUpdater.Tests\UltimateUpdater.Tests.csproj -c Release
```

`build.bat` runs the full pipeline into `dist\`.

## Guidelines

- **Read `docs/INVARIANTS.md` first.** Several entries there are security-critical.
- **A source only reads.** Checking for updates never installs, downloads or self-updates a tool;
  a missing tool means the source shows no rows.
- **No invented versions.** A row shows a version only when it is really known.
- **Tests with every change.** New behaviour comes with a test; a bug fix comes with a test that
  failed before the fix.
- **Every language.** New user-facing text goes into `Resources/Strings.resx` and every
  `Strings.<language>.resx`.
- **One topic per pull request,** with a short description of what changed and how you tested it.

## License

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
