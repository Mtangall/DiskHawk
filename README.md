<p align="center"><img src="assets/diskhawk-logo-256.png" width="96" alt="DiskHawk"></p>

<h1 align="center">DiskHawk</h1>
<p align="center"><b>Fleet disk analytics for Windows — find out where the space went on every machine, in seconds, without an agent.</b></p>

<p align="center">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue.svg"></a>
  <img alt="Platform: Windows" src="https://img.shields.io/badge/platform-Windows%2010%2F11%20%7C%20Server%202016%2B-0078d4">
  <img alt=".NET Framework 4.8" src="https://img.shields.io/badge/.NET%20Framework-4.8-512bd4">
</p>

DiskHawk scans many Windows endpoints in parallel from a single console, shows which folder is actually eating the space on each machine, and lets you clean it up remotely with guard rails and a full audit trail.

> **Status:** 1.4 is the first public release. Remote **deletion** is irreversible — try it in a lab first (see [Testing](#testing)).

## Features

| | |
|---|---|
| **Raw NTFS $MFT engine** | Reads the Master File Table directly (like WizTree): seconds per drive, hard links counted once, no "access denied" gaps. Falls back to a multi-threaded classic engine on non-NTFS volumes or without admin rights. |
| **Agentless** | WMI + SMB (`admin$`). A small scanner is copied to a fresh, administrators-only folder under `C:\Windows\DiskHawk`, verified (SHA-256), run at low priority and removed. No WinRM, no service, nothing left installed. |
| **Fleet view** | Status cards, usage bars, per-machine *hotspot* (the folder where the space really concentrates), largest file, filters for 80 % / 90 %+ full, CSV export. |
| **Deep dive** | Folder tree, sortable list and nested treemap per machine; largest files and per-extension statistics. |
| **Guarded remote cleanup** | Deletes files/folders on the target machine itself. Critical paths (Windows, Program Files, profiles, OneDrive) need an explicit extra confirmation, drive roots are never deleted, links are never followed, and every action is written to `deletion_audit.csv`. |
| **Enterprise friendly** | Active Directory import (name filter, OU, last-logon window), parallelism / retry / timeout settings, English and Turkish UI. |

## Install

**MSI (recommended):** download `DiskHawk-<version>-x64.msi` from [Releases](../../releases) and run it. The installer shows the license agreement (EULA), asks for administrator approval, installs per-machine (default `C:\Program Files\DiskHawk`, writable only by administrators), adds a Start menu shortcut, associates `.dhr` report files and can start DiskHawk when it finishes. Its dialogs and EULA are in Turkish on Turkish Windows and in English everywhere else (`ProductLanguage=1033` or `1055` on the msiexec command line picks one). Silent install for SCCM / Intune / GPO: `msiexec /i DiskHawk-<version>-x64.msi /qn`.

**Portable:** build from source (below) and run `DiskHawk.exe` from any folder that only administrators can write to.

Requirements

- Console: Windows 10/11 or Server 2016+, .NET Framework 4.8.
- Targets: Windows 10/11 or Server 2016+ with .NET Framework 4.8; TCP 135 + dynamic RPC (WMI) and TCP 445 (SMB) reachable from the console; the account running DiskHawk must be a local administrator on the targets.
- Endpoint protection that blocks unknown executables must allow `DiskHawk.Scanner.exe` (it runs from `C:\Windows\DiskHawk\`). Release binaries are not code-signed yet.

## Quick start

The full **[User Guide](docs/USER_GUIDE.md)** covers installation, scanning, reports, cleanup, settings, the command-line scanner and troubleshooting.


1. Start **DiskHawk** with an account that is a local administrator on the target machines.
2. Add machines: type a name, paste a list (Ctrl+V), **Import** a TXT/CSV, or pull from **Active Directory**.
3. **Scan selected** (F5). Double-click a finished machine to open its report.
4. **This PC** analyzes the local machine (run as administrator to use the MFT engine).

## Command-line scanner

`DiskHawk.Scanner.exe` also works on its own (e.g. via SCCM, GPO or a scheduled task):

```
DiskHawk.Scanner.exe                       all fixed drives, report in the current folder
DiskHawk.Scanner.exe C: -o \\server\share\reports\
DiskHawk.Scanner.exe C: --csv C:\Temp\report --min-file-mb 50
  --engine auto|mft|classic   scan engine (default auto)
  --below-normal / --low-io   lower CPU / I/O priority
  --lang en|tr                language of result texts
  --delete list.txt --delete-log result.tsv [--allow-critical]
```

Exit codes: 0 OK, 2 error (`<report>.err` is written), 3 cancelled, 64 bad arguments. Open any `.dhr` report in the console with **Open report**.

If many machines write reports to one share, give computer accounts *create files* only and keep read/delete for administrators — DiskHawk treats every report as untrusted input, but the share should still not let machines overwrite each other's reports.

## Security model

DiskHawk runs with administrator rights across a whole fleet, so it is built defensively:

- **Scanner integrity** — the SHA-256 of `DiskHawk.Scanner.exe` is embedded into `DiskHawk.exe` at build time; a scanner that does not match is never deployed. The console also warns if non-administrators can write to its own folder.
- **Remote working folder** — `C:\Windows\DiskHawk\<run>` with an ACL reset to SYSTEM + Administrators and verified before use; a new folder per run.
- **Deletion engine** — the target is opened without following links and its real location is verified (`GetFinalPathNameByHandle`); the tree is then listed and deleted only through open handles. Paths reached through a junction, SUBST drive, mounted folder or 8.3 short name are refused. Protected paths are checked both in the console and on the target.
- **Untrusted input** — reports, deletion results and progress files from remote machines are size-bounded and parsed defensively; names are sanitized; a report opened from a file asks before any remote action; Explorer never launches files and asks before connecting to a server named in a report.
- **Exports** — CSV cells starting with `= + - @` are neutralized (Excel formula injection); the audit log cannot be forged with line breaks.

Found a vulnerability? See [SECURITY.md](SECURITY.md).

## Building from source

```
build.cmd                 (Visual Studio 2019/2022, Build Tools or the .NET SDK; without any of them a private
                           .NET SDK is downloaded once into .tools\ - no administrator rights needed)
installer\build-msi.cmd   (WiX Toolset v3.14, downloaded into .tools\ if missing — English/Turkish MSI with EULA)
```

GitHub Actions (`.github/workflows/build.yml`) builds the solution and the MSI on every push; pushing a tag such as `v1.4.0` publishes a release with the MSI attached.

Output goes to `bin\Release\`. .NET Framework 4.8, C# 7.3, no third-party packages.

```
src/DiskHawk.Core      Scan engines (MFT + classic), report format (.dhr), analysis, deletion engine, localization
src/DiskHawk.Scanner   Standalone scanner (Core compiled in, single exe)
src/DiskHawk.App       WinForms console: fleet view, result viewer, treemap, remote scan/delete (WMI), AD import
installer/             WiX source, installer strings (.wxl) and license agreement texts (EULA.*.txt) for the MSI
assets/                Logo and application icon
```

## Data files

Installed under Program Files: `%LOCALAPPDATA%\DiskHawk`. Portable: next to the executable if that folder is writable.
`settings.ini`, `machines.txt`, `Reports\<MACHINE>\*.dhr`, `deletion_audit.csv`, `DiskHawk.log`, `error.log` (logs rotate at 10 MB).

## Testing

Before using remote deletion in production, run it in a lab VM (take a snapshot first): normal trees, read-only files, paths longer than 260 characters, locked files, junctions inside a folder, a path through a junction (must be refused), a critical path without `--allow-critical` (must be protected), and OneDrive online-only items (must be skipped).

## Localization

English is the default; Turkish can be selected in **Settings**. UI strings are written in Turkish in the source and used as keys; `src/DiskHawk.Core/Loc.cs` holds the English dictionary. Adding a language means adding one dictionary — see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE) © 2026 DiskHawk contributors.
