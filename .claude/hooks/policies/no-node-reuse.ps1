#Requires -Version 7.0
# no-node-reuse: a reused MSBuild node outlives the build and holds memory on the shared machine. The harnesses set
# MSBUILDDISABLENODEREUSE=1 where they can (Claude's settings env, Codex's shell_environment_policy); Antigravity has
# no project environment, so the flag stays required on every build entry point until that reaches all three.

$script:NodeReuseVerbs = @('build', 'msbuild', 'restore', 'pack', 'publish')
$script:NodeReuseReason = 'dotnet build, msbuild, restore, pack and publish pass -nodeReuse:false (or -nr:false); reused MSBuild nodes retain memory on the shared machine. dotnet test takes no such flag: build first, then dotnet test --no-build.'

function Test-NoNodeReusePolicy {
    param([hashtable] $Context, $Invocation)
    $argv = $Invocation.Argv
    if ($Invocation.Name -eq 'msbuild') { $rest = @($argv | Select-Object -Skip 1) }
    elseif ($Invocation.Name -eq 'dotnet' -and $argv.Count -ge 2 -and $argv[1] -in $script:NodeReuseVerbs) { $rest = @($argv | Select-Object -Skip 2) }
    else { return }
    if (@($rest | Where-Object { $_ -in @('--help', '-h', '-?', '/?', '/h', '-help', '/help') }).Count) { return }
    if ($Invocation.Env['MSBUILDDISABLENODEREUSE'] -eq '1') { return }
    $flags = @($rest | Where-Object { $_ -match '^(-{1,2}|/)(nodeReuse|nr):' })
    if (-not $flags.Count -or @($flags | Where-Object { $_ -notmatch ':false$' }).Count) {
        return New-PolicyDecision 'deny' $script:NodeReuseReason
    }
}
