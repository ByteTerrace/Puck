#Requires -Version 7.0
# no-coauthor: Puck commits carry the checkout identity and no Co-Authored-By trailer. Reads every message a commit,
# merge, commit-tree or cherry-pick would record: -m and --trailer text, -F and -t files (resolved against the
# effective working directory), and reused or copied commit messages. A revert writes a fresh message, so it is not
# read. A message on stdin cannot be inspected before the command runs, so it is refused: write the message file
# with the edit tool before the commit call.

$script:CoauthorTrailer = '(?im)(^|\\n|`n)\s*Co-Authored-By\s*[:=]'
$script:CoauthorReason = 'Puck commits carry the checkout identity and no Co-Authored-By trailer; remove that trailer from the commit message.'

# Per verb: short options that take a value and what each one is, and the long ones.
$script:GitMessageOptions = @{
    'commit'      = @{
        Short = @{ [char]'m' = 'message'; [char]'F' = 'file'; [char]'t' = 'template'; [char]'c' = 'reuse'; [char]'C' = 'reuse' }
        Attached = 'uS'
        Long = @{ '--message' = 'message'; '--file' = 'file'; '--template' = 'template'; '--reuse-message' = 'reuse'; '--reedit-message' = 'reuse'; '--trailer' = 'trailer'; '--author' = 'skip'; '--date' = 'skip'; '--cleanup' = 'skip'; '--fixup' = 'skip'; '--squash' = 'skip'; '--pathspec-from-file' = 'skip' }
    }
    'merge'       = @{
        Short = @{ [char]'m' = 'message'; [char]'F' = 'file'; [char]'s' = 'skip'; [char]'X' = 'skip' }
        Attached = 'S'
        Long = @{ '--message' = 'message'; '--file' = 'file'; '--strategy' = 'skip'; '--strategy-option' = 'skip'; '--into-name' = 'skip'; '--cleanup' = 'skip' }
    }
    'commit-tree' = @{
        Short = @{ [char]'m' = 'message'; [char]'F' = 'file'; [char]'p' = 'skip' }
        Attached = 'S'
        Long = @{}
    }
}

# Returns the message sources of an option list: @{ Messages; Files; Templates; Reuse; Explicit }.
function Get-GitMessageSources {
    param([string[]] $Rest, [hashtable] $Options)
    $sources = @{ Messages = [Collections.Generic.List[string]]::new(); Files = [Collections.Generic.List[string]]::new(); Templates = [Collections.Generic.List[string]]::new(); Reuse = [Collections.Generic.List[string]]::new(); Explicit = $false }
    $add = {
        param([string] $Kind, [string] $Value)
        switch ($Kind) {
            'message' { $sources.Messages.Add($Value); $sources.Explicit = $true }
            'trailer' { $sources.Messages.Add($Value) }
            'file' { $sources.Files.Add($Value); $sources.Explicit = $true }
            'template' { $sources.Templates.Add($Value) }
            'reuse' { $sources.Reuse.Add($Value) }
        }
    }
    for ($i = 0; $i -lt $Rest.Count; $i++) {
        $t = $Rest[$i]
        if ($t -eq '--') { break }
        if ($t.StartsWith('--')) {
            $parts = $t -split '=', 2
            $kind = $Options.Long[$parts[0]]
            if ($null -eq $kind) { continue }
            $value = if ($parts.Count -eq 2) { $parts[1] } elseif ($i + 1 -lt $Rest.Count) { $Rest[++$i] } else { $null }
            if ($null -ne $value) { & $add $kind $value }
            continue
        }
        if ($t.Length -lt 2 -or -not $t.StartsWith('-')) { continue }
        for ($k = 1; $k -lt $t.Length; $k++) {
            if ($Options.Attached.Contains($t[$k])) { break }
            $kind = $Options.Short[$t[$k]]
            if ($null -eq $kind) { continue }
            $value = if ($k + 1 -lt $t.Length) { $t.Substring($k + 1) } elseif ($i + 1 -lt $Rest.Count) { $Rest[++$i] } else { $null }
            if ($null -ne $value) { & $add $kind $value }
            break
        }
    }
    return $sources
}

function Get-GitMessages {
    param([hashtable] $Git, [string[]] $Revisions)
    foreach ($revision in $Revisions) {
        $arguments = if ($revision.Contains('..')) { @('log', '--format=%B', $revision, '--') } else { @('log', '-1', '--format=%B', $revision, '--') }
        $read = Invoke-PolicyGit $Git.Cwd $Git.Globals $arguments
        if ($read.Code -ne 0) { return @{ Error = "Cannot inspect the message of $($revision): $($read.Err.Trim())" } }
        $read.Out
    }
}

function Test-NoCoauthorPolicy {
    param([hashtable] $Context, $Invocation)
    if ($Invocation.Name -ne 'git') { return }
    $git = Resolve-GitAlias (Split-GitInvocation $Invocation)
    if ($git.ContainsKey('Shell')) { return }
    if ($git.Verb -eq 'cherry-pick') {
        if (Test-GitOption $git.Rest '' '' @('--continue', '--abort', '--skip', '--quit')) { return }
        $revisions = @(Get-GitOperands $git.Rest 'mX' @('--mainline', '--strategy', '--strategy-option', '--cleanup'))
        foreach ($message in Get-GitMessages $git $revisions) {
            if ($message -is [hashtable]) { return New-PolicyDecision 'deny' $message.Error }
            if ($message -match $script:CoauthorTrailer) {
                return New-PolicyDecision 'deny' 'A cherry-pick copies its source''s message, and this one carries a Co-Authored-By trailer; commit the change with a cleaned message from --file.'
            }
        }
        return
    }
    $options = $script:GitMessageOptions[$git.Verb]
    if ($null -eq $options) { return }
    $sources = Get-GitMessageSources $git.Rest $options
    if ($git.Verb -eq 'commit' -and $git.Rest -contains '--amend' -and $git.Rest -contains '--no-edit') { $sources.Reuse.Add('HEAD') }
    foreach ($message in $sources.Messages) {
        if ($message -match $script:CoauthorTrailer) { return New-PolicyDecision 'deny' $script:CoauthorReason }
    }
    foreach ($file in @($sources.Files) + @($sources.Templates)) {
        $isTemplate = $sources.Templates.Contains($file) -and -not $sources.Files.Contains($file)
        if ($file -eq '-') {
            return New-PolicyDecision 'deny' "Use git $($git.Verb) --file <path> so the hook can inspect the message; a message on stdin cannot be checked for Co-Authored-By trailers."
        }
        $path = Resolve-PolicyPath $file $git.Cwd
        if ($null -eq $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            if ($isTemplate) { continue }
            return New-PolicyDecision 'deny' 'The git message file must exist before the tool call so its Co-Authored-By trailers can be checked; write it with the edit tool first.'
        }
        if ([IO.File]::ReadAllText($path) -match $script:CoauthorTrailer) { return New-PolicyDecision 'deny' $script:CoauthorReason }
    }
    if ($sources.Reuse.Count -and -not $sources.Explicit) {
        foreach ($message in Get-GitMessages $git @($sources.Reuse)) {
            if ($message -is [hashtable]) { return New-PolicyDecision 'deny' $message.Error }
            if ($message -match $script:CoauthorTrailer) {
                return New-PolicyDecision 'deny' 'The reused git commit message carries a Co-Authored-By trailer; supply a cleaned message with --file.'
            }
        }
    }
}
