@echo off
rem Builds DiskHawk.sln (Release) into bin\Release. Uses Visual Studio / Build Tools MSBuild or the .NET SDK;
rem if neither is installed, a private .NET SDK is downloaded once into .tools\dotnet (see build.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if errorlevel 1 exit /b 1
