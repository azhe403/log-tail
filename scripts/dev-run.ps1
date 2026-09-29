<#
.SYNOPSIS
    Rebuild and launch the LogTail desktop app, cleaning up stale processes first.

.DESCRIPTION
    The full dev loop in one command: kill any leftover LogTail / test processes
    belonging to this repo, build, then start the app and block until it exits.

    Processes are only killed when their executable lives inside this repository,
    so a test run from a different project on the same machine is never disturbed.

.PARAMETER Configuration
    Build configuration to use. Matches the folder under bin\. Default: Debug.

.PARAMETER NoBuild
    Skip the build step and launch whatever is already in bin\.

.PARAMETER NoWait
    Start the app and return immediately instead of waiting for it to exit.

.EXAMPLE
    .\scripts\dev-run.ps1
    Kills stale processes, builds Debug, launches, waits until you close the app.

.EXAMPLE
    .\scripts\dev-run.ps1 -NoWait
    Relaunch without blocking, so the agent can continue working.

.EXAMPLE
    .\scripts\dev-run.ps1 -Configuration Release -NoBuild
    Relaunch the already-built Release binary.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $NoBuild,

    [switch] $NoWait
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot      = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$uiProject     = Join-Path $repoRoot 'src\LogTail.UI\LogTail.UI.csproj'
$binRoot       = Join-Path $repoRoot 'src\LogTail.UI\bin'

# Process names that can hold a lock on LogTail's output assemblies.
$knownProcesses = @('LogTail', 'LogTail.UI.Tests', 'LogTail.Core.Tests', 'testhost', 'vstest.console')

function Stop-StaleRepoProcesses {
    param([string] $Root)

    $stopped = @()

    $candidates = Get-Process -Name $knownProcesses -ErrorAction SilentlyContinue
    foreach ($process in $candidates) {
        # -Path needs elevation for processes owned by another user; treat a failure
        # as "not ours" rather than letting it abort the whole loop.
        $path = $null
        try { $path = $process.Path } catch { $path = $null }

        if (-not $path) { continue }
        if (-not $path.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase)) { continue }

        $stopped += "$($process.Name) (pid $($process.Id))"
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not stop $($process.Name) (pid $($process.Id)): $_"
        }
    }

    if ($stopped.Count -gt 0) {
        Write-Host "Stopped stale: $($stopped -join ', ')" -ForegroundColor Yellow
        Start-Sleep -Milliseconds 500   # let the OS release file handles
    }
}

function Invoke-Build {
    param([string] $Project, [string] $Config)

    Write-Host "Building ($Config)..." -ForegroundColor Cyan
    & dotnet build $Project -c $Config --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE. Not launching."
    }
}

function Resolve-AppExecutable {
    param([string] $BinPath, [string] $Config)

    $expected = Get-ChildItem -Path $BinPath -Recurse -Filter 'LogTail.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\$Config\net*" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($expected) { return $expected.FullName }

    throw "No LogTail.exe for '$Config' under $BinPath. Run without -NoBuild, or build the project first."
}

Write-Host 'LogTail dev loop' -ForegroundColor White
Write-Host "  repo:  $repoRoot"
Write-Host "  build: $(if ($NoBuild) { 'skipped' } else { $Configuration })"

Stop-StaleRepoProcesses -Root $repoRoot

if (-not $NoBuild) {
    Invoke-Build -Project $uiProject -Config $Configuration
}

$exe = Resolve-AppExecutable -BinPath $binRoot -Config $Configuration
Write-Host "Launching $exe" -ForegroundColor Green

$app = Start-Process -FilePath $exe -PassThru

# Touch .Handle so the process handle is cached. Without it, .ExitCode throws
# "No process is associated with this object" after WaitForExit().
$null = $app.Handle

if ($NoWait) {
    Write-Host "Running in background (pid $($app.Id))." -ForegroundColor DarkGray
    return
}

$app.WaitForExit()
Write-Host "LogTail exited with code $($app.ExitCode)." -ForegroundColor Cyan
