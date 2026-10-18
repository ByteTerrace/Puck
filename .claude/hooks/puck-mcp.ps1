# Starts the operator MCP adapter from this checkout's own Release build of src/Puck.Cli, so agents drive the head's
# adapter rather than an installed `puck` that lags it. The build is copied to .claude/mcp-cli/<build stamp>/ first:
# a running adapter then locks only its copy, never the build output, and each new build gets a fresh copy the next
# time a client starts the server. Standard output is the MCP channel, so everything this script says goes to stderr.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$build = Join-Path $root 'src/Puck.Cli/bin/Release/net11.0'
$entry = Join-Path $build 'Puck.Cli.dll'
$adapterArgs = @('mcp', '--profile', 'operator') + $args

if (-not (Test-Path -LiteralPath $entry)) {
    [Console]::Error.WriteLine("puck-mcp: no Release build at $build; running the installed puck. Build src/Puck.Cli in Release and restart the server.")
    $process = Start-Process -FilePath 'puck' -ArgumentList $adapterArgs -NoNewWindow -Wait -PassThru
    exit $process.ExitCode
}

$cache = Join-Path $root '.claude/mcp-cli'
$stamp = (Get-Item -LiteralPath $entry).LastWriteTimeUtc.Ticks.ToString()
$copy = Join-Path $cache $stamp
if (-not (Test-Path -LiteralPath (Join-Path $copy 'Puck.Cli.dll'))) {
    $staging = "$copy.partial"
    & robocopy $build $staging /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        [Console]::Error.WriteLine("puck-mcp: copying $build failed (robocopy $LASTEXITCODE).")
        exit 1
    }
    Move-Item -LiteralPath $staging -Destination $copy -Force
}

# Earlier copies go once no running adapter holds them; a held one stays until its adapter exits.
Get-ChildItem -LiteralPath $cache -Directory | Where-Object Name -ne $stamp | ForEach-Object {
    Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
}

$process = Start-Process -FilePath 'dotnet' -ArgumentList (@((Join-Path $copy 'Puck.Cli.dll')) + $adapterArgs) -NoNewWindow -Wait -PassThru
exit $process.ExitCode
