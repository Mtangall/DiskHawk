# Changelog

## 1.4.0 — first public release
- MIT license, English documentation.
- MSI installer (`installer/`): license agreement (EULA), English and Turkish dialogs in a single package (follows the Windows display language), option to start DiskHawk after setup, program folder writable only by administrators; a rebuilt package with the same version replaces the installed one.
- `build.cmd` no longer needs Visual Studio: it falls back to the .NET SDK and, if none is installed, downloads a private copy into `.tools\`.
- When installed under Program Files, data (settings, reports, logs) is stored in `%LOCALAPPDATA%\DiskHawk`.
- Removed migration code for internal pre-release data formats.

## 1.3.3
- Scanner integrity: SHA-256 embedded at build time; mismatching scanners are not deployed; warning if non-administrators can write to the application folder.
- Bounded reads of remote helper files and reports; corrupt reports opened by double-click no longer close the application.
- Log rotation (10 MB); `error.log` in the data folder; Active Directory OU must be a distinguished name.

## 1.3.2
- Handle-based deletion engine: links are never followed, the real path is verified before deleting.

## 1.3.1
- Remote working folder moved to an administrators-only `C:\Windows\DiskHawk\<run>`; scanner verified after copy.
- Hardened report reader (limits, sanitized names, root validation), CSV formula neutralization, Explorer safeguards, deletion list count check, machine name validation, $MFT parser bounds.
