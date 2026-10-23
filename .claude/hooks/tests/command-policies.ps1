#Requires -Version 7.0
# CPU-only check of the command-policy hooks. Every case runs the dispatcher in process for each payload shape its
# dialect reaches: the Claude lead and a Claude subagent (Bash or PowerShell), a Codex lane (shell tool and native
# exec_command with workdir) and an Antigravity lane (workspacePaths). The lead's verdict and the lane's verdict are
# separate columns. A second part runs the real scripts as processes (agent-policy.ps1 per tool,
# bash-transport.ps1, prose-doctrine.ps1). It executes no candidate command: the git
# it runs only builds and reads the scratch repositories it creates.
$ErrorActionPreference = 'Stop'
$hooks = Split-Path -Parent $PSScriptRoot
. "$hooks/command-policy.ps1"

$scratch = (Join-Path ([IO.Path]::GetTempPath()) "puck-hook-tests-$PID").Replace('\', '/')
function Invoke-ScratchGit {
    param([string] $Directory, [string[]] $Arguments)
    $output = & git -C $Directory @Arguments 2>&1
    if ($LASTEXITCODE) { throw "git $($Arguments -join ' ') in $($Directory): $output" }
    return $output
}
function New-ScratchRepository {
    param([string] $Name, [string] $Branch)
    $directory = "$scratch/$Name"
    $null = New-Item -ItemType Directory -Force $directory
    Invoke-ScratchGit $directory @('init', '-q', '-b', 'main') | Out-Null
    # Background maintenance in a scratch repository can outlive the test.
    foreach ($pair in @(@('maintenance.auto', 'false'), @('gc.auto', '0'), @('user.name', 'Hook Test'), @('user.email', 'hook@example.invalid'), @('commit.gpgsign', 'false'))) {
        Invoke-ScratchGit $directory (@('config') + $pair) | Out-Null
    }
    [IO.File]::WriteAllText("$directory/a.txt", 'a')
    Invoke-ScratchGit $directory @('add', 'a.txt') | Out-Null
    Invoke-ScratchGit $directory @('commit', '-q', '-m', 'test: first') | Out-Null
    if ($Branch -ne 'main') { Invoke-ScratchGit $directory @('checkout', '-q', '-b', $Branch) | Out-Null }
    Invoke-ScratchGit $directory @('remote', 'add', 'origin', "$scratch/nowhere.git") | Out-Null
    Invoke-ScratchGit $directory @('config', "branch.$Branch.remote", 'origin') | Out-Null
    Invoke-ScratchGit $directory @('config', "branch.$Branch.merge", "refs/heads/$Branch") | Out-Null
    Invoke-ScratchGit $directory @('update-ref', "refs/remotes/origin/$Branch", 'HEAD') | Out-Null
    return $directory
}

$null = New-Item -ItemType Directory -Force $scratch
$topic = New-ScratchRepository 'topic' 'topic'
$onMain = New-ScratchRepository 'on-main' 'main'
$mapped = New-ScratchRepository 'mapped' 'topic'
Invoke-ScratchGit $mapped @('config', 'remote.origin.push', 'refs/heads/*:refs/heads/*') | Out-Null
Invoke-ScratchGit $topic @('config', 'alias.p', 'push') | Out-Null
Invoke-ScratchGit $topic @('config', 'alias.ship', '!git push origin main') | Out-Null
Invoke-ScratchGit $topic @('config', 'alias.ci', 'commit') | Out-Null
[IO.File]::WriteAllText("$topic/b.txt", 'b')
Invoke-ScratchGit $topic @('add', 'b.txt') | Out-Null
Invoke-ScratchGit $topic @('commit', '-q', '-m', "test: borrowed`n`nCo-Authored-By: A <a@example.invalid>") | Out-Null
$trailerCommit = (Invoke-ScratchGit $topic @('rev-parse', 'HEAD')).Trim()
[IO.File]::WriteAllText("$topic/c.txt", 'c')
Invoke-ScratchGit $topic @('add', 'c.txt') | Out-Null
Invoke-ScratchGit $topic @('commit', '-q', '-m', 'test: clean') | Out-Null
$cleanCommit = (Invoke-ScratchGit $topic @('rev-parse', 'HEAD')).Trim()
[IO.File]::WriteAllText("$onMain/d.txt", 'd')
Invoke-ScratchGit $onMain @('add', 'd.txt') | Out-Null
Invoke-ScratchGit $onMain @('commit', '-q', '-m', "test: amended`n`nCo-Authored-By: A <a@example.invalid>") | Out-Null
$null = New-Item -ItemType Directory -Force "$topic/sub", "$topic/.agents", "$scratch/src/Puck.World"
[IO.File]::WriteAllText("$topic/trailer.txt", "cli: test`n`nCo-Authored-By: Example <example@example.invalid>`n")
# msg.txt carries the trailer in the repository root and not in sub/, so only a resolved cd or -C passes it.
[IO.File]::WriteAllText("$topic/msg.txt", "cli: test`n`nCo-Authored-By: Example <example@example.invalid>`n")
[IO.File]::WriteAllText("$topic/sub/msg.txt", "cli: test`n")
[IO.File]::WriteAllText("$topic/clean.txt", "cli: test`n")
[IO.File]::WriteAllText("$scratch/src/Puck.World/Puck.World.csproj", '<Project />')
$long = 'echo ' + ('x' * 2100)

# Command, lead verdict, lane verdict, dialect (any, posix, pwsh or pwsh-known), working directory, and the lane verdict
# for a payload that names no shell when its stricter POSIX reading differs.
$cases = [Collections.Generic.List[object]]::new()
function Add-Case {
    param([string] $Policy, [string] $Command, [string] $Lead, [string] $Lane, [string] $Dialect = 'any', [string] $Cwd = $topic, [string] $Unknown = $Lane)
    $cases.Add([pscustomobject]@{ Policy = $Policy; Command = $Command; Lead = $Lead; Lane = $Lane; Dialect = $Dialect; Cwd = $Cwd; Unknown = $Unknown })
}

# world-devices
Add-Case world-devices 'dotnet run --project src/Puck.World -- --world worlds/debug' deny deny
Add-Case world-devices 'dotnet run --project src/Puck.World -- --devices none' pass pass
Add-Case world-devices 'dotnet "path with spaces/Puck.World.dll" --devices none' pass pass
Add-Case world-devices 'dotnet src/Puck.World/bin/Release/net11.0/Puck.World.dll' deny deny
Add-Case world-devices 'dotnet run --project=src/Puck.World/Puck.World.csproj -- --devices=none' pass pass
Add-Case world-devices 'dotnet run --project src/Puck.World -- --devices none --devices all' deny deny
Add-Case world-devices 'dotnet Puck.World.dll --devices none; dotnet Puck.World.dll' deny deny
Add-Case world-devices 'puck canary' pass pass
Add-Case world-devices 'dotnet build src/Puck.World -nodeReuse:false' pass pass
Add-Case world-devices 'dotnet exec Puck.World.dll' deny deny
Add-Case world-devices 'dotnet exec --runtimeconfig r.json Puck.World.dll' deny deny
Add-Case world-devices 'dotnet exec --runtimeconfig r.json Puck.World.dll --devices none' pass pass
Add-Case world-devices 'dotnet src/Puck.World/bin/Release/net11.0/Puck.World.dll --world worlds/debug --devices all' pass pass
Add-Case world-devices 'dotnet src/Puck.World/bin/Release/net11.0/Puck.World.dll --devices some' deny deny
Add-Case world-devices 'puck lease gpu --holder test -- dotnet Puck.World.dll' deny deny
Add-Case world-devices 'echo dotnet Puck.World.dll' pass pass
Add-Case world-devices 'env Puck.World --world worlds/debug' deny deny posix
Add-Case world-devices 'dotnet run --project Puck.World.csproj' deny deny
Add-Case world-devices 'cd src/Puck.World && dotnet run' deny deny any $scratch
Add-Case world-devices 'cd src/Puck.World && dotnet run -- --devices none' pass pass any $scratch
Add-Case world-devices 'Start-Process dotnet -ArgumentList "src/Puck.World/bin/Puck.World.dll"' deny deny pwsh
Add-Case world-devices 'Start-Process dotnet -ArgumentList "src/Puck.World/bin/Puck.World.dll","--devices","none"' pass pass pwsh
# no-node-reuse, and the tokenizer cases that reach it
Add-Case no-node-reuse 'dotnet build src/Puck.Cli' deny deny
Add-Case no-node-reuse 'dotnet test tests/Puck.Cli.Core.Tests --no-build' pass pass
Add-Case no-node-reuse 'dotnet msbuild Puck.slnx' deny deny
Add-Case no-node-reuse 'dotnet build src/Puck.Cli -nodeReuse:false' pass pass
Add-Case no-node-reuse 'dotnet build src/Puck.Cli /nodeReuse:false' pass pass
Add-Case no-node-reuse 'dotnet build src/Puck.Cli -nr:false' pass pass
Add-Case no-node-reuse 'dotnet build A -nodeReuse:false -nr:true' deny deny
Add-Case no-node-reuse 'dotnet build A -nodeReuse:false && dotnet build B' deny deny
Add-Case no-node-reuse 'dotnet restore Puck.slnx' deny deny
Add-Case no-node-reuse 'dotnet restore Puck.slnx -nodeReuse:false' pass pass
Add-Case no-node-reuse 'dotnet pack src/Puck.Cli' deny deny
Add-Case no-node-reuse 'dotnet publish src/Puck.Cli -nr:false' pass pass
Add-Case no-node-reuse 'dotnet build --help' pass pass
Add-Case no-node-reuse 'dotnet build -h' pass pass
Add-Case no-node-reuse 'dotnet --info' pass pass
Add-Case no-node-reuse 'echo dotnet build A' pass pass
Add-Case no-node-reuse '# dotnet build A' pass pass
Add-Case no-node-reuse '(dotnet build A)' deny deny
Add-Case no-node-reuse '$(dotnet build A)' deny deny posix
Add-Case no-node-reuse 'foreach ($x in 1) { dotnet build A }' deny deny pwsh
Add-Case no-node-reuse 'DOTNET_NOLOGO=1 dotnet build A' deny deny posix
Add-Case no-node-reuse 'MSBUILDDISABLENODEREUSE=1 dotnet build A' pass pass posix
Add-Case no-node-reuse 'pwsh -NoProfile -Command dotnet build A' deny deny
Add-Case no-node-reuse 'pwsh -Command "dotnet build A"' deny deny
Add-Case no-node-reuse 'bash -ec "dotnet build A"' deny deny
Add-Case no-node-reuse 'bash -o pipefail -c "dotnet build A"' deny deny
Add-Case no-node-reuse 'echo pwsh -Command "dotnet build A"' pass pass
Add-Case no-node-reuse 'dotnet build A 2>&1 | tee build.log' deny deny posix
Add-Case no-node-reuse 'dotnet build A -nodeReuse:false 2>&1 | tee build.log' pass pass posix
Add-Case no-node-reuse 'env -i FOO=1 dotnet build A' deny deny posix
Add-Case no-node-reuse 'env -u FOO dotnet build A' deny deny posix
Add-Case no-node-reuse 'timeout -k 5 60 dotnet build A' deny deny posix
Add-Case no-node-reuse 'nice -n 5 dotnet build A' deny deny posix
Add-Case no-node-reuse 'command dotnet build A' deny deny posix
Add-Case no-node-reuse 'command -v dotnet build A' pass pass posix
Add-Case no-node-reuse 'echo "`dotnet build A`"' deny deny posix
Add-Case no-node-reuse 'echo "$(dotnet build A)"' deny deny posix
Add-Case no-node-reuse "cat <<'EOF'`ndotnet build A`nEOF" pass pass posix
Add-Case no-node-reuse 'Start-Process dotnet -ArgumentList ''build'',''A''' deny deny pwsh
Add-Case no-node-reuse "@'`ndotnet build A`n'@ | Out-File notes.txt" pass pass pwsh
Add-Case no-node-reuse 'cmd /c "dotnet build A"' deny deny
# no-coauthor
Add-Case no-coauthor 'git commit -m "cli: change"' pass pass
Add-Case no-coauthor "git commit -m 'cli: change`n`nCo-Authored-By: A <a@example.invalid>'" deny deny
Add-Case no-coauthor 'git commit --trailer "Co-Authored-By=A <a@example.invalid>"' deny deny
Add-Case no-coauthor 'git commit -am "Co-Authored-By: A <a@example.invalid>"' deny deny
Add-Case no-coauthor 'git commit -sm "Co-Authored-By: A <a@example.invalid>"' deny deny
Add-Case no-coauthor 'git commit --file trailer.txt' deny deny
Add-Case no-coauthor 'git commit --file clean.txt' pass pass
Add-Case no-coauthor 'git commit -F missing.txt' deny deny
Add-Case no-coauthor 'git commit -F -' deny deny
Add-Case no-coauthor 'git commit -t trailer.txt' deny deny
Add-Case no-coauthor 'git commit -m "cli: remove Co-Authored-By documentation"' pass pass
Add-Case no-coauthor 'git log --grep="Co-Authored-By:"' pass pass
Add-Case no-coauthor 'git show commit -m "Co-Authored-By: example"' pass pass
Add-Case no-coauthor 'git commit -F msg.txt' deny deny
Add-Case no-coauthor 'git -C sub commit -F msg.txt' pass deny
Add-Case no-coauthor 'cd sub && git commit -F msg.txt' pass pass
# Read as POSIX, Set-Location is no cd, so git commits from the root, whose msg.txt carries the trailer.
Add-Case no-coauthor 'Set-Location sub; git commit -F msg.txt' pass pass pwsh $topic deny
Add-Case no-coauthor 'MSYS_NO_PATHCONV=1 git commit -m "x`nCo-Authored-By: a"' deny deny posix
Add-Case no-coauthor "git commit -m `"`$(cat <<'EOF'`nfix: don't crash`n`nCo-Authored-By: a`nEOF`n)`"" deny deny posix
Add-Case no-coauthor "git commit -m `"`$(cat <<'EOF'`nfix: don't crash`nEOF`n)`" && git status" pass pass posix
Add-Case no-coauthor 'git merge -m "Co-Authored-By: a" topic' deny deny
Add-Case no-coauthor 'git merge -F trailer.txt topic' deny deny
Add-Case no-coauthor 'git merge -s ours topic' pass pass
Add-Case no-coauthor 'git commit-tree HEAD^{tree} -m "Co-Authored-By: a"' deny deny
Add-Case no-coauthor "git cherry-pick $trailerCommit" deny deny
Add-Case no-coauthor "git cherry-pick $cleanCommit" pass pass
Add-Case no-coauthor "git revert $trailerCommit" pass pass
Add-Case no-coauthor 'git commit --amend --no-edit' pass pass
Add-Case no-coauthor 'git commit --amend --no-edit' deny deny any $onMain
Add-Case no-coauthor "git commit -C $trailerCommit" deny deny
Add-Case no-coauthor 'git ci -m "Co-Authored-By: a"' deny deny
# git-safety: pushes
Add-Case git-safety 'git push origin HEAD:refs/heads/lead/catch-up' pass deny
Add-Case git-safety 'git push origin main' ask deny
Add-Case git-safety 'git push origin domain' pass deny
Add-Case git-safety 'git push --dry-run origin main' ask deny
Add-Case git-safety 'git push origin HEAD:main' ask deny
Add-Case git-safety 'git push origin HEAD:refs/heads/main' ask deny
Add-Case git-safety 'git push origin topic' pass deny
Add-Case git-safety 'git push origin HEAD' pass deny
Add-Case git-safety 'git push origin HEAD' ask deny any $onMain
Add-Case git-safety 'git push' pass deny
Add-Case git-safety 'git push' ask deny any $onMain
Add-Case git-safety 'git push' ask deny any $mapped
Add-Case git-safety 'git push origin topic' ask deny any $mapped
Add-Case git-safety 'git status && git push' pass deny
Add-Case git-safety 'git push origin +topic' ask deny
Add-Case git-safety 'git push origin :topic' ask deny
Add-Case git-safety 'git push origin :' ask deny
Add-Case git-safety 'git push origin "refs/heads/*:refs/heads/*"' ask deny
Add-Case git-safety 'git push --all origin' ask deny
Add-Case git-safety 'git push --mirror origin' ask deny
Add-Case git-safety 'git push --delete origin topic' ask deny
Add-Case git-safety 'git push -f origin topic' ask deny
Add-Case git-safety 'git push -uf origin topic' ask deny
Add-Case git-safety 'git push --force-with-lease origin topic' ask deny
Add-Case git-safety 'git push --prune origin' ask deny
Add-Case git-safety 'git push -o ci.skip origin topic' pass deny
Add-Case git-safety 'git --no-pager push --dry-run origin HEAD:main' ask deny
Add-Case git-safety "git -C $onMain push" ask deny
Add-Case git-safety "git -C$onMain push" ask deny
Add-Case git-safety '(git push origin main)' ask deny
Add-Case git-safety 'echo "$(git push origin main)"' ask deny posix
Add-Case git-safety 'bash -c "git push origin main"' ask deny
Add-Case git-safety 'pwsh -Command "git push origin main"' ask deny
Add-Case git-safety 'Start-Process git -ArgumentList "push origin main"' ask deny pwsh
Add-Case git-safety 'git p origin main' ask deny
Add-Case git-safety 'git ship' ask deny
Add-Case git-safety 'git -c alias.q=push q origin main' ask deny
Add-Case git-safety 'echo git push' pass pass
Add-Case git-safety 'git commit -m "docs: explain git push origin main"' pass pass
Add-Case git-safety "cat <<'EOF'`ngit push origin main`nEOF" pass pass posix
Add-Case git-safety "@'`ngit push origin main`n'@ | Out-File notes.txt" pass pass pwsh
# git-safety: gh
Add-Case git-safety 'gh pr merge 12 --squash' ask deny
Add-Case git-safety 'gh repo sync' ask deny
Add-Case git-safety 'gh api repos/o/r/merges -f base=main -f head=x' ask deny
Add-Case git-safety 'gh api repos/o/r/git/refs/heads/main -X PATCH -f sha=x' ask deny
Add-Case git-safety 'gh api --method=DELETE repos/o/r/git/refs/heads/x' ask deny
Add-Case git-safety 'gh api repos/o/r/pulls' pass pass
Add-Case git-safety 'gh api -X GET repos/o/r/pulls -f state=open' pass pass
Add-Case git-safety "gh api graphql -f query='mutation { mergePullRequest(input: {}) { clientMutationId } }'" ask deny
Add-Case git-safety "gh api graphql -f query='query { viewer { login } }'" pass pass
Add-Case git-safety 'gh pr create --fill' pass deny
Add-Case git-safety 'gh pr view 12' pass pass
# git-safety: shared state, refused for a lane only
Add-Case git-safety 'git stash' pass deny
Add-Case git-safety 'git stash list' pass pass
Add-Case git-safety 'git stash show -p' pass pass
Add-Case git-safety 'git stash push -m wip' pass deny
Add-Case git-safety 'git stash pop' pass deny
Add-Case git-safety 'a=1; git stash' pass deny posix
Add-Case git-safety 'pwsh -Command "git stash"' pass deny
Add-Case git-safety 'git branch -a' pass pass
Add-Case git-safety 'git branch new-topic' pass pass
Add-Case git-safety 'git branch -u origin/topic' pass pass
Add-Case git-safety 'git branch --format="%(refname)"' pass pass
Add-Case git-safety 'git branch -D codex/old' pass deny
Add-Case git-safety 'git branch -Df codex/old' pass deny
Add-Case git-safety 'git branch --quiet -D codex/old' pass deny
Add-Case git-safety 'git branch --delete codex/old' pass deny
Add-Case git-safety 'git branch -m a b' pass deny
Add-Case git-safety 'git branch -c a b' pass deny
Add-Case git-safety 'git branch -f topic HEAD' pass deny
Add-Case git-safety '(git branch -D probe-x)' pass deny
Add-Case git-safety 'git checkout -B lead/catch-up' pass deny
Add-Case git-safety 'git checkout -b codex/new' pass pass
Add-Case git-safety 'git switch -C lead/catch-up' pass deny
Add-Case git-safety 'git switch --force-create lead/catch-up' pass deny
Add-Case git-safety 'git switch -c codex/new' pass pass
Add-Case git-safety 'git fetch origin main:main' pass deny
Add-Case git-safety 'git fetch origin +main:main' pass deny
Add-Case git-safety 'git fetch --prune' pass deny
Add-Case git-safety 'git fetch -p origin' pass deny
Add-Case git-safety 'git fetch origin' pass pass
Add-Case git-safety 'git fetch origin main' pass pass
Add-Case git-safety 'git fetch https://example.invalid/r.git main' pass pass
Add-Case git-safety 'git pull --prune' pass deny
Add-Case git-safety 'git worktree list' pass pass
Add-Case git-safety 'git worktree remove ../x' pass deny
Add-Case git-safety 'git worktree add ../x' pass deny
Add-Case git-safety 'git tag v1' pass pass
Add-Case git-safety 'git tag -a v1 -mfix' pass pass
Add-Case git-safety 'git tag -d v1' pass deny
Add-Case git-safety 'git tag -fa v1 -m x' pass deny
Add-Case git-safety 'git reflog show' pass pass
Add-Case git-safety 'git reflog expire --all' pass deny
Add-Case git-safety 'git update-ref refs/heads/x HEAD' pass deny
Add-Case git-safety 'git symbolic-ref HEAD' pass pass
Add-Case git-safety 'git symbolic-ref HEAD refs/heads/x' pass deny
Add-Case git-safety 'git gc' pass deny
Add-Case git-safety 'git rebase main' pass deny
Add-Case git-safety 'git replace -l' pass pass
Add-Case git-safety 'git remote -v' pass pass
Add-Case git-safety 'git remote add other ../x' pass deny
Add-Case git-safety 'git notes add -m x' pass deny
Add-Case git-safety 'git config --get user.name' pass pass
Add-Case git-safety 'git config user.name' pass pass
Add-Case git-safety 'git config --list' pass pass
Add-Case git-safety 'git config --global --get user.name' pass pass
Add-Case git-safety 'git config get user.name' pass pass
Add-Case git-safety 'git config user.name x' pass deny
Add-Case git-safety 'git config alias.p push' pass deny
Add-Case git-safety 'git config --unset user.name' pass deny
Add-Case git-safety 'git config set user.name x' pass deny
Add-Case git-safety 'git -C ../other status' pass deny
Add-Case git-safety 'git -C../other status' pass deny
Add-Case git-safety 'git -cfoo=bar status' pass deny
Add-Case git-safety 'git --git-dir=../x/.git status' pass deny
Add-Case git-safety 'GIT_DIR=../x/.git git status' pass deny posix
Add-Case git-safety 'git commit --file clean.txt' pass pass
Add-Case git-safety 'git reset --hard HEAD' pass pass
Add-Case git-safety 'git status' pass pass
# codex-launch
Add-Case codex-launch 'codex exec "plain brief"' pass pass
Add-Case codex-launch 'codex exec --sandbox danger-full-access "echo"' ask deny
Add-Case codex-launch 'codex exec -s danger-full-access "echo"' ask deny
Add-Case codex-launch 'codex exec --dangerously-bypass-approvals-and-sandbox "echo"' ask deny
Add-Case codex-launch 'codex exec --full-auto "echo"' ask deny
Add-Case codex-launch 'codex exec -c approval_policy=never "echo"' ask deny
Add-Case codex-launch 'codex exec --config=sandbox_mode=danger-full-access "echo"' ask deny
Add-Case codex-launch 'codex exec -c permissions.puck.network.enabled=true "echo"' ask deny
Add-Case codex-launch 'codex exec -c model_reasoning_effort=high "echo"' pass pass
Add-Case codex-launch 'codex -p fast exec "echo"' ask deny
Add-Case codex-launch 'codex execpolicy check --rules .codex/rules/git.rules git push' pass pass
# opaque-command
Add-Case opaque-command 'pwsh -EncodedCommand ZABpAHIA' ask deny
Add-Case opaque-command '$g = "git"; & $g push' ask deny pwsh
Add-Case opaque-command 'Invoke-Expression $script' ask deny pwsh
Add-Case opaque-command 'Invoke-Expression "git stash"' pass deny pwsh
Add-Case opaque-command 'echo "git stash" | bash' ask deny posix
Add-Case opaque-command 'eval "$CMD"' ask deny posix
Add-Case opaque-command 'eval "git stash list"' pass pass posix
Add-Case opaque-command '$GIT push' ask deny posix
# Operands known only at run time: an unresolved git verb, option or destination asks the lead and refuses a lane.
Add-Case git-safety '$verb = "stash"; git $verb' ask deny pwsh
Add-Case git-safety 'verb=stash; git $verb' ask deny posix
Add-Case git-safety 'git push x HEAD:$target' ask deny
Add-Case git-safety 'git push origin "$(git branch --show-current)"' ask deny posix
Add-Case git-safety 'git -C $dir push' ask deny
Add-Case git-safety 'git stash $sub' pass deny
Add-Case git-safety 'git branch $flag topic' pass deny
Add-Case git-safety 'git log -n $count' pass pass
Add-Case git-safety 'gh $group merge 1' ask deny
Add-Case git-safety 'gh api $endpoint' ask deny
Add-Case git-safety 'gh pr view $number' pass pass
# The dialect comes from the tool and its shell; an unknown shell is read both ways and the stricter reading wins.
Add-Case git-safety 'echo `git stash`' pass deny posix
Add-Case git-safety 'echo `git stash`' pass pass pwsh-known
Add-Case git-safety '$env:DOTNET_NOLOGO = 1; git status' pass pass pwsh
# Attached and = forms of Codex's short options.
Add-Case codex-launch 'codex exec -punsafe "hi"' ask deny
Add-Case codex-launch 'codex exec -p=unsafe "hi"' ask deny posix
Add-Case codex-launch 'codex -punsafe exec "hi"' ask deny
Add-Case codex-launch 'codex exec --profile=unsafe "hi"' ask deny
Add-Case codex-launch 'codex exec -sdanger-full-access "hi"' ask deny
Add-Case codex-launch 'codex exec -s=danger-full-access "hi"' ask deny posix
Add-Case codex-launch 'codex exec -capproval_policy=never "hi"' ask deny
Add-Case codex-launch 'codex exec -c=approval_policy=never "hi"' ask deny posix
Add-Case codex-launch 'codex exec -m gpt-6.1-sol "hi"' pass pass
# command with a combined option cluster holding v or V only reports how a name resolves.
Add-Case git-safety 'command -pv git stash' pass pass posix
Add-Case git-safety 'command -pV git stash' pass pass posix
Add-Case git-safety 'command -p git stash' pass deny posix

function Get-Verdict {
    param([string] $Reply)
    if (-not $Reply) { return 'pass' }
    $parsed = ConvertFrom-Json -InputObject $Reply -AsHashtable
    return $parsed['decision'] ?? $parsed['hookSpecificOutput']['permissionDecision']
}

# Codex and Antigravity payloads that name no shell are read both ways, so they join the posix and pwsh cases.
$unknownShell = @('Codex', 'CodexNative', 'Antigravity')
$shapes = @{
    posix        = @('ClaudeLeadBash', 'ClaudeSubagentBash', 'CodexBash') + $unknownShell
    pwsh         = @('ClaudeLeadPowerShell', 'ClaudeSubagentPowerShell', 'CodexPwsh') + $unknownShell
    'pwsh-known' = @('ClaudeLeadPowerShell', 'ClaudeSubagentPowerShell', 'CodexPwsh')
}
$shapes.any = @('ClaudeLeadBash', 'ClaudeSubagentBash', 'CodexBash') + $shapes.pwsh
$count = 0
$failures = [Collections.Generic.List[string]]::new()
try {
    foreach ($case in $cases) {
        foreach ($shape in $shapes[$case.Dialect]) {
            $tool = 'claude'
            $payload = switch -Wildcard ($shape) {
                'Claude*' {
                    $p = @{ session_id = 's'; hook_event_name = 'PreToolUse'; tool_name = ($shape -like '*Bash') ? 'Bash' : 'PowerShell'; tool_input = @{ command = $case.Command }; cwd = $case.Cwd }
                    if ($shape -like 'ClaudeSubagent*') { $p.agent_id = 'a1'; $p.agent_type = 'implementer' }
                    $p
                }
                'Codex' { $tool = 'codex'; @{ session_id = 's'; turn_id = 't'; hook_event_name = 'PreToolUse'; tool_name = 'Bash'; tool_input = @{ command = $case.Command }; cwd = $case.Cwd } }
                'CodexNative' { $tool = 'codex'; @{ session_id = 's'; turn_id = 't'; hook_event_name = 'PreToolUse'; tool_name = 'exec_command'; tool_input = @{ cmd = $case.Command; workdir = $case.Cwd }; cwd = $scratch } }
                'CodexBash' { $tool = 'codex'; @{ session_id = 's'; turn_id = 't'; hook_event_name = 'PreToolUse'; tool_name = 'exec_command'; tool_input = @{ cmd = $case.Command; workdir = $case.Cwd; shell = 'bash' }; cwd = $scratch } }
                'CodexPwsh' { $tool = 'codex'; @{ session_id = 's'; turn_id = 't'; hook_event_name = 'PreToolUse'; tool_name = 'exec_command'; tool_input = @{ cmd = $case.Command; workdir = $case.Cwd; shell = 'C:/Program Files/PowerShell/7/pwsh.exe' }; cwd = $scratch } }
                'Antigravity' { $tool = 'agy'; @{ conversationId = 'c'; workspacePaths = @($case.Cwd); toolCall = @{ name = 'run_command'; args = @{ CommandLine = $case.Command } } } }
            }
            $expected = ($shape -like 'ClaudeLead*') ? $case.Lead : (($shape -in $unknownShell) ? $case.Unknown : $case.Lane)
            $reply = Invoke-AgentPolicy $payload $tool
            $verdict = Get-Verdict $reply
            $count++
            if ($verdict -ne $expected) { $failures.Add("$shape $($case.Policy): expected $expected, got $verdict for $($case.Command.Replace("`n", '\n')) -> $reply") }
        }
    }

    # The real scripts, as each tool starts them.
    function Invoke-Hook {
        param([string] $Script, [string[]] $Arguments, [hashtable] $Payload, [string] $Directory = $topic)
        $start = [Diagnostics.ProcessStartInfo]::new('pwsh')
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WorkingDirectory = $Directory
        foreach ($argument in @('-NoProfile', '-File', "$hooks/$Script") + @($Arguments)) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        $process.StandardInput.Write(($Payload | ConvertTo-Json -Depth 6 -Compress))
        $process.StandardInput.Close()
        $output = $process.StandardOutput.ReadToEnd()
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $code = $process.ExitCode
        $process.Dispose()
        if ($code -ne 0) { throw "$Script exited $($code): $errorText" }
        return $output.Trim()
    }
    $claude = { param($command, $cwd = $topic) @{ session_id = 's'; hook_event_name = 'PreToolUse'; tool_name = 'Bash'; tool_input = @{ command = $command }; cwd = $cwd } }
    $checkout = (Split-Path -Parent (Split-Path -Parent $hooks)).Replace('\', '/')
    $doctrineSession = "hook-test-$PID"
    $edit = { param($path) @{ session_id = $doctrineSession; hook_event_name = 'PreToolUse'; tool_name = 'Write'; tool_input = @{ file_path = $path }; cwd = $scratch } }
    $processCases = @(
        @{ Name = 'claude lead main push asks'; Script = 'agent-policy.ps1'; Arguments = @('-Tool', 'claude'); Payload = (& $claude 'git push origin main'); Expect = { $_.hookSpecificOutput.permissionDecision -eq 'ask' } }
        @{ Name = 'claude subagent stash is refused'; Script = 'agent-policy.ps1'; Arguments = @('-Tool', 'claude'); Payload = ((& $claude 'git stash') + @{ agent_id = 'a1'; agent_type = 'specialist' }); Expect = { $_.hookSpecificOutput.permissionDecision -eq 'deny' } }
        @{ Name = 'codex lane push is refused'; Script = 'agent-policy.ps1'; Arguments = @('-Tool', 'codex'); Payload = @{ turn_id = 't'; tool_name = 'Bash'; tool_input = @{ command = 'git --no-pager push --dry-run origin HEAD:main' }; cwd = $topic }; Expect = { $_.hookSpecificOutput.permissionDecision -eq 'deny' } }
        @{ Name = 'antigravity lane branch delete is refused'; Script = 'agent-policy.ps1'; Arguments = @('-Tool', 'agy'); Payload = @{ conversationId = 'c'; workspacePaths = @($topic); toolCall = @{ name = 'run_command'; args = @{ CommandLine = '(git branch -D probe-x)' } } }; Expect = { $_.decision -eq 'deny' -and $_.reason } }
        @{ Name = 'antigravity passing command abstains'; Script = 'agent-policy.ps1'; Arguments = @('-Tool', 'agy'); Payload = @{ conversationId = 'c'; workspacePaths = @($topic); toolCall = @{ name = 'run_command'; args = @{ CommandLine = 'git status' } } }; Expect = $null }
        @{ Name = 'antigravity cwd falls back from .agents'; Script = 'agent-policy.ps1'; Arguments = @('-Tool', 'agy'); Payload = @{ conversationId = 'c'; toolCall = @{ name = 'run_command'; args = @{ CommandLine = 'git commit -F trailer.txt' } } }; Directory = "$topic/.agents"; Expect = { $_.decision -eq 'deny' } }
        @{ Name = 'shape decides the tool without -Tool'; Script = 'agent-policy.ps1'; Arguments = @(); Payload = @{ conversationId = 'c'; workspacePaths = @($topic); toolCall = @{ name = 'run_command'; args = @{ CommandLine = 'git stash' } } }; Expect = { $_.decision -eq 'deny' } }
        @{ Name = 'codex bash shell reads backticks as a substitution'; Script = 'agent-policy.ps1'; Arguments = @('-Tool', 'codex'); Payload = @{ turn_id = 't'; tool_name = 'exec_command'; tool_input = @{ cmd = 'echo `git stash`'; shell = 'bash'; workdir = $topic } }; Expect = { $_.hookSpecificOutput.permissionDecision -eq 'deny' } }
        @{ Name = 'antigravity string args decode'; Script = 'agent-policy.ps1'; Arguments = @('-Tool', 'agy'); Payload = @{ conversationId = 'c'; workspacePaths = @($topic); toolCall = @{ name = 'run_command'; args = '{"CommandLine":"git stash"}' } }; Expect = { $_.decision -eq 'deny' } }
        @{ Name = 'transport passes a short command'; Script = 'bash-transport.ps1'; Arguments = @(); Payload = (& $claude 'git push origin main'); Expect = $null }
        @{ Name = 'transport asks for a moved main push and quotes it'; Script = 'bash-transport.ps1'; Arguments = @(); Payload = (& $claude "$long && git push --dry-run origin main"); Expect = { $_.hookSpecificOutput.permissionDecision -eq 'ask' -and $_.hookSpecificOutput.permissionDecisionReason -match 'git push --dry-run origin main' -and $_.hookSpecificOutput.updatedInput.command -match '^\. ' } }
        @{ Name = 'transport asks for a moved branch push'; Script = 'bash-transport.ps1'; Arguments = @(); Payload = (& $claude "$long && git push origin topic"); Expect = { $_.hookSpecificOutput.permissionDecision -eq 'ask' } }
        @{ Name = 'transport asks for a moved codex exec'; Script = 'bash-transport.ps1'; Arguments = @(); Payload = (& $claude "$long && codex exec ""brief"""); Expect = { $_.hookSpecificOutput.permissionDecision -eq 'ask' -and $_.hookSpecificOutput.permissionDecisionReason -match 'codex exec' } }
        @{ Name = 'transport moves a long commit that mentions push without asking'; Script = 'bash-transport.ps1'; Arguments = @(); Payload = (& $claude "git commit -m ""$('y' * 2100) then git push origin main"""); Expect = { $_.hookSpecificOutput.updatedInput.command -match '^\. ' -and -not $_.hookSpecificOutput.ContainsKey('permissionDecision') } }
        @{ Name = 'transport moves a backslashed command without asking'; Script = 'bash-transport.ps1'; Arguments = @(); Payload = (& $claude 'echo a\\b'); Expect = { $_.hookSpecificOutput.updatedInput.command -match '^\. ' -and -not $_.hookSpecificOutput.ContainsKey('permissionDecision') } }
        @{ Name = 'transport ignores a heredoc that mentions push'; Script = 'bash-transport.ps1'; Arguments = @(); Payload = (& $claude "cat <<'EOF' > notes.txt`n$('z' * 2100)`ngit push origin main`nEOF"); Expect = { -not $_.hookSpecificOutput.ContainsKey('permissionDecision') } }
        @{ Name = 'transport asks when a moved command holds what git-safety refuses a lane'; Script = 'bash-transport.ps1'; Arguments = @(); Payload = ((& $claude "$long && git branch -D x") + @{ agent_id = 'a1' }); Expect = { $_.hookSpecificOutput.permissionDecision -eq 'ask' -and $_.hookSpecificOutput.permissionDecisionReason -match 'git branch -D x' } }
        @{ Name = 'transport leaves a lead''s moved shared-state command to settings'; Script = 'bash-transport.ps1'; Arguments = @(); Payload = (& $claude "$long && git branch -D x"); Expect = { -not $_.hookSpecificOutput.ContainsKey('permissionDecision') } }
        # prose-doctrine finds its rules in the checkout that holds the file, whatever the session's directory.
        @{ Name = 'doctrine classifies a new docs file in this checkout as human documentation'; Script = 'prose-doctrine.ps1'; Arguments = @(); Payload = (& $edit "$checkout/docs/development/hook-test-$PID/new.md"); Expect = { $_.hookSpecificOutput.additionalContext -match 'human documentation' } }
        @{ Name = 'doctrine shows a surface once per session, agent and checkout'; Script = 'prose-doctrine.ps1'; Arguments = @(); Payload = (& $edit "$checkout/docs/other.md"); Expect = $null }
        @{ Name = 'doctrine classifies .codex as agent guidance'; Script = 'prose-doctrine.ps1'; Arguments = @(); Payload = (& $edit "$checkout/.codex/notes.md"); Expect = { $_.hookSpecificOutput.additionalContext -match 'agent guidance' } }
        @{ Name = 'doctrine classifies .agents as agent guidance for another agent'; Script = 'prose-doctrine.ps1'; Arguments = @(); Payload = ((& $edit "$checkout/.agents/notes.md") + @{ agent_id = 'a1' }); Expect = { $_.hookSpecificOutput.additionalContext -match 'agent guidance' } }
        @{ Name = 'doctrine classifies generated API pages as machine-written'; Script = 'prose-doctrine.ps1'; Arguments = @(); Payload = (& $edit "$checkout/docs/api/api/Puck.Example.md"); Expect = { $_.hookSpecificOutput.additionalContext -match 'machine-written artifact' } }
        @{ Name = 'doctrine ignores a file outside any Puck checkout';Script = 'prose-doctrine.ps1'; Arguments = @(); Payload = (& $edit "$topic/notes.md"); Expect = $null }
    )
    foreach ($case in $processCases) {
        $directory = $case['Directory'] ?? $topic
        $output = Invoke-Hook $case.Script $case.Arguments $case.Payload $directory
        $count++
        if ($null -eq $case.Expect) {
            if ($output) { $failures.Add("process $($case.Name): expected no output, got $output") }
            continue
        }
        if (-not $output) { $failures.Add("process $($case.Name): expected a reply, got none"); continue }
        $reply = ConvertFrom-Json -InputObject $output -AsHashtable
        if (-not ($reply | ForEach-Object $case.Expect)) { $failures.Add("process $($case.Name): unexpected reply $output") }
    }
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) "claude-prose-doctrine/hook-test-$PID") -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failures.Count) {
    $failures | ForEach-Object { "FAIL: " + (($_.Length -gt 400) ? $_.Substring(0, 400) + '...' : $_) }
    throw "$($failures.Count) of $count command-policy cases failed."
}
"PASS: $count command-policy cases."
