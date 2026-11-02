# DesktopSplitter cloud build driver v3 (lives outside .github/workflows so
# it can be updated without editing the workflow file).
# Logging: transcript -> ci-log.txt. After the standalone driver build the
# transcript is stopped, the driver build output is appended, and the
# transcript resumes in append mode - so the driver errors always land in
# the committed log even when build.ps1 throws later.
# The workflow commits ci-log.txt to the ci-logs branch after every run.
$ErrorActionPreference = 'Continue'

$logPath = Join-Path $PWD 'ci-log.txt'
try { Start-Transcript -Path $logPath -Force | Out-Null } catch { }

function Section([string]$t) { Write-Host "`n########## $t ##########" }

Section 'ENVIRONMENT'
Write-Host "PSVersion: $($PSVersionTable.PSVersion)"
Write-Host "OS: $([Environment]::OSVersion.VersionString)"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) { & $vswhere -all -products * -format json } else { Write-Host 'vswhere NOT FOUND' }
if (Get-Command dotnet -ErrorAction SilentlyContinue) { dotnet --list-sdks } else { Write-Host 'dotnet NOT FOUND' }
Write-Host 'Windows Kits Include dirs:'
Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Include" -Directory -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $($_.Name)" }

Section 'INSTALL WINDOWS SDK 10.0.26100 (if missing)'
if (Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Include\10.0.26100.0") {
    Write-Host 'SDK 26100 already present, skipping'
} else {
    winget install --id Microsoft.WindowsSDK.10.0.26100 --exact --accept-source-agreements --accept-package-agreements --disable-interactivity
    Write-Host "winget SDK exit code: $LASTEXITCODE"
}

Section 'INSTALL WDK 10.0.26100'
winget install --id Microsoft.WindowsWDK.10.0.26100 --exact --accept-source-agreements --accept-package-agreements --disable-interactivity
Write-Host "winget WDK exit code: $LASTEXITCODE"

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
    Write-Host 'MSVC versions + Spectre libs:'
    Get-ChildItem "$vs\VC\Tools\MSVC" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $spectre = Test-Path (Join-Path $_.FullName 'lib\spectre\x64')
        Write-Host "  MSVC $($_.Name) spectre-x64-libs: $spectre"
    }
} else {
    Write-Host 'No VS installation found!'
}

Section 'DRIVER BUILD (standalone, full log captured)'
$msbuild = $null
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
}
Write-Host "msbuild: $msbuild"
$driverExit = $null
if ($msbuild) {
    $dp = Start-Process -FilePath $msbuild -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput 'driver-build.log' -RedirectStandardError 'driver-build.err' `
        -ArgumentList 'driver\DesktopSplitterVdd.vcxproj', '/p:Configuration=Release', '/p:Platform=x64', '/p:Inf2CatUseLocalTime=true', '/v:n', '/nologo'
    $driverExit = $dp.ExitCode
    Write-Host "driver standalone exit code: $driverExit"
    Write-Host 'driver package dir:'
    Get-ChildItem 'driver\x64\Release\DesktopSplitterVdd' -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $($_.Name) $($_.Length)" }
} else {
    Write-Host 'msbuild NOT FOUND'
}

# --- flush driver build output into the committed log, no matter what ---
try { Stop-Transcript | Out-Null } catch { }
try {
    Add-Content -Path $logPath -Value "`n########## DRIVER BUILD LOG (stdout, last 600 lines) ##########"
    if (Test-Path 'driver-build.log') { Get-Content 'driver-build.log' -Tail 600 | Add-Content -Path $logPath }
    Add-Content -Path $logPath -Value "`n########## DRIVER BUILD LOG (stderr, last 200 lines) ##########"
    if (Test-Path 'driver-build.err') { Get-Content 'driver-build.err' -Tail 200 | Add-Content -Path $logPath }
} catch { }
try { Start-Transcript -Path $logPath -Append | Out-Null } catch { }

$finalExit = 1
if ($driverExit -ne 0) {
    Section 'SKIP build.ps1 (driver already failed standalone)'
    Write-Host "driver standalone exit code: $driverExit - see DRIVER BUILD LOG above."
} else {
    Section 'BUILD (build.ps1, Release, NoVersionBump)'
    try {
        & .\build.ps1 -Configuration Release -NoVersionBump
        Write-Host "build.ps1 exit code: $LASTEXITCODE"
    } catch {
        Write-Host "build.ps1 threw: $($_.Exception.Message)"
    }

    Section 'RESULT'
    $setupExe = 'installer\setup\bin\x64\Release\DesktopSplitterSetup.exe'
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
}

try { Stop-Transcript | Out-Null } catch { }
exit $finalExit
