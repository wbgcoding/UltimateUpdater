# Third-party dependencies

Every direct NuGet package this project references and its licence. The exact versions live in the
two `.csproj` files and change with routine dependency updates, so they are not repeated here.
Transitive packages are not listed here; `dotnet list package --include-transitive` prints them.

| Package | Licence |
| --- | --- |
| CommunityToolkit.Mvvm | MIT |
| Raffinert.FuzzySharp | MIT |
| System.Management | MIT |
| WPF-UI | MIT |
| coverlet.collector | MIT |
| Microsoft.NET.Test.Sdk | MIT |
| xunit | Apache-2.0 |
| xunit.runner.visualstudio | Apache-2.0 |

This file is checked against both `.csproj` files by an automated test: adding a package without
adding it here fails the build. Update this table in the same change that adds, removes or
re-licenses a package.

Brand marks shown in the update table are a separate matter, covered by
[`NOTICE-brand-icons.md`](NOTICE-brand-icons.md).
