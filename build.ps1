# Builds DiskHawk.sln into bin\<Configuration>\ (default Release).
# Toolchain, first match wins:
#   1. Visual Studio 2019/2022 or Build Tools (MSBuild 16+)
#   2. an installed .NET SDK (dotnet on PATH)
#   3. a private .NET SDK in .tools\dotnet, downloaded on first use with Microsoft's dotnet-install script
#      (no administrator rights needed; delete .tools to remove it)
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow in Windows PowerShell with a progress bar
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }
    # -products * also finds Build Tools installations
    & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}

function Test-DotnetSdk([string]$exe) {
    if (-not $exe -or -not (Test-Path $exe)) { return $false }
    try { return [bool](& $exe --list-sdks 2>$null) } catch { return $false }
}

function Get-PrivateDotnet {
    $dir = Join-Path $root '.tools\dotnet'
    $exe = Join-Path $dir 'dotnet.exe'
    if (Test-DotnetSdk $exe) { return $exe }
    Write-Host 'No MSBuild or .NET SDK found - downloading a private .NET SDK into .tools\dotnet (one time)...'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $script = Join-Path $root '.tools\dotnet-install.ps1'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
    # the install script is Authenticode-signed by Microsoft; never run anything else
    $sig = Get-AuthenticodeSignature $script
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "dotnet-install.ps1 signature check failed ($($sig.Status))."
    }
    & $script -Channel 10.0 -InstallDir $dir -NoPath
    if (-not (Test-DotnetSdk $exe)) { throw '.NET SDK installation failed.' }
    return $exe
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$sln = Join-Path $root 'DiskHawk.sln'
$msbuild = Find-MSBuild
if ($msbuild) {
    & $msbuild $sln -restore "-p:Configuration=$Configuration" -m -v:m
} else {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not (Test-DotnetSdk $dotnet)) { $dotnet = Get-PrivateDotnet }
    & $dotnet build $sln -c $Configuration -v:m
}
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'BUILD FAILED'
    exit 1
}
Write-Host ''
Write-Host "Output: $(Join-Path $root "bin\$Configuration")"
Write-Host '  DiskHawk.exe          - management console'
Write-Host '  DiskHawk.Core.dll'
Write-Host '  DiskHawk.Scanner.exe  - agentless scanner deployed to target machines'
