# Launches the completed checkout build through the local signed development release channel and application stub.
# The persistent MCP front follows activated builds while previous workers retain their owned Worlds.
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/cli-build.ps1"

function Write-Note([string] $message) {
    [Console]::Error.WriteLine("puck-mcp: $message")
}

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$source = $root
$build = Get-CliBuild $root

if ($null -eq $build) {
    $main = Get-MainCheckout $root
    if ($main -and ($main -ne $root)) {
        $mainBuild = Get-CliBuild $main
        if ($null -ne $mainBuild) {
            $refusal = Get-MainBuildRefusal $root $main $mainBuild
            if ($null -eq $refusal) {
                $build = $mainBuild
                $source = $main
                Write-Note "this worktree has no Release build of src/Puck.Cli; using the main checkout's at $build, built from the same sources."
            } else {
                Write-Note "this worktree has no Release build of src/Puck.Cli, and the main checkout's is not used: $refusal."
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

# Only the small BCL stub is bootstrapped here. Application publication, signature verification, staging and
# activation use the launcher release chain; no private application-copy staging lives in this hook.
$install = Join-Path $source '.claude/mcp-install'
$stubBuild = Join-Path $source 'src/Puck.Launcher.Stub/bin/Release/net11.0'
& dotnet build (Join-Path $source 'src/Puck.Launcher.Stub/Puck.Launcher.Stub.csproj') -c Release -nodeReuse:false 2>&1 | ForEach-Object { [Console]::Error.WriteLine($_) }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$null = New-Item -ItemType Directory -Force -Path $install
foreach ($file in Get-ChildItem -LiteralPath $stubBuild -File) {
    $target = Join-Path $install $file.Name
    if ((Test-Path -LiteralPath $target) -and ((Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash)) { continue }
    # A stub another client is running keeps its files locked; the next start after it exits takes the new build.
    try { Copy-Item -LiteralPath $file.FullName -Destination $target -Force } catch { Write-Note "kept the running stub's $($file.Name): $($_.Exception.Message)" }
}
'{"AppExecutableFileName":"Puck.Cli.exe","MaxAttempts":3}' | Set-Content -LiteralPath (Join-Path $install 'stub.json') -NoNewline
$dll = Join-Path $build 'Puck.Cli.dll'
& dotnet $dll mcp --profile operator --prepare-install --install-root $install --build-source $build 2>&1 | ForEach-Object { [Console]::Error.WriteLine($_) }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$stub = Join-Path $install 'Puck.Launcher.Stub.exe'
$arguments = (@('mcp', '--profile', 'operator', '--install-root', $install, '--build-source', $build, '--checkout', $source) + @($args) | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
$process = Start-Process -FilePath $stub -ArgumentList $arguments -NoNewWindow -Wait -PassThru
exit $process.ExitCode
