#Requires -Version 7.0
# Locates a checkout's completed Release build of src/Puck.Cli and the main checkout behind a worktree. Dot-sourced by
# puck-mcp.ps1 and provenance.ps1, never registered.

# Returns the checkout's Release build directory of src/Puck.Cli when a build completed there (Puck.Cli.csproj writes
# puck-build-ready after a successful build and deletes it before the next), or $null.
function Get-CliBuild([string] $checkout) {
    $build = Join-Path $checkout 'src/Puck.Cli/bin/Release/net11.0'
    if ((Test-Path -LiteralPath (Join-Path $build 'Puck.Cli.dll')) -and (Test-Path -LiteralPath (Join-Path $build 'puck-build-ready'))) {
        return $build
    }
    return $null
}

# Returns the main checkout (the parent of git's common directory) of the checkout holding $directory, or $null.
function Get-MainCheckout([string] $directory) {
    $common = & git -C $directory rev-parse --path-format=absolute --git-common-dir 2>$null
    if (($LASTEXITCODE -ne 0) -or -not $common) { return $null }
    return (Resolve-Path -LiteralPath (Split-Path -Parent $common)).Path
}
