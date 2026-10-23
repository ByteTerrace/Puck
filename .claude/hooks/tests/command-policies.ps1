#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$messageFile = Join-Path ([IO.Path]::GetTempPath()) "agents-slim-message-$PID.txt"
[IO.File]::WriteAllText($messageFile, "cli: test`n`nCo-Authored-By: Example <example@example.invalid>`n")
$cases = @(
    @('world-devices', 'dotnet run --project src/Puck.World -- --world worlds/debug', $true),
    @('world-devices', 'dotnet run --project src/Puck.World -- --devices none', $false),
    @('world-devices', 'dotnet "path with spaces/Puck.World.dll" --devices none', $false),
    @('world-devices', 'dotnet src/Puck.World/bin/Release/net11.0/Puck.World.dll', $true),
    @('world-devices', 'dotnet run --project=src/Puck.World/Puck.World.csproj -- --devices=none', $false),
    @('world-devices', 'dotnet run --project src/Puck.World -- --devices none --devices all', $true),
    @('world-devices', 'dotnet Puck.World.dll --devices none; dotnet Puck.World.dll', $true),
    @('world-devices', 'puck canary', $false),
    @('world-devices', 'puck parity --world worlds/debug', $false),
    @('world-devices', 'Get-Content .claude/launch.json', $false),
    @('world-devices', 'dotnet build src/Puck.World -nodeReuse:false', $false),
    @('world-devices', 'dotnet exec Puck.World.dll', $true),
    @('world-devices', 'dotnet src/Puck.World/bin/Release/net11.0/Puck.World.dll --world worlds/debug --devices all', $false),
    @('world-devices', 'dotnet src/Puck.World/bin/Release/net11.0/Puck.World.dll --world worlds/debug --devices some', $true),
    @('world-devices', 'puck lease gpu --holder test -- dotnet Puck.World.dll', $true),
    @('world-devices', 'echo dotnet Puck.World.dll', $false),
    @('no-node-reuse', 'dotnet build src/Puck.Cli', $true),
    @('no-node-reuse', 'dotnet test tests/Puck.Cli.Core.Tests --no-build', $false),
    @('no-node-reuse', 'dotnet msbuild Puck.slnx', $true),
    @('no-node-reuse', 'dotnet build src/Puck.Cli -nodeReuse:false', $false),
    @('no-node-reuse', 'dotnet test tests/Puck.Cli.Core.Tests -c Release', $false),
    @('no-node-reuse', 'dotnet msbuild Puck.slnx -nodeReuse:false', $false),
    @('no-node-reuse', 'dotnet build A -nodeReuse:false && dotnet build B', $true),
    @('no-node-reuse', 'dotnet build A -nodeReuse:false -nr:true', $true),
    @('no-node-reuse', 'pwsh -Command "dotnet build A"', $true),
    @('no-node-reuse', 'dotnet --info', $false),
    @('no-node-reuse', 'echo dotnet build A', $false),
    @('no-node-reuse', '# dotnet build A', $false),
    @('no-coauthor', 'git commit -m "cli: change"', $false),
    @('no-coauthor', "git commit -m 'cli: change`n`nCo-Authored-By: A <a@example.invalid>'", $true),
    @('no-coauthor', 'git commit --trailer "Co-Authored-By=A <a@example.invalid>"', $true),
    @('no-coauthor', 'git commit -am "Co-Authored-By: A <a@example.invalid>"', $true),
    @('no-coauthor', "git commit --file '$messageFile'", $true),
    @('no-coauthor', 'git commit -m "cli: remove Co-Authored-By documentation"', $false),
    @('no-coauthor', 'git log --grep="Co-Authored-By:"', $false),
    @('no-coauthor', 'git show commit -m "Co-Authored-By: example"', $false),
    @('no-coauthor', 'git -C . commit -m "Co-Authored-By: example"', $true)
)
$count = 0
try {
    foreach ($shape in @('Claude', 'Codex', 'CodexNative', 'Antigravity')) {
        foreach ($case in $cases) {
            $payload = switch ($shape) {
                Claude { @{ hook_event_name = 'PreToolUse'; tool_name = 'Bash'; tool_input = @{ command = $case[1] }; cwd = (Get-Location).Path } }
                Codex { @{ hook_event_name = 'PreToolUse'; turn_id = 'agents-slim'; tool_name = 'Bash'; tool_input = @{ command = $case[1] }; cwd = (Get-Location).Path } }
                CodexNative { @{ hook_event_name = 'PreToolUse'; tool_name = 'exec_command'; tool_input = @{ cmd = $case[1] }; cwd = (Get-Location).Path } }
                Antigravity { @{ conversationId = 'agents-slim'; toolCall = @{ name = 'run_command'; args = @{ CommandLine = $case[1] } } } }
            }
            $start = [Diagnostics.ProcessStartInfo]::new('pwsh')
            $start.RedirectStandardInput = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            foreach ($argument in @('-NoProfile', '-File', "$root/$($case[0]).ps1")) { $start.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::Start($start)
            $process.StandardInput.Write(($payload | ConvertTo-Json -Depth 6 -Compress))
            $process.StandardInput.Close()
            $output = $process.StandardOutput.ReadToEnd()
            $errorText = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) { throw "$shape $($case[0]): $errorText" }
            $reply = if ($output.Trim()) { ConvertFrom-Json $output -AsHashtable } else { @{} }
            $denied = ($reply['decision'] -eq 'deny' -or $reply['hookSpecificOutput']?['permissionDecision'] -eq 'deny')
            if ($denied -ne $case[2]) { throw "$shape $($case[0]): expected deny=$($case[2]), got $output for $($case[1])" }
            if ($shape -eq 'Antigravity' -and -not $denied -and $reply['decision'] -ne 'allow') { throw 'Antigravity requires an explicit allow decision.' }
            "$shape $($case[0]) $(if ($denied) { 'DENY' } else { 'PASS' }): $($case[1].Replace("`n", '\n'))"
            $process.Dispose()
            $count++
        }
    }
    "PASS: $count command-policy cases."
} finally { Remove-Item -LiteralPath $messageFile -Force }
