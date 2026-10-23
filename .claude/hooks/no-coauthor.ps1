#Requires -Version 7.0
. "$PSScriptRoot/command-policy.ps1"
$policy = Read-PolicyInput
if ($null -eq $policy) { exit 0 }

foreach ($tokens in Get-PolicyInvocations $policy.Command) {
    for ($i = 0; $i -lt $tokens.Count; $i++) {
        if (-not (Test-PolicyExecutablePosition $tokens $i)) { continue }
        if ((Get-PolicyExecutable $tokens[$i]) -ne 'git') { continue }
        $commit = -1
        for ($j = $i + 1; $j -lt $tokens.Count; $j++) {
            if ($tokens[$j].StartsWith('-')) {
                if ($tokens[$j] -in @('-C', '-c', '--git-dir', '--work-tree', '--namespace', '--config-env')) { $j++ }
                continue
            }
            if ($tokens[$j] -eq 'commit') { $commit = $j }
            break
        }
        if ($commit -lt 0) { continue }
        $cwd = $policy.Payload['cwd'] ?? $policy.Payload['toolCall']?['args']?['Cwd'] ?? (Get-Location).Path
        for ($j = $i + 1; $j -lt $commit; $j++) {
            if ($tokens[$j] -ceq '-C' -and $j + 1 -lt $commit) { $cwd = [IO.Path]::GetFullPath($tokens[++$j], $cwd) }
        }
        $reuse = if ($tokens -contains '--amend' -and $tokens -contains '--no-edit') { 'HEAD' } else { $null }
        $explicitMessage = $false
        for ($j = $commit + 1; $j -lt $tokens.Count; $j++) {
            $message = $null
            $file = $null
            $isTrailer = $tokens[$j] -eq '--trailer' -or $tokens[$j].StartsWith('--trailer=')
            if ($tokens[$j] -in @('-m', '--message', '--trailer') -and $j + 1 -lt $tokens.Count) { $message = $tokens[++$j] }
            elseif ($tokens[$j] -match '^--(message|trailer)=(.*)$') { $message = $Matches[2] }
            elseif ($tokens[$j] -cmatch '^-[aqv]*m(.+)$') { $message = $Matches[1] }
            elseif ($tokens[$j] -cmatch '^-[aqv]+m$' -and $j + 1 -lt $tokens.Count) { $message = $tokens[++$j] }
            elseif ($tokens[$j] -in @('-F', '--file') -and $j + 1 -lt $tokens.Count) { $file = $tokens[++$j] }
            elseif ($tokens[$j] -match '^--file=(.*)$' -or $tokens[$j] -cmatch '^-F(.+)$') { $file = $Matches[1] }
            elseif ($tokens[$j] -in @('-C', '-c', '--reuse-message', '--reedit-message') -and $j + 1 -lt $tokens.Count) { $reuse = $tokens[++$j] }
            elseif ($tokens[$j] -match '^--(?:reuse-message|reedit-message)=(.+)$' -or $tokens[$j] -cmatch '^-[Cc](.+)$') { $reuse = $Matches[1] }
            if ($null -ne $file) {
                if ($file -eq '-') {
                    Deny-PolicyCommand $policy 'Use git commit --file <path> so the hook can inspect the message; stdin commit messages cannot be checked for Co-Authored-By trailers.'
                }
                $path = [IO.Path]::GetFullPath($file, $cwd)
                if (Test-Path -LiteralPath $path -PathType Leaf) { $message = [IO.File]::ReadAllText($path) }
                else { Deny-PolicyCommand $policy 'The git commit message file must exist before the tool call so its Co-Authored-By trailers can be checked.' }
            }
            if ($null -ne $message -and -not $isTrailer) { $explicitMessage = $true }
            if ($null -ne $message -and $message -match '(?im)(^|\\n|`n)\s*Co-Authored-By\s*[:=]') {
                Deny-PolicyCommand $policy 'Puck commits carry the checkout identity and no Co-Authored-By trailer; remove that trailer from the commit message.'
            }
        }
        if ($null -ne $reuse -and -not $explicitMessage) {
            $start = [Diagnostics.ProcessStartInfo]::new('git')
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            foreach ($argument in @('-C', $cwd, 'log', '-1', '--format=%B', $reuse, '--')) { $start.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::Start($start)
            $message = $process.StandardOutput.ReadToEnd()
            $errorText = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            $code = $process.ExitCode
            $process.Dispose()
            if ($code -ne 0) { Deny-PolicyCommand $policy "Cannot inspect reused commit message: $($errorText.Trim())" }
            if ($message -match '(?im)^\s*Co-Authored-By\s*[:=]') {
                Deny-PolicyCommand $policy 'The reused git commit message carries a Co-Authored-By trailer; supply a cleaned message with --file.'
            }
        }
    }
}
Complete-PolicyCommand $policy
