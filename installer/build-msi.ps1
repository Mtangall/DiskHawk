# Builds installer\out\DiskHawk-<version>-x64.msi with WiX Toolset 3.14: welcome, license agreement (EULA), install folder,
# progress and finish dialogs, in English and Turkish. Run build.cmd first so bin\Release contains the binaries.
#   1. EULA.<culture>.txt is converted to RTF for the license dialog
#   2. DiskHawk.wxs is compiled once and linked per culture with DiskHawk.<culture>.wxl
#   3. the Turkish MSI is turned into a language transform (torch) and embedded into the English MSI as substorage "1055";
#      Windows Installer applies it automatically when the Windows display language is Turkish.
#      Force a language with: msiexec /i DiskHawk-<version>-x64.msi ProductLanguage=1033   (or 1055)
# An installed WiX 3.14 is used if present; otherwise the portable WiX binaries are downloaded once into .tools\wix314.
param([string]$Version)   # default: the version of bin\Release\DiskHawk.exe
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow in Windows PowerShell with a progress bar
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$bin = Join-Path $root 'bin\Release'
$exe = Join-Path $bin 'DiskHawk.exe'
if (-not (Test-Path $exe)) { throw "bin\Release\DiskHawk.exe not found - run build.cmd first." }
if (-not $Version) {
    $vi = (Get-Item $exe).VersionInfo
    $Version = '{0}.{1}.{2}' -f $vi.FileMajorPart, $vi.FileMinorPart, $vi.FileBuildPart
}

# the first culture is the base package, the others become embedded language transforms
$cultures = @(
    @{ Name = 'en-US'; Lcid = 1033; Codepage = 1252; Charset = 0 },
    @{ Name = 'tr-TR'; Lcid = 1055; Codepage = 1254; Charset = 162 }
)

# the binaries in this zip are not Authenticode-signed, so the download is pinned by hash (WiX 3.14.1, 41297555 bytes)
$wixZipUrl = 'https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip'
$wixZipSha256 = '6AC824E1642D6F7277D0ED7EA09411A508F6116BA6FAE0AA5F2C7DAA2FF43D31'
$wix = $null
foreach ($p in @($(if ($env:WIX) { Join-Path $env:WIX 'bin' }), "${env:ProgramFiles(x86)}\WiX Toolset v3.14\bin", (Join-Path $root '.tools\wix314'))) {
    if ($p -and (Test-Path (Join-Path $p 'candle.exe'))) { $wix = $p; break }
}
if (-not $wix) {
    $wix = Join-Path $root '.tools\wix314'
    $zip = Join-Path $root '.tools\wix314-binaries.zip'
    New-Item -ItemType Directory -Force $wix | Out-Null
    Write-Host 'WiX Toolset 3.14 not found - downloading the portable binaries into .tools\wix314 (one time)...'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest $wixZipUrl -OutFile $zip -UseBasicParsing
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $wixZipSha256) { Remove-Item $zip; throw 'wix314-binaries.zip: SHA-256 mismatch, not used.' }
    Expand-Archive $zip -DestinationPath $wix -Force
    Get-ChildItem $wix -Recurse | Unblock-File
    Remove-Item $zip
}

function Invoke-Wix([string]$tool, [string[]]$arguments) {
    & (Join-Path $wix "$tool.exe") @arguments
    if ($LASTEXITCODE -ne 0) { throw "$tool failed (exit code $LASTEXITCODE)" }
}

