@echo off
rem Builds installer\out\DiskHawk-<version>-x64.msi (English + Turkish, license agreement) with WiX Toolset 3.14,
rem downloaded automatically if not installed. Run build.cmd first.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-msi.ps1" %*
if errorlevel 1 exit /b 1
