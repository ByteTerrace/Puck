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

# What src/Puck.Cli builds from: every project under src and build, and the repository-wide build inputs.
$script:CliBuildInputs = @('src', 'build', 'lib', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'nuget.config', '.editorconfig')

# Returns why the main checkout's build cannot stand in for the asking checkout's, or $null when it can: both hold the
# same committed build inputs with nothing changed or untracked under them, and the main checkout's build completed
# after its HEAD last moved, so the build is of those inputs and not of an older head. The status reads take no
# optional lock, so reading the main checkout never rewrites its index.
function Get-MainBuildRefusal([string] $root, [string] $main, [string] $build) {
    $mine = & git -C $root ls-tree HEAD -- @script:CliBuildInputs 2>$null
    if ($LASTEXITCODE -ne 0) { return "git could not read this checkout's HEAD" }
    $theirs = & git -C $main ls-tree HEAD -- @script:CliBuildInputs 2>$null
    if ($LASTEXITCODE -ne 0) { return "git could not read the main checkout's HEAD" }
    if ((@($mine) -join "`n") -cne (@($theirs) -join "`n")) { return 'its committed sources differ from this checkout''s' }
    foreach ($checkout in @($root, $main)) {
        $changed = & git --no-optional-locks -C $checkout status --porcelain --untracked-files=all -- @script:CliBuildInputs 2>$null
        if (($LASTEXITCODE -ne 0) -or $changed) { return "$checkout has uncommitted source changes" }
    }
    $moved = & git -C $main log -g -1 --date=unix --format=%gd HEAD 2>$null
    if (($LASTEXITCODE -ne 0) -or ($moved -notmatch '\{(\d+)\}')) { return 'its HEAD reflog does not say when HEAD last moved' }
    $builtAt = [DateTimeOffset]::new((Get-Item -LiteralPath (Join-Path $build 'puck-build-ready')).LastWriteTimeUtc).ToUnixTimeSeconds()
    if ($builtAt -lt [long]$Matches[1]) { return 'its build predates its current HEAD' }
    return $null
}

# Returns the main checkout (the parent of git's common directory) of the checkout holding $directory, or $null.
function Get-MainCheckout([string] $directory) {
    $common = & git -C $directory rev-parse --path-format=absolute --git-common-dir 2>$null
    if (($LASTEXITCODE -ne 0) -or -not $common) { return $null }
    return (Resolve-Path -LiteralPath (Split-Path -Parent $common)).Path
}