# EULA text -> RTF for the license dialog. Blank lines separate paragraphs, '# ' starts a heading (the first one is the
# title) and a '{LICENSE}' line inserts ..\LICENSE without its title line. Only 7-bit ASCII is written (\uN escapes),
# so the RTF does not depend on the code page of the MSI database.
function ConvertTo-RtfText([string]$s) {
    $sb = New-Object Text.StringBuilder
    foreach ($ch in $s.ToCharArray()) {
        $c = [int]$ch
        if ('\{}'.Contains([string]$ch)) { [void]$sb.Append('\' + $ch) }
        elseif ($c -lt 128) { [void]$sb.Append($ch) }
        else { [void]$sb.Append('\u' + $(if ($c -gt 32767) { $c - 65536 } else { $c }) + '?') }
    }
    $sb.ToString()
}

function Convert-EulaToRtf([string]$source, [string]$target, [int]$codepage, [int]$charset) {
    $license = [IO.File]::ReadAllLines((Join-Path $root 'LICENSE')) | Select-Object -Skip 1
    $lines = foreach ($l in [IO.File]::ReadAllLines($source)) { if ($l.Trim() -eq '{LICENSE}') { $license } else { $l } }
    $blocks = New-Object Collections.Generic.List[string]
    $para = New-Object Collections.Generic.List[string]
    foreach ($l in @($lines) + '') {
        $t = $l.Trim()
        if ($t -eq '' -or $t.StartsWith('# ')) {
            if ($para.Count) { $blocks.Add($para -join ' '); $para.Clear() }
            if ($t) { $blocks.Add($t) }
        }
        else { $para.Add($t) }
    }
    $rtf = New-Object Text.StringBuilder
    [void]$rtf.Append("{\rtf1\ansi\ansicpg$codepage\deff0{\fonttbl{\f0\fswiss\fcharset$charset Segoe UI;}}\viewkind4\uc1`r`n")
    $first = $true
    foreach ($b in $blocks) {
        if ($b.StartsWith('# ')) {
            $size = if ($first) { 24 } else { 18 }
            [void]$rtf.Append("\pard\sb120\sa80\f0\fs$size\b " + (ConvertTo-RtfText $b.Substring(2)) + "\b0\par`r`n")
            $first = $false
        }
        else { [void]$rtf.Append('\pard\sa120\f0\fs18 ' + (ConvertTo-RtfText $b) + "\par`r`n") }
    }
    [void]$rtf.Append('}')
    [IO.File]::WriteAllText($target, $rtf.ToString(), [Text.Encoding]::ASCII)
}

# Embeds language transforms as substorages named after their LCID and lists all languages in the Template summary
# property (like WiSubStg.vbs + WiLangId.vbs); Windows Installer then applies the transform that matches the user's
# display language. Uses the Windows Installer automation interface, late bound.
function Add-LanguageTransforms([string]$msi, [string]$template, [hashtable]$transforms) {
    $call = [Reflection.BindingFlags]::InvokeMethod
    $get = [Reflection.BindingFlags]::GetProperty
    $set = [Reflection.BindingFlags]::SetProperty
    function Com($obj, $member, $flags, [object[]]$params = @()) {
        # results of earlier calls come back wrapped in PSObject; COM rejects wrapped arguments (DISP_E_TYPEMISMATCH)
        $raw = New-Object object[] $params.Count
        for ($i = 0; $i -lt $params.Count; $i++) { $raw[$i] = $params[$i].PSObject.BaseObject }
        $obj.GetType().InvokeMember($member, $flags, $null, $obj, $raw)
    }
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $objects = @()
    try {
        $db = Com $installer OpenDatabase $call @($msi, 1)   # msiOpenDatabaseModeTransact
        $view = Com $db OpenView $call @('SELECT `Name`, `Data` FROM `_Storages`')
        $objects += $db, $view
        [void](Com $view Execute $call)
        foreach ($name in $transforms.Keys) {
            $rec = Com $installer CreateRecord $call @(2)
            $objects += $rec
            [void](Com $rec StringData $set @(1, $name))
            [void](Com $rec SetStream $call @(2, $transforms[$name]))
            [void](Com $view Modify $call @(3, $rec))   # msiViewModifyAssign
        }
        [void](Com $view Close $call)
        $si = Com $db SummaryInformation $get @(1)
        $objects += $si
        [void](Com $si Property $set @(7, $template))   # PID_TEMPLATE = platform;languages
        [void](Com $si Persist $call)
        [void](Com $db Commit $call)
    }
    finally {
        foreach ($o in @($objects) + $installer) { if ($o) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($o) } }
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    }
}

$out = Join-Path $here 'out'
$obj = Join-Path $out 'obj'
if (Test-Path $obj) { Remove-Item $obj -Recurse -Force }
New-Item -ItemType Directory -Force $obj | Out-Null
$msi = Join-Path $out "DiskHawk-$Version-x64.msi"
# one ProductCode for every culture: a language transform must not change it
$productCode = [guid]::NewGuid().ToString('B').ToUpperInvariant()

Push-Location $here
try {
    Invoke-Wix candle @('-nologo', '-arch', 'x64', '-ext', 'WixUtilExtension',
        "-dVersion=$Version", "-dProductCode=$productCode", "-dBinDir=$bin", "-dAssetsDir=$(Join-Path $root 'assets')",
        '-out', (Join-Path $obj 'DiskHawk.wixobj'), 'DiskHawk.wxs')
    foreach ($c in $cultures) {
        $rtf = Join-Path $obj "EULA.$($c.Name).rtf"
        Convert-EulaToRtf (Join-Path $here "EULA.$($c.Name).txt") $rtf $c.Codepage $c.Charset
        # WixUI strings missing in a culture fall back to English; ICE61 = AllowSameVersionUpgrades (intended)
        $cultureList = if ($c.Name -eq 'en-US') { 'en-US' } else { "$($c.Name);en-US" }
        Invoke-Wix light @('-nologo', '-ext', 'WixUIExtension', '-ext', 'WixUtilExtension', "-cultures:$cultureList",
            '-loc', "DiskHawk.$($c.Name).wxl", "-dWixUILicenseRtf=$rtf", '-sice:ICE61', '-spdb',
            '-out', (Join-Path $obj "$($c.Name)\DiskHawk.msi"), (Join-Path $obj 'DiskHawk.wixobj'))
    }
    $baseMsi = Join-Path $obj "$($cultures[0].Name)\DiskHawk.msi"
    $transforms = @{}
    foreach ($c in $cultures | Select-Object -Skip 1) {
        $mst = Join-Path $obj "$($c.Name).mst"
        Invoke-Wix torch @('-nologo', '-t', 'language', $baseMsi, (Join-Path $obj "$($c.Name)\DiskHawk.msi"), '-out', $mst)
        $transforms["$($c.Lcid)"] = $mst
    }
    Copy-Item $baseMsi $msi -Force
    Add-LanguageTransforms $msi ('x64;' + (($cultures | ForEach-Object { $_.Lcid }) -join ',')) $transforms
}
finally { Pop-Location }
Remove-Item $obj -Recurse -Force
Write-Host ''
Write-Host "MSI: $msi"
Write-Host "     version $Version, languages: $(($cultures | ForEach-Object { $_.Name }) -join ', ')"
