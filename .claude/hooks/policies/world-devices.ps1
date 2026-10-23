#Requires -Version 7.0
# world-devices: a Puck.World boot names its devices, --devices none for an agent's World or --devices all for one
# the owner asked to play in. Recognizes the executable, `dotnet <path>/Puck.World.dll`, `dotnet exec [host options]
# Puck.World.dll`, and `dotnet run` or `dotnet watch run` of the Puck.World project, named or implied by the
# effective working directory.

$script:DotnetExecValues = @('--runtimeconfig', '--depsfile', '--additionalprobingpath', '--additional-deps', '--fx-version', '--roll-forward', '--roll-forward-on-no-candidate-fx')
$script:WorldDevicesReason = 'Puck.World boots name their devices: --devices none for an agent World (physical input belongs to the owner), or --devices all only for a World the owner asked to play in.'

function Test-PuckWorldProject {
    param([string] $Project, [string] $Cwd)
    $path = $Project.Replace('\', '/').TrimEnd('/')
    if ($path -match '(^|/)Puck\.World(/Puck\.World\.csproj)?$' -or $path -match '(^|/)Puck\.World\.csproj$') { return $true }
    return $false
}

function Test-WorldDevicesPolicy {
    param([hashtable] $Context, $Invocation)
    $argv = $Invocation.Argv
    $tail = @($argv | Select-Object -Skip 1)
    $world = $Invocation.Name -eq 'Puck.World'
    if ($Invocation.Name -eq 'dotnet' -and $tail.Count) {
        if ($tail[0] -match '(^|[/\\])Puck\.World\.dll$') { $world = $true }
        elseif ($tail[0] -eq 'exec') {
            for ($j = 1; $j -lt $tail.Count; $j++) {
                if ($tail[$j] -in $script:DotnetExecValues) { $j++; continue }
                $world = $tail[$j] -match '(^|[/\\])Puck\.World\.dll$'
                break
            }
        } elseif ($tail[0] -eq 'run' -or ($tail[0] -eq 'watch' -and ($tail.Count -lt 2 -or $tail[1] -eq 'run' -or $tail[1].StartsWith('-')))) {
            $project = $null
            for ($j = 1; $j -lt $tail.Count -and $tail[$j] -ne '--'; $j++) {
                if ($tail[$j] -match '^--project=(.+)$') { $project = $Matches[1] }
                elseif ($tail[$j] -in @('--project', '-p') -and $j + 1 -lt $tail.Count) { $project = $tail[++$j] }
            }
            if ($null -ne $project) { $world = Test-PuckWorldProject $project $Invocation.Cwd }
            elseif ($Invocation.Cwd) { $world = Test-Path -LiteralPath (Join-Path $Invocation.Cwd 'Puck.World.csproj') }
        }
    }
    if (-not $world) { return }
    $devices = @()
    for ($j = 0; $j -lt $tail.Count; $j++) {
        if ($tail[$j] -match '^--devices=(.*)$') { $devices += $Matches[1] }
        elseif ($tail[$j] -eq '--devices' -and $j + 1 -lt $tail.Count) { $devices += $tail[$j + 1] }
    }
    if ($devices.Count -eq 0 -or @($devices | Where-Object { $_ -notin @('none', 'all') }).Count -or @($devices | Select-Object -Unique).Count -gt 1) {
        return New-PolicyDecision 'deny' $script:WorldDevicesReason
    }
}
