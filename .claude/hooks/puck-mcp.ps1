# Starts the operator MCP adapter from a Release build of this checkout's src/Puck.Cli, so every agent drives the
# head's adapter. A linked worktree with no build of its own uses its main checkout's build, the one the live World
# runs beside; with no build at all, the checkout's CLI is built first. The installed `puck` tool is never used: it
# keeps the commit it was installed at. The build is copied to .claude/mcp-cli/<build stamp>/ first: a running adapter
# then locks only its copy, never the build output, and each new build gets a fresh copy the next time a client starts
# the server. Standard output is the MCP channel, so everything this script says goes to stderr.
$ErrorActionPreference = 'Stop'

function Write-Note([string] $message) {
    [Console]::Error.WriteLine("puck-mcp: $message")
}

function Get-CliBuild([string] $checkout) {
    $build = Join-Path $checkout 'src/Puck.Cli/bin/Release/net11.0'
    if (Test-Path -LiteralPath (Join-Path $build 'Puck.Cli.dll')) {
        return $build
    }
    return $null
}

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$source = $root
$build = Get-CliBuild $root

if ($null -eq $build) {
    $common = & git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null
    if (($LASTEXITCODE -eq 0) -and $common) {
        $main = (Resolve-Path -LiteralPath (Split-Path -Parent $common)).Path
        if ($main -ne $root) {
            $build = Get-CliBuild $main
            if ($null -ne $build) {
                $source = $main
                Write-Note "this worktree has no Release build of src/Puck.Cli; using the main checkout's at $build."
            }
        }
    }
}

if ($null -eq $build) {
    # One build at a time per checkout: several clients (Claude Code, Codex lanes, VS Code) can start the server at once.
    # The name hashes the path with SHA-256 because string hash codes differ between processes.
    $key = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant()))).Substring(0, 16)
    $mutex = [System.Threading.Mutex]::new($false, "Global\puck-mcp-build-$key")
    try {
        [void] $mutex.WaitOne()
    } catch [System.Threading.AbandonedMutexException] { }
    try {
        $build = Get-CliBuild $root
        if ($null -eq $build) {
            Write-Note "no Release build of src/Puck.Cli in $root; building it."
            & dotnet build (Join-Path $root 'src/Puck.Cli/Puck.Cli.csproj') -c Release -nodeReuse:false 2>&1 | ForEach-Object { [Console]::Error.WriteLine($_) }
            $build = Get-CliBuild $root
            if (($LASTEXITCODE -ne 0) -or ($null -eq $build)) {
                Write-Note "building src/Puck.Cli failed (exit $LASTEXITCODE); fix the build and restart the server."
                exit 1
            }
        }
    } finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
}

# The stamp is the newest file in the build, so a rebuild that changes only a dependency (Puck.Mcp.dll) still makes a
# fresh copy.
$cache = Join-Path $source '.claude/mcp-cli'
$stamp = (Get-ChildItem -LiteralPath $build -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc.Ticks.ToString()
$copy = Join-Path $cache $stamp
if (-not (Test-Path -LiteralPath (Join-Path $copy 'Puck.Cli.dll'))) {
    $staging = "$copy.$PID.partial"
    & robocopy $build $staging /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        Write-Note "copying $build failed (robocopy $LASTEXITCODE)."
        exit 1
    }
    try {
        Move-Item -LiteralPath $staging -Destination $copy
    } catch {
        # Another client made the same copy first.
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# An earlier copy goes only once no adapter runs from it: a running adapter maps its Puck.Cli.dll, so that file cannot
# be opened exclusively. Deleting a held copy would remove the assemblies it has not loaded yet.
Get-ChildItem -LiteralPath $cache -Directory | Where-Object { $_.Name -like '*.partial' -and $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-1) } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $cache -Directory | Where-Object { ($_.Name -ne $stamp) -and ($_.Name -notlike '*.partial') } | ForEach-Object {
    $entry = Join-Path $_.FullName 'Puck.Cli.dll'
    try {
        if (Test-Path -LiteralPath $entry) {
            [System.IO.File]::Open($entry, 'Open', 'ReadWrite', 'None').Dispose()
        }
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    } catch { }
}

$process = Start-Process -FilePath 'dotnet' -ArgumentList (@((Join-Path $copy 'Puck.Cli.dll'), 'mcp', '--profile', 'operator') + $args) -NoNewWindow -Wait -PassThru
exit $process.ExitCode
