#Requires -Version 7.0
. "$PSScriptRoot/command-policy.ps1"
$policy = Read-PolicyInput
if ($null -eq $policy) { exit 0 }

foreach ($tokens in Get-PolicyInvocations $policy.Command) {
    for ($i = 0; $i -lt $tokens.Count; $i++) {
        if (-not (Test-PolicyExecutablePosition $tokens $i)) { continue }
        if ((Get-PolicyExecutable $tokens[$i]) -notin @('dotnet', 'Puck.World')) { continue }
        if ((Get-PolicyExecutable $tokens[$i]) -eq 'Puck.World' -and $i -gt 0 -and $tokens[$i - 1] -ne '--') { continue }
        $tail = @($tokens | Select-Object -Skip ($i + 1))
        $world = (Get-PolicyExecutable $tokens[$i]) -eq 'Puck.World'
        if ($tail.Count -gt 0 -and $tail[0] -match '(^|[/\\])Puck\.World\.dll$') { $world = $true }
        if ($tail.Count -gt 1 -and $tail[0] -eq 'exec' -and $tail[1] -match '(^|[/\\])Puck\.World\.dll$') { $world = $true }
        if ($tail.Count -gt 0 -and $tail[0] -eq 'run') {
            for ($j = 1; $j -lt $tail.Count; $j++) {
                $project = $null
                if ($tail[$j] -match '^--project=(.+)$') { $project = $Matches[1] }
                elseif ($tail[$j] -in @('--project', '-p') -and $j + 1 -lt $tail.Count) { $project = $tail[$j + 1] }
                if ($null -ne $project -and $project.Replace('\', '/').TrimEnd('/') -match '(^|/)Puck\.World(/Puck\.World\.csproj)?$') { $world = $true }
            }
        }
        if (-not $world) { continue }
        $devices = @()
        for ($j = 0; $j -lt $tail.Count; $j++) {
            if ($tail[$j] -match '^--devices=(.*)$') { $devices += $Matches[1] }
            elseif ($tail[$j] -eq '--devices' -and $j + 1 -lt $tail.Count) { $devices += $tail[$j + 1] }
        }
        if ($devices.Count -eq 0 -or @($devices | Where-Object { $_ -notin @('none', 'all') }).Count -or @($devices | Select-Object -Unique).Count -gt 1) {
            Deny-PolicyCommand $policy 'Puck.World boots name their devices: --devices none for an agent World (physical input belongs to the owner), or --devices all only for a World the owner asked to play in.'
        }
    }
}
Complete-PolicyCommand $policy
