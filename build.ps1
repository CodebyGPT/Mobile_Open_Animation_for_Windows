# ============================================================================
#  Build Mobile Open Animation with the portable .NET SDK in .toolchain\dotnet
#
#  One build, not two. There is nothing to choose between: the diagnostic code is in
#  every build and is switched on and off at run time by Debug mode in the tray menu, so
#  the exe that is shipped is the exe that was tested. What used to be the difference
#  between a debug and a release build - whether the logging exists at all - is now the
#  difference between two states of the same file, and the state is a menu entry.
#
#  Everything the build touches stays inside this repository:
#    * no PATH change           (the SDK is invoked by full path)
#    * no registry writes
#    * no writes to your user profile - NuGet's cache and config, the CLI home and
#      APPDATA/LOCALAPPDATA are all redirected under .toolchain
#    * no installer was run; delete .toolchain and every trace is gone
#
#  Usage:
#    .\build.ps1                 # the build
#    .\build.ps1 -Run            # build, then launch
#
#  The exe is written to src\bin\Release\net48\MobileOpenAnimation.exe, and so are the
#  settings file and the log it writes when Debug mode is on - both live next to the exe.
# ============================================================================
[CmdletBinding()]
param(
    [switch]$Run,
    [string]$Project = 'src\Moa.csproj'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tc   = Join-Path $root '.toolchain'
$dn   = Join-Path $tc 'dotnet\dotnet.exe'

# The one configuration. Release rather than Debug because this is the build that ships, and a build
# whose optimizations are switched off is not the one to be testing behaviour in.
$Configuration = 'Release'

if (-not (Test-Path $dn)) {
    throw "Portable .NET SDK not found at:`n  $dn`nExtract the SDK zip so dotnet.exe sits at .toolchain\dotnet\dotnet.exe"
}

# ---- contain the build under .toolchain -------------------------------------------------
$env:DOTNET_ROOT                       = Join-Path $tc 'dotnet'
$env:DOTNET_CLI_HOME                   = Join-Path $tc 'clihome'
$env:NUGET_PACKAGES                    = Join-Path $tc 'nuget'
$env:APPDATA                           = Join-Path $tc 'appdata'
$env:LOCALAPPDATA                      = Join-Path $tc 'localappdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT       = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO                     = '1'
$env:DOTNET_CLI_UI_LANGUAGE            = 'en'

foreach ($d in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:APPDATA, $env:LOCALAPPDATA)) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

$proj = Join-Path $root $Project
if (-not (Test-Path $proj)) { throw "Project not found: $proj" }

$outDir = Join-Path $root "src\bin\$Configuration\net48"
$exe    = Join-Path $outDir 'MobileOpenAnimation.exe'

# Said before the build rather than after the failure, because the failure it causes is a file copy that
# cannot happen and the compiler's message for it says nothing about the program still running.
#
# The test is an exclusive open of the file, which is exactly what the copy needs and therefore cannot be
# wrong about it. It replaced a Get-Process by name, which answered "not running" here while an older copy of
# the program was running with the image mapped: this shell is not elevated and the running copy is, so the
# process could not be seen or listed at all. A scanner holding the file without running it is the same
# answer either way - the file is in use.
if (Test-Path $exe) {
    $held = $false
    try { [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None').Close() } catch { $held = $true }
    if ($held) {
        Write-Host "note   : the exe is in use, so the build will compile and then fail to replace it." -ForegroundColor Yellow
        Write-Host "         Exit the program from its tray menu, then run this again." -ForegroundColor Yellow
    }
}

Write-Host "SDK    : $(& $dn --version)" -ForegroundColor DarkGray
Write-Host "project: $Project ($Configuration)" -ForegroundColor DarkGray

& $dn build $proj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Run) {
    if (-not (Test-Path $exe)) { throw "Built exe not found: $exe" }
    & $exe
}
