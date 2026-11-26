# Contributing

## Before you start

- **Bugs and ideas:** open an issue with the matching template first, so we can agree on the
  approach before you spend time on code.
- **Security problems:** never in a public issue, see [SECURITY.md](SECURITY.md). The app starts
  installers with administrator rights, so these reports come first.

## Build and test

You need Windows 10 or 11, the .NET 10 SDK, Python 3 (the `py` launcher) with Pillow and, for the
installer, Inno Setup 6. Only signing a release also needs `py -3 -m pip install cryptography`.

```bat
dotnet build UltimateUpdater.slnx -c Release
dotnet test tests\UltimateUpdater.Tests\UltimateUpdater.Tests.csproj -c Release --filter "FullyQualifiedName!~WindowLayoutTests&FullyQualifiedName!~LiveButtonContrastTests&FullyQualifiedName!~PressedPixelTests"
```

That filter matches CI. The render tests need a desktop session and run on their own:

```bat
dotnet test tests\UltimateUpdater.Tests\UltimateUpdater.Tests.csproj -c Release --filter "FullyQualifiedName~WindowLayoutTests"
```

`build.bat` runs the full pipeline into `dist\`.

## Helper scripts

- `tools\make-third-party-notices.py` rewrites `THIRD-PARTY-NOTICES.txt` from the NuGet package cache;
  run it after a package update (a test fails when the file is out of date).
- `tools\coverage-report.py <coverage.cobertura.xml>` lists the classes with the lowest line coverage
  (collect with `dotnet test --collect:"XPlat Code Coverage"`).
- `tools\vm\` holds the scripts that prepare a Windows VM for the Store and package-manager tests
  (a signed test package, a local winget source, seeded outdated packages). They run in a disposable
  virtual machine, never on your own PC.

## Guidelines

- **Mind the security-critical code.** Elevated launches, the named pipe to the elevated helper, the
  self-update signature check and Setup's elevated PowerShell are the places where a small change
  can open a hole; touch them only with a test and say so in the pull request.
- **A source only reads.** Checking for updates never installs, downloads or self-updates a tool;
  a missing tool means the source shows no rows.
- **No invented versions.** A row shows a version only when it is really known.
- **Tests with every change.** New behaviour comes with a test; a bug fix comes with a test that
  failed before the fix.
- **Every language.** New user-facing text goes into `Resources/Strings.resx` and every translated
  `Strings.*.resx` (for example `Strings.de.resx`).
- **One topic per pull request,** with a short description of what changed and how you tested it.

## License

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
