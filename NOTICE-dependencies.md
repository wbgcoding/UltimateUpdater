# Third-party dependencies

Every direct NuGet package this project references, its version at the time of writing and its
licence. Transitive packages are not listed here; `dotnet list package --include-transitive` prints them.

| Package | Version | Licence |
| --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| Raffinert.FuzzySharp | 5.0.3 | MIT |
| System.Management | 10.0.11 | MIT |
| WPF-UI | 4.3.0 | MIT |
| coverlet.collector | 10.0.1 | MIT |
| Microsoft.NET.Test.Sdk | 18.9.0 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 4.0.0 | Apache-2.0 |

This file is checked against both `.csproj` files by an automated test: adding a package without
adding it here fails the build. Update this table in the same change that adds, removes or
re-licenses a package.

Brand marks shown in the update table are a separate matter, covered by
[`NOTICE-brand-icons.md`](NOTICE-brand-icons.md).
