# Third-party dependencies

Every direct NuGet package this project references and its licence. The exact versions live in the
two `.csproj` files and change with routine dependency updates, so they are not repeated here.

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

The first four are bundled in the executable, together with their own dependencies System.CodeDom and
WPF-UI.Abstractions (both MIT) and the self-contained .NET runtime with WPF and Windows Forms (MIT). The
last four are test tools and do not ship.

## Licence texts that ship

[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) holds the full licence and notice texts of everything
that is bundled, copied unchanged from the packages. It sits in the repository root, beside the portable
executable in every release, and in the install folder of an installed copy. `tools/make-third-party-notices.py`
rewrites it from the NuGet package cache; run it after a package update and commit the result.

This file is checked against both `.csproj` files by an automated test: adding a package without
adding it here fails the build. A second test fails when a bundled package has no section in
`THIRD-PARTY-NOTICES.txt`. Update this table and the notices in the same change that adds, removes or
re-licenses a package.

Brand marks shown in the update table are a separate matter, covered by
[`NOTICE-brand-icons.md`](NOTICE-brand-icons.md).
