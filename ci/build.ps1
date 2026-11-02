# DesktopSplitter cloud build driver (lives outside .github/workflows so it
# can be updated without editing the workflow file).
# Everything is logged to ci-log.txt via transcript; the workflow commits
# that file to the ci-logs branch after every run, success or failure.
$ErrorActionPreference = 'Continue'

try { Start-Transcript -Path (Join-Path $PWD 'ci-log.txt') -Force | Out-Null } catch { }

function Section([string]$t) { Write-Host "`n########## $t ##########" }
function Show-Exit([string]$what) { Write-Host "$what exit code: $LASTEXITCODE" }

Section 'ENVIRONMENT'
Write-Host "PSVersion: $($PSVersionTable.PSVersion)"
Write-Host "OS: $([Environment]::OSVersion.VersionString)"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    & $vswhere -all -products * -format json
} else { Write-Host 'vswhere NOT FOUND' }
if (Get-Command dotnet -ErrorAction SilentlyContinue) { dotnet --list-sdks } else { Write-Host 'dotnet NOT FOUND' }
Write-Host 'Windows Kits Include dirs:'
Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Include" -Directory -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host 'IddCx headers:'
Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Include\*\um\iddcx\*\IddCx.h" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $($_.FullName)" }

Section 'INSTALL WINDOWS SDK 10.0.26100 (if missing)'
if (Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Include\10.0.26100.0") {
    Write-Host 'SDK 26100 already present, skipping'
} else {
    winget install --id Microsoft.WindowsSDK.10.0.26100 --exact --accept-source-agreements --accept-package-agreements --disable-interactivity
    Show-Exit 'winget SDK'
}

Section 'INSTALL WDK 10.0.26100'
winget install --id Microsoft.WindowsWDK.10.0.26100 --exact --accept-source-agreements --accept-package-agreements --disable-interactivity
Show-Exit 'winget WDK'
Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Include\*\um\iddcx\*\IddCx.h" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "IddCx after install: $($_.FullName)" }

Section 'ADD WDK COMPONENT TO VISUAL STUDIO'
$vs = $null
if (Test-Path $vswhere) { $vs = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1 }
Write-Host "VS path: $vs"
if ($vs) {
    $setupExe = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\setup.exe"
    $p = Start-Process -FilePath $setupExe -Wait -PassThru -ArgumentList 'modify', '--installPath', "`"$vs`"", '--add', 'Component.Microsoft.Windows.DriverKit', '--quiet', '--norestart'
    Write-Host "VS modify exit code: $($p.ExitCode)"
    Write-Host 'Platform toolsets now present:'
    Get-ChildItem "$vs\MSBuild\Microsoft\VC\*\Platforms\x64\PlatformToolsets\*" -Directory -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $($_.Name)" }
} else {
    Write-Host 'No VS installation found!'
}

Section 'BUILD (build.ps1, Release, NoVersionBump)'
& .\build.ps1 -Configuration Release -NoVersionBump
$buildExit = $LASTEXITCODE
Write-Host "build.ps1 exit code: $buildExit"

Section 'RESULT'
$setupExe = 'installer\setup\bin\x64\Release\DesktopSplitterSetup.exe'
$finalExit = 1
if (Test-Path $setupExe) {
    $version = (Get-Content .\build\generated\version.txt -Raw).Trim()
    $named = "DesktopSplitterSetup-$version.exe"
    Copy-Item $setupExe $named -Force
    Write-Host "Setup exe ready: $named ($([math]::Round((Get-Item $named).Length/1MB,1)) MB)"
    & ".\$named" /verify | Write-Host
    Write-Host "verify exit code: $LASTEXITCODE"
    if ($LASTEXITCODE -eq 0) { $finalExit = 0 }
} else {
    Write-Host 'Setup exe NOT produced.'
}

try { Stop-Transcript | Out-Null } catch { }
exit $finalExit
