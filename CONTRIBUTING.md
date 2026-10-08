# Contributing

Thanks for helping! Bug reports, test results from real environments and pull requests are welcome.

## Building

- Run `build.cmd`, or open `DiskHawk.sln` in Visual Studio 2019/2022 (with the .NET Framework 4.8 targeting pack). `build.cmd` uses Visual Studio / Build Tools MSBuild or an installed .NET SDK; if neither is present it downloads a private .NET SDK into `.tools\` (no administrator rights needed; delete the folder to remove it).
- The App project embeds the SHA-256 of the scanner at build time, so always build the whole solution.
- MSI: run `installer\build-msi.cmd` after `build.cmd`. It uses an installed WiX Toolset v3.14 or downloads the portable binaries into `.tools\` (hash-checked). The license agreement is generated from `installer\EULA.en-US.txt` and `installer\EULA.tr-TR.txt` (plain text; the MIT text itself comes from `LICENSE`) and the installer's own strings live in `installer\DiskHawk.<culture>.wxl` — keep the English and Turkish versions in sync.

## Guidelines

- C# 7.3 / .NET Framework 4.8, no third-party packages.
- Code comments and documentation are in English.
- **UI strings:** write them in Turkish inside `L.T("...")` and add the English text to the dictionary in `src/DiskHawk.Core/Loc.cs` (the Turkish text is the key). A missing entry shows the Turkish text in the English UI, so please check.
- Anything that touches remote execution, deletion or report parsing must keep treating remote data as untrusted. Please describe how you tested such changes (lab VM, scenarios).
- Keep the scanner small and dependency-free: it is copied to every target machine.

## Pull requests

1. Fork and create a branch.
2. Keep changes focused; describe the motivation and how you tested.
3. Update `CHANGELOG.md` under "Unreleased".
