#Requires -Version 7.0
. "$PSScriptRoot/command-policy.ps1"
$policy = Read-PolicyInput
if ($null -eq $policy) { exit 0 }

foreach ($tokens in Get-PolicyInvocations $policy.Command) {
    for ($i = 0; $i -lt $tokens.Count - 1; $i++) {
        if (-not (Test-PolicyExecutablePosition $tokens $i)) { continue }
        if ((Get-PolicyExecutable $tokens[$i]) -ne 'dotnet' -or $tokens[$i + 1] -notin @('build', 'msbuild')) { continue }
        $flags = @($tokens | Select-Object -Skip ($i + 2) | Where-Object { $_ -match '^[-/](nodeReuse|nr):' })
        if (@($flags | Where-Object { $_ -match '^[-/]nodeReuse:false$' }).Count -eq 0 -or @($flags | Where-Object { $_ -notmatch '^[-/](nodeReuse|nr):false$' }).Count) {
            Deny-PolicyCommand $policy 'dotnet build/msbuild must pass -nodeReuse:false (or /nodeReuse:false); reused MSBuild nodes retain memory on the shared machine. dotnet test takes no such flag: build first, then dotnet test --no-build.'
        }
    }
}
Complete-PolicyCommand $policy
