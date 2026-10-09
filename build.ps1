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
    [string]$Project = 'src\Moa.csproj',
    # The commit this build should claim, for the case where git cannot be asked - a source zip, or git not
    # installed. Given it is used as it stands; left out, the script asks when there is someone to ask. A
    # build that is going to be published passes it with the id it will be published as, because the public
    # repository's history is not this one's and the id has to name something its readers can look up.
    [string]$Commit = ''
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

# The identity the program reports about itself, which it reads back out of the assembly (Build.cs), so that
# nothing in the source has to be kept in step with it.
#
# Local time, the clock the log's own timestamps use. The commit is short because the whole thing ends up in a
# file name. A tree with changes in it gets -dirty, because a build from such a tree is not the commit it
# names and a version that quietly claims otherwise is worse than none; untracked files are not counted, since
# the ones that live here sit outside the compiled directory and would mark every build dirty for nothing.
#
# The variable is not called $commit: PowerShell variable names do not distinguish case, so $commit and the
# -Commit parameter are one and the same, and clearing the working variable would clear the argument with it.
$stamp    = Get-Date -Format 'yyyy.MM.dd-HHmm'
$commitId = ''

if ($Commit) { $commitId = $Commit }
elseif (Test-Path (Join-Path $root '.git')) {
    $short = & git -C $root rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $short) {
        $commitId = $short.Trim()
        if (& git -C $root status --porcelain --untracked-files=no 2>$null) { $commitId += '-dirty' }
    }
}

if (-not $commitId) {
    # No git to ask. Better to ask the person running the build than to write a version that names nothing:
    # the whole point of the number is to say which source produced this exe.
    #
    # Asked only when there is a terminal to ask on. A redirected stdin means a script or a service is driving
    # the build, and a prompt there would hang it rather than inform anyone.
    if ([Console]::IsInputRedirected) {
        Write-Host "note   : no git here, so this build's version will say nogit." -ForegroundColor Yellow
        Write-Host "         Pass -Commit <id> to name it yourself." -ForegroundColor Yellow
        $commitId = 'nogit'
    }
    else {
        Write-Host "note   : no git commit could be read here." -ForegroundColor Yellow
        $manual = Read-Host "         commit id for this build (Enter to leave it as nogit)"
        # Kept to characters that are safe in a file name and in the command line it is passed on.
        $clean = ($manual -replace '[^A-Za-z0-9._-]', '-').Trim('-')
        $commitId = if ($clean) { $clean } else { 'nogit' }
    }
}

$version = "$stamp-$commitId"
Write-Host "version: $version" -ForegroundColor DarkGray

& $dn build $proj -c $Configuration --nologo "-p:InformationalVersion=$version" "-p:Version=$(Get-Date -Format 'yyyy.M.d')"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Run) {
    if (-not (Test-Path $exe)) { throw "Built exe not found: $exe" }
    & $exe
}
