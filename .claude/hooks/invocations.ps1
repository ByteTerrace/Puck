#Requires -Version 7.0
# The shared invocation classifier. Dot-sourced by command-policy.ps1 and bash-transport.ps1, never registered.
#
# Turns one tool command into the program invocations it would start: PowerShell through PowerShell's own parser,
# POSIX shell text through the tokenizer below. Each invocation carries its argument vector with wrappers (env, sudo,
# timeout, xargs, a lease's `--`, bash -c, pwsh -Command, Start-Process, ...) unwrapped, its NAME=value prefixes, the
# effective working directory after any earlier cd or Set-Location, and its source text. Quoted text, heredoc bodies
# and here-strings are data. A form the classifier recognizes but cannot see into (a computed command name,
# -EncodedCommand, eval of a variable, a shell reading its script from stdin) is marked Opaque, never passed over.
Set-StrictMode -Version Latest

$script:PolicyMaxDepth = 6
$script:PolicyReservedWords = @('if', 'then', 'else', 'elif', 'fi', 'do', 'done', 'while', 'until', 'esac', '!', '{', '}', '[[', ']]')
$script:PolicySkippedForms = @('for', 'case', 'select', 'function', 'in')
$script:PolicyPosixShells = @('bash', 'sh', 'zsh', 'dash', 'ksh', 'ash')
$script:PolicyPwshShells = @('pwsh', 'powershell', 'pwsh-preview')
# Wrappers that run the command after their own options: the options that take a separate value, and how many
# operands precede the command.
$script:PolicyWrappers = @{
    sudo    = @{ Values = @('-u', '-g', '-h', '-p', '-C', '-D', '-r', '-t', '-U', '-T', '--user', '--group', '--host', '--prompt', '--chdir', '--role', '--type', '--other-user', '--command-timeout', '--close-from'); Operands = 0 }
    doas    = @{ Values = @('-u', '-C'); Operands = 0 }
    nohup   = @{ Values = @(); Operands = 0 }
    builtin = @{ Values = @(); Operands = 0 }
    exec    = @{ Values = @('-a'); Operands = 0 }
    time    = @{ Values = @('-f', '-o', '--format', '--output'); Operands = 0 }
    chronic = @{ Values = @(); Operands = 0 }
    winpty  = @{ Values = @(); Operands = 0 }
    nice    = @{ Values = @('-n', '--adjustment'); Operands = 0 }
    ionice  = @{ Values = @('-c', '-n', '-p', '-P', '-u', '--class', '--classdata'); Operands = 0 }
    stdbuf  = @{ Values = @('-i', '-o', '-e', '--input', '--output', '--error'); Operands = 0 }
    timeout = @{ Values = @('-k', '-s', '--kill-after', '--signal'); Operands = 1 }
    xargs   = @{ Values = @('-a', '-d', '-E', '-I', '-L', '-n', '-P', '-s', '--arg-file', '--delimiter', '--eof', '--replace', '--max-lines', '--max-args', '--max-procs', '--max-chars', '--process-slot-var'); Operands = 0 }
}

function Get-PolicyExecutable {
    param([string] $Token)
    return ($Token.Replace('\', '/') -split '/')[-1] -replace '\.(exe|cmd|bat)$', ''
}

function Resolve-PolicyPath {
    param([string] $Path, [string] $Cwd)
    if ([string]::IsNullOrEmpty($Path)) { return $Cwd }
    if ($Path -eq '~' -or $Path.StartsWith('~/') -or $Path.StartsWith('~\')) { $Path = [Environment]::GetFolderPath('UserProfile') + $Path.Substring(1) }
    # Git Bash spells D:\x as /d/x.
    if ($IsWindows -and $Path -match '^/([A-Za-z])(/|$)') { $Path = "$($Matches[1]):/" + $Path.Substring(3) }
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    if ([string]::IsNullOrEmpty($Cwd) -or -not [IO.Path]::IsPathFullyQualified($Cwd)) { return $null }
    return [IO.Path]::GetFullPath($Path, $Cwd)
}

function New-PolicyInvocation {
    param([string[]] $Argv, [bool[]] $Dynamic, [hashtable] $Env, [string] $Cwd, [string] $Text, [string] $Dialect, [string] $Opaque)
    [pscustomobject]@{
        Argv    = $Argv
        Dynamic = $Dynamic
        Name    = Get-PolicyExecutable $Argv[0]
        Env     = $Env
        Cwd     = $Cwd
        Text    = $Text.Trim()
        Dialect = $Dialect
        Opaque  = $Opaque ? $Opaque : $null
    }
}

# Returns the index just past the parenthesis that closes one opened before $Start, skipping quotes, escapes, nested
# substitutions and heredoc bodies (a commit message in `$(cat <<'EOF' ... EOF)` may hold an apostrophe).
function Find-PolicyClose {
    param([string] $Text, [int] $Start)
    $depth = 1
    $heredocs = [Collections.Generic.List[string]]::new()
    for ($i = $Start; $i -lt $Text.Length; $i++) {
        $c = $Text[$i]
        if ($c -eq '\') { $i++; continue }
        if ($c -eq "'") {
            $j = $Text.IndexOf("'", $i + 1)
            if ($j -lt 0) { return $Text.Length }
            $i = $j
            continue
        }
        if ($c -eq '"') {
            for ($i++; $i -lt $Text.Length -and $Text[$i] -ne '"'; $i++) {
                if ($Text[$i] -eq '\') { $i++ }
                elseif ($Text[$i] -eq '$' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '(') { $i = (Find-PolicyClose $Text ($i + 2)) - 1 }
            }
            continue
        }
        if ($c -eq '<' -and $i + 2 -lt $Text.Length -and $Text[$i + 1] -eq '<' -and $Text[$i + 2] -ne '<') {
            $m = [regex]::Match($Text.Substring($i + 2), '^-?[ \t]*(?:''([^'']*)''|"([^"]*)"|([^\s;&|<>()]+))')
            if ($m.Success) {
                $delimiter = ($m.Groups[1].Value + $m.Groups[2].Value + $m.Groups[3].Value) -replace '\\', ''
                $heredocs.Add($delimiter)
                $i += 1 + $m.Length
            }
            continue
        }
        if ($c -eq "`n" -and $heredocs.Count) {
            foreach ($delimiter in $heredocs) {
                while ($i + 1 -lt $Text.Length) {
                    $end = $Text.IndexOf("`n", $i + 1)
                    if ($end -lt 0) { $end = $Text.Length }
                    $line = $Text.Substring($i + 1, $end - $i - 1).TrimEnd("`r").TrimStart("`t")
                    $i = $end
                    if ($line -ceq $delimiter) { break }
                }
            }
            $heredocs.Clear()
            continue
        }
        if ($c -eq '(') { $depth++ }
        elseif ($c -eq ')') {
            $depth--
            if ($depth -eq 0) { return $i + 1 }
        }
    }
    return $Text.Length
}

function Find-PolicyBacktick {
    param([string] $Text, [int] $Start)
    for ($i = $Start; $i -lt $Text.Length; $i++) {
        if ($Text[$i] -eq '\') { $i++ }
        elseif ($Text[$i] -eq '`') { return $i }
    }
    return $Text.Length
}

# Splits text into words by POSIX quoting alone, for a string that names a command line rather than a script:
# env -S, an alias body, Start-Process's joined argument list.
function Split-PolicyWords {
    param([string] $Text)
    $words = [Collections.Generic.List[string]]::new()
    $word = [Text.StringBuilder]::new()
    $started = $false
    for ($i = 0; $i -lt $Text.Length; $i++) {
        $c = $Text[$i]
        if ([char]::IsWhiteSpace($c)) {
            if ($started) { $words.Add($word.ToString()); [void]$word.Clear(); $started = $false }
            continue
        }
        $started = $true
        if ($c -eq "'") {
            $j = $Text.IndexOf("'", $i + 1)
            if ($j -lt 0) { $j = $Text.Length }
            [void]$word.Append($Text, $i + 1, $j - $i - 1)
            $i = $j
        } elseif ($c -eq '"') {
            for ($i++; $i -lt $Text.Length -and $Text[$i] -ne '"'; $i++) {
                if ($Text[$i] -eq '\' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -in @('"', '\')) { $i++ }
                [void]$word.Append($Text[$i])
            }
        } else { [void]$word.Append($c) }
    }
    if ($started) { $words.Add($word.ToString()) }
    return $words.ToArray()
}

# Returns the index where a wrapped command starts, or -1 when $Argv is not a wrapper. env also collects its
# assignments and -C directory into $State.
function Get-PolicyWrappedStart {
    param([string[]] $Argv, [string] $Name, [hashtable] $State)
    if ($Name -eq 'env') {
        for ($j = 1; $j -lt $Argv.Count; $j++) {
            $t = $Argv[$j]
            if ($t -eq '--') { return $j + 1 }
            if ($t -in @('-u', '--unset')) { $j++; continue }
            if ($t -in @('-C', '--chdir')) { $State.Cwd = Resolve-PolicyPath $Argv[++$j] $State.Cwd; continue }
            if ($t -match '^--chdir=(.*)$') { $State.Cwd = Resolve-PolicyPath $Matches[1] $State.Cwd; continue }
            if ($t -in @('-S', '--split-string') -or $t -match '^(-S|--split-string=)(.+)$') {
                $split = if ($t -in @('-S', '--split-string')) { $Argv[++$j] } else { $Matches[2] }
                $State.Split = @(Split-PolicyWords $split)
                return $j + 1
            }
            if ($t.StartsWith('-')) { continue }
            if ($t -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $State.Env[$Matches[1]] = $Matches[2]; continue }
            return $j
        }
        return $Argv.Count
    }
    if ($Name -eq 'command') {
        for ($j = 1; $j -lt $Argv.Count; $j++) {
            # command -v and -V only report how a name resolves; they run nothing.
            if ($Argv[$j] -cin @('-v', '-V')) { return -1 }
            if ($Argv[$j] -eq '--') { return $j + 1 }
            if (-not $Argv[$j].StartsWith('-')) { return $j }
        }
        return $Argv.Count
    }
    $wrapper = $script:PolicyWrappers[$Name]
    if ($null -eq $wrapper) { return -1 }
    $j = 1
    while ($j -lt $Argv.Count) {
        $t = $Argv[$j]
        if ($t -eq '--') { $j++; break }
        if ($t.Length -lt 2 -or -not $t.StartsWith('-')) { break }
        $j += ($t -cin $wrapper.Values) ? 2 : 1
    }
    return $j + $wrapper.Operands
}

# Finds the script a shell wrapper runs: @{ Script; Dialect } to inspect, @{ Opaque } when it cannot be seen, or
# $null when the shell runs a script file (not inspected: arbitrary script bodies are outside this classifier).
function Get-PolicyShellScript {
    param([string[]] $Argv, [bool[]] $Dynamic, [string] $Name)
    $rest = { param($from) ($Argv[$from..($Argv.Count - 1)] -join ' ') }
    $restDynamic = { param($from) @($Dynamic[$from..($Dynamic.Count - 1)] | Where-Object { $_ }).Count -gt 0 }
    if ($Name -in $script:PolicyPosixShells) {
        for ($j = 1; $j -lt $Argv.Count; $j++) {
            $t = $Argv[$j]
            if ($t -cin @('-o', '+o', '-O', '+O', '--rcfile', '--init-file')) { $j++; continue }
            if ($t -cin @('--version', '--help')) { return $null }
            if ($t -cmatch '^-[A-Za-z]*c[A-Za-z]*$') {
                if ($j + 1 -ge $Argv.Count) { return $null }
                if ($Dynamic[$j + 1]) { return @{ Opaque = "a $Name -c script computed at run time" } }
                return @{ Script = $Argv[$j + 1]; Dialect = 'posix' }
            }
            if ($t -eq '-s' -or $t -eq '-') { return @{ Opaque = "$Name reading its script from standard input" } }
            if ($t -eq '--' -or -not ($t.StartsWith('-') -or $t.StartsWith('+'))) { return $null }
        }
        return @{ Opaque = "$Name reading its script from standard input" }
    }
    if ($Name -in $script:PolicyPwshShells) {
        $values = '^-(executionpolicy|ex|ep|workingdirectory|wd|windowstyle|w|outputformat|o|of|inputformat|in|if|configurationname|config|configurationfile|custompipename|settingsfile|settings|version|v)$'
        for ($j = 1; $j -lt $Argv.Count; $j++) {
            $t = $Argv[$j]
            if ($t -match '^[-/](e|ec|en|enc|enco|encod|encode|encoded|encodedc\w*|encodeda\w*)$') {
                return @{ Opaque = "pwsh -EncodedCommand, which this check cannot read" }
            }
            if ($t -match '^[-/](c|com|comm|comma|comman|command)$') {
                if ($j + 1 -ge $Argv.Count) { return $null }
                if ($Argv[$j + 1] -eq '-') { return @{ Opaque = 'pwsh reading its command from standard input' } }
                if (& $restDynamic ($j + 1)) { return @{ Opaque = 'a pwsh -Command computed at run time' } }
                return @{ Script = & $rest ($j + 1); Dialect = 'powershell' }
            }
            if ($t -match '^[-/](f|fi|fil|file)$') { return $null }
            if ($t -match $values) { $j++; continue }
            if ($t.StartsWith('-')) { continue }
            # A bare operand is a script file to pwsh and a command to Windows PowerShell.
            if ($Name -eq 'powershell') {
                if (& $restDynamic $j) { return @{ Opaque = 'a powershell command computed at run time' } }
                return @{ Script = & $rest $j; Dialect = 'powershell' }
            }
            return $null
        }
        return $null
    }
    if ($Name -eq 'cmd') {
        for ($j = 1; $j -lt $Argv.Count; $j++) {
            if ($Argv[$j] -match '^/[cCkK]$') {
                if ($j + 1 -ge $Argv.Count) { return $null }
                if (& $restDynamic ($j + 1)) { return @{ Opaque = 'a cmd /c command computed at run time' } }
                return @{ Script = & $rest ($j + 1); Dialect = 'posix' }
            }
        }
        return $null
    }
    if ($Name -eq 'eval') {
        if ($Argv.Count -lt 2) { return $null }
        if (& $restDynamic 1) { return @{ Opaque = 'eval of text computed at run time' } }
        return @{ Script = & $rest 1; Dialect = 'posix' }
    }
    return $null
}

# Unwraps one argument vector into the invocations it starts.
function Resolve-PolicyInvocation {
    param([string[]] $Argv, [bool[]] $Dynamic, [hashtable] $Env, [string] $Cwd, [string] $Text, [string] $Dialect, [int] $Depth)
    while ($Argv.Count -gt 0) {
        # A POSIX NAME=value prefix, wherever the dialect let it through.
        if (-not $Dynamic[0] -and $Argv[0] -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            $Env[$Matches[1]] = $Matches[2]
            $Argv = @($Argv | Select-Object -Skip 1)
            $Dynamic = @($Dynamic | Select-Object -Skip 1)
            continue
        }
        $name = Get-PolicyExecutable $Argv[0]
        if ($Dynamic[0]) {
            New-PolicyInvocation $Argv $Dynamic $Env $Cwd $Text $Dialect 'a command name computed at run time'
            return
        }
        $state = @{ Env = $Env; Cwd = $Cwd; Split = $null }
        $start = Get-PolicyWrappedStart $Argv $name $state
        if ($start -ge 0) {
            $Cwd = $state.Cwd
            if ($null -ne $state.Split) {
                $tail = @($Argv | Select-Object -Skip $start)
                $Argv = @($state.Split) + $tail
                $Dynamic = @($state.Split | ForEach-Object { $false }) + @($Dynamic | Select-Object -Skip $start)
                continue
            }
            if ($start -ge $Argv.Count) { return }
            $Argv = $Argv[$start..($Argv.Count - 1)]
            $Dynamic = $Dynamic[$start..($Dynamic.Count - 1)]
            continue
        }
        if ($name -in ($script:PolicyPosixShells + $script:PolicyPwshShells + @('cmd', 'eval'))) {
            New-PolicyInvocation $Argv $Dynamic $Env $Cwd $Text $Dialect $null
            $shell = Get-PolicyShellScript $Argv $Dynamic $name
            if ($null -eq $shell) { return }
            if ($shell.ContainsKey('Opaque')) {
                New-PolicyInvocation $Argv $Dynamic $Env $Cwd $Text $Dialect $shell.Opaque
                return
            }
            if ($Depth -lt $script:PolicyMaxDepth) { Get-PolicyInvocations $shell.Script $shell.Dialect $Cwd ($Depth + 1) }
            return
        }
        New-PolicyInvocation $Argv $Dynamic $Env $Cwd $Text $Dialect $null
        # find runs its -exec command; a lease runs the command after its own `--`.
        $forward = -1
        $stop = $Argv.Count
        if ($name -eq 'find') {
            for ($j = 1; $j -lt $Argv.Count - 1; $j++) {
                if ($Argv[$j] -in @('-exec', '-execdir', '-ok', '-okdir')) {
                    $forward = $j + 1
                    for ($k = $forward; $k -lt $Argv.Count; $k++) { if ($Argv[$k] -in @(';', '+')) { $stop = $k; break } }
                    break
                }
            }
        } elseif (($Argv | Select-Object -First 3) -contains 'lease') {
            $forward = [Array]::IndexOf($Argv, '--') + 1
            if ($forward -eq 0) { $forward = -1 }
        }
        if ($forward -le 0 -or $forward -ge $stop) { return }
        $Argv = $Argv[$forward..($stop - 1)]
        $Dynamic = $Dynamic[$forward..($stop - 1)]
    }
}

# Emits the invocations of a POSIX shell script.
function Get-PolicyPosixInvocations {
    param([string] $Text, [string] $Cwd, [int] $Depth)
    $n = $Text.Length
    $words = [Collections.Generic.List[object]]::new()
    $word = [Text.StringBuilder]::new()
    $started = $false; $dynamic = $false; $quoted = $false
    $redirectTarget = $false; $heredocNext = $false
    $pending = [Collections.Generic.List[object]]::new()
    $commandStart = 0
    $i = 0

    $endWord = {
        if ($started) {
            $value = $word.ToString()
            if ($redirectTarget) { $redirectTarget = $false }
            elseif ($heredocNext) { $pending.Add(@{ Delimiter = $value; Quoted = $quoted }); $heredocNext = $false }
            else { $words.Add([pscustomobject]@{ Value = $value; Dynamic = $dynamic }) }
        }
        [void]$word.Clear(); $started = $false; $dynamic = $false; $quoted = $false
    }
    $endCommand = {
        . $endWord
        $list = @($words)
        $words.Clear()
        $source = $Text.Substring($commandStart, [Math]::Min($i, $n) - $commandStart)
        $commandStart = [Math]::Min($i + 1, $n)
        while ($list.Count -and -not $list[0].Dynamic -and $list[0].Value -in $script:PolicyReservedWords) { $list = @($list | Select-Object -Skip 1) }
        if ($list.Count -and $list[0].Value -in $script:PolicySkippedForms) { $list = @() }
        $prefix = @{}
        while ($list.Count -and $list[0].Value -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            $prefix[$Matches[1]] = $Matches[2]
            $list = @($list | Select-Object -Skip 1)
        }
        if ($list.Count) {
            $argv = [string[]]@($list | ForEach-Object Value)
            $flags = [bool[]]@($list | ForEach-Object Dynamic)
            if ((Get-PolicyExecutable $argv[0]) -in @('cd', 'pushd') -and $argv.Count -ge 2 -and -not $flags[1] -and $argv[1] -ne '-') {
                $Cwd = Resolve-PolicyPath $argv[1] $Cwd
            }
            Resolve-PolicyInvocation $argv $flags $prefix $Cwd $source 'posix' $Depth
        }
    }
    $substitute = {
        param([int] $From, [int] $To)
        if ($Depth -lt $script:PolicyMaxDepth) { Get-PolicyPosixInvocations $Text.Substring($From, $To - $From) $Cwd ($Depth + 1) }
    }

    for ($i = 0; $i -lt $n; $i++) {
        $c = $Text[$i]
        $next = if ($i + 1 -lt $n) { $Text[$i + 1] } else { [char]0 }
        if ($c -eq "`n") {
            . $endCommand
            foreach ($heredoc in $pending) {
                while ($i + 1 -lt $n) {
                    $end = $Text.IndexOf("`n", $i + 1)
                    if ($end -lt 0) { $end = $n }
                    $line = $Text.Substring($i + 1, $end - $i - 1).TrimEnd("`r")
                    $i = $end
                    if ($line.TrimStart("`t") -ceq $heredoc.Delimiter) { break }
                    # An unquoted delimiter leaves the body open to command substitution.
                    if (-not $heredoc.Quoted) {
                        foreach ($m in [regex]::Matches($line, '\$\(|`')) {
                            if ($m.Value -eq '`') {
                                $close = Find-PolicyBacktick $line ($m.Index + 1)
                                if ($Depth -lt $script:PolicyMaxDepth) { Get-PolicyPosixInvocations $line.Substring($m.Index + 1, $close - $m.Index - 1) $Cwd ($Depth + 1) }
                            } else {
                                $close = Find-PolicyClose $line ($m.Index + 2)
                                if ($Depth -lt $script:PolicyMaxDepth) { Get-PolicyPosixInvocations $line.Substring($m.Index + 2, [Math]::Max(0, $close - $m.Index - 3)) $Cwd ($Depth + 1) }
                            }
                        }
                    }
                }
            }
            $pending.Clear()
            $commandStart = [Math]::Min($i + 1, $n)
            continue
        }
        if ($c -eq '#' -and -not $started) {
            while ($i + 1 -lt $n -and $Text[$i + 1] -ne "`n") { $i++ }
            continue
        }
        if ($c -eq ' ' -or $c -eq "`t" -or $c -eq "`r") { . $endWord; continue }
        if ($c -eq '\') {
            if ($next -eq "`n") { $i++; continue }
            if ($next -eq "`r" -and $i + 2 -lt $n -and $Text[$i + 2] -eq "`n") { $i += 2; continue }
            if ($i + 1 -lt $n) { [void]$word.Append($next); $i++ }
            $started = $true; $quoted = $true
            continue
        }
        if ($c -eq "'") {
            $j = $Text.IndexOf("'", $i + 1)
            if ($j -lt 0) { $j = $n }
            [void]$word.Append($Text, $i + 1, $j - $i - 1)
            $i = $j; $started = $true; $quoted = $true
            continue
        }
        if ($c -eq '$' -and $next -eq "'") {
            for ($i += 2; $i -lt $n -and $Text[$i] -ne "'"; $i++) {
                if ($Text[$i] -eq '\' -and $i + 1 -lt $n) {
                    $i++
                    $escape = @{ [char]'n' = "`n"; [char]'t' = "`t"; [char]'r' = "`r" }[$Text[$i]]
                    [void]$word.Append($escape ?? $Text[$i])
                } else { [void]$word.Append($Text[$i]) }
            }
            $started = $true; $quoted = $true
            continue
        }
        if ($c -eq '"') {
            for ($i++; $i -lt $n -and $Text[$i] -ne '"'; $i++) {
                $d = $Text[$i]
                $after = if ($i + 1 -lt $n) { $Text[$i + 1] } else { [char]0 }
                if ($d -eq '\' -and $after -in @('$', '`', '"', '\', "`n")) {
                    if ($after -ne "`n") { [void]$word.Append($after) }
                    $i++
                } elseif ($d -eq '$' -and $after -eq '(') {
                    $close = Find-PolicyClose $Text ($i + 2)
                    if ($i + 2 -lt $n -and $Text[$i + 2] -ne '(') { . $substitute ($i + 2) ([Math]::Max($i + 2, $close - 1)) }
                    [void]$word.Append($Text, $i, $close - $i)
                    $i = $close - 1; $dynamic = $true
                } elseif ($d -eq '`') {
                    $close = Find-PolicyBacktick $Text ($i + 1)
                    . $substitute ($i + 1) $close
                    [void]$word.Append($Text, $i, [Math]::Min($close + 1, $n) - $i)
                    $i = $close; $dynamic = $true
                } else {
                    if ($d -eq '$' -and ($after -match '[A-Za-z0-9_{@*#?!$-]')) { $dynamic = $true }
                    [void]$word.Append($d)
                }
            }
            $started = $true; $quoted = $true
            continue
        }
        if (($c -eq '$' -and $next -eq '(') -or (($c -eq '<' -or $c -eq '>') -and $next -eq '(' -and -not $started)) {
            $close = Find-PolicyClose $Text ($i + 2)
            if ($i + 2 -lt $n -and $Text[$i + 2] -ne '(') { . $substitute ($i + 2) ([Math]::Max($i + 2, $close - 1)) }
            [void]$word.Append($Text, $i, $close - $i)
            $i = $close - 1; $started = $true; $dynamic = $true
            continue
        }
        if ($c -eq '`') {
            $close = Find-PolicyBacktick $Text ($i + 1)
            . $substitute ($i + 1) $close
            [void]$word.Append($Text, $i, [Math]::Min($close + 1, $n) - $i)
            $i = $close; $started = $true; $dynamic = $true
            continue
        }
        if ($c -eq '$' -and $next -match '[A-Za-z0-9_{@*#?!$-]') {
            [void]$word.Append($c); $started = $true; $dynamic = $true
            continue
        }
        if ($c -eq '&' -and $next -eq '>') {
            . $endWord
            $i++
            if ($i + 1 -lt $n -and $Text[$i + 1] -eq '>') { $i++ }
            $redirectTarget = $true
            continue
        }
        if ($c -in @(';', '&', '|', '(', ')')) {
            . $endCommand
            if (($c -eq '&' -and $next -eq '&') -or ($c -eq '|' -and $next -in @('|', '&')) -or ($c -eq ';' -and $next -in @(';', '&'))) { $i++; $commandStart = [Math]::Min($i + 1, $n) }
            continue
        }
        if ($c -eq '<' -or $c -eq '>') {
            # A word of digits right before the operator is a file descriptor, not an argument.
            if ($started -and -not $quoted -and $word.ToString() -match '^\d+$') { [void]$word.Clear(); $started = $false } else { . $endWord }
            if ($c -eq '<' -and $next -eq '<') {
                if ($i + 2 -lt $n -and $Text[$i + 2] -eq '<') { $i += 2; $redirectTarget = $true; continue }
                $i++
                if ($i + 1 -lt $n -and $Text[$i + 1] -eq '-') { $i++ }
                $heredocNext = $true
                continue
            }
            if ($next -in @('>', '&', '|') -or ($c -eq '<' -and $next -eq '>')) { $i++ }
            $redirectTarget = $true
            continue
        }
        [void]$word.Append($c); $started = $true
    }
    $i = $n
    . $endCommand
}

# Emits the words of one PowerShell command element: @{ Value; Dynamic }. An array literal is one word per element.
function ConvertFrom-PolicyPwshElement {
    param($Element)
    switch ($Element.GetType().Name) {
        'StringConstantExpressionAst' { return [pscustomobject]@{ Value = $Element.Value; Dynamic = $false } }
        'ExpandableStringExpressionAst' { return [pscustomobject]@{ Value = $Element.Value; Dynamic = $Element.NestedExpressions.Count -gt 0 } }
        'CommandParameterAst' { return [pscustomobject]@{ Value = $Element.Extent.Text; Dynamic = $false } }
        'ScriptBlockExpressionAst' { return [pscustomobject]@{ Value = $Element.Extent.Text; Dynamic = $false } }
        'ArrayLiteralAst' { return $Element.Elements | ForEach-Object { ConvertFrom-PolicyPwshElement $_ } }
        default { return [pscustomobject]@{ Value = $Element.Extent.Text; Dynamic = $true } }
    }
}

# Returns the named and positional arguments of a cmdlet call: @{ Named = @{ name = element }; Positional = @() }.
function Get-PolicyPwshArguments {
    param($Elements, [string[]] $Switches)
    $named = @{}
    $positional = [Collections.Generic.List[object]]::new()
    for ($j = 1; $j -lt $Elements.Count; $j++) {
        $e = $Elements[$j]
        if ($e -is [Management.Automation.Language.CommandParameterAst]) {
            $key = $e.ParameterName.ToLowerInvariant()
            if ($null -ne $e.Argument) { $named[$key] = $e.Argument }
            elseif ($Switches | Where-Object { $_.StartsWith($key) }) { $named[$key] = $null }
            elseif ($j + 1 -lt $Elements.Count) { $named[$key] = $Elements[++$j] }
        } else { $positional.Add($e) }
    }
    return @{ Named = $named; Positional = $positional }
}

function Find-PolicyPwshArgument {
    param([hashtable] $Arguments, [string[]] $Names, [int] $Position)
    foreach ($key in $Arguments.Named.Keys) {
        if ($Names | Where-Object { $_.StartsWith($key) }) { return $Arguments.Named[$key] }
    }
    if ($Position -ge 0 -and $Position -lt $Arguments.Positional.Count) { return $Arguments.Positional[$Position] }
    return $null
}

# Emits the invocations of a PowerShell script; text PowerShell cannot parse falls back to the POSIX tokenizer.
function Get-PolicyPwshInvocations {
    param([string] $Text, [string] $Cwd, [int] $Depth)
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
    if ($errors.Count) {
        Get-PolicyPosixInvocations $Text $Cwd $Depth
        return
    }
    $commands = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true)
    foreach ($command in $commands) {
        $elements = $command.CommandElements
        $head = $elements[0]
        $source = $command.Extent.Text
        # & { ... } runs a script block whose own commands are found separately.
        if ($head -is [Management.Automation.Language.ScriptBlockExpressionAst]) { continue }
        $headWords = @(ConvertFrom-PolicyPwshElement $head)
        if ($headWords[0].Dynamic -or $head -isnot [Management.Automation.Language.StringConstantExpressionAst] -and $head -isnot [Management.Automation.Language.ExpandableStringExpressionAst]) {
            New-PolicyInvocation @($source) @($true) @{} $Cwd $source 'powershell' 'a command name computed at run time'
            continue
        }
        $name = (Get-PolicyExecutable $headWords[0].Value).ToLowerInvariant()
        if ($name -in @('set-location', 'cd', 'sl', 'chdir', 'push-location', 'pushd')) {
            $arguments = Get-PolicyPwshArguments $elements @('passthru')
            $path = Find-PolicyPwshArgument $arguments @('path', 'literalpath', 'lp') 0
            if ($null -ne $path) {
                $word = @(ConvertFrom-PolicyPwshElement $path)[0]
                if (-not $word.Dynamic -and $word.Value -notin @('-', '+')) { $Cwd = Resolve-PolicyPath $word.Value $Cwd }
            }
            continue
        }
        if ($name -in @('invoke-expression', 'iex')) {
            $arguments = Get-PolicyPwshArguments $elements @()
            $expression = Find-PolicyPwshArgument $arguments @('command') 0
            $word = if ($null -ne $expression) { @(ConvertFrom-PolicyPwshElement $expression)[0] } else { $null }
            if ($null -eq $word -or $word.Dynamic -or $expression -is [Management.Automation.Language.VariableExpressionAst]) {
                New-PolicyInvocation @($headWords[0].Value) @($false) @{} $Cwd $source 'powershell' 'Invoke-Expression of text computed at run time'
            } elseif ($Depth -lt $script:PolicyMaxDepth) {
                Get-PolicyPwshInvocations $word.Value $Cwd ($Depth + 1)
            }
            continue
        }
        if ($name -in @('start-process', 'saps', 'start')) {
            $arguments = Get-PolicyPwshArguments $elements @('loaduserprofile', 'nonewwindow', 'passthru', 'wait', 'usenewenvironment')
            $file = Find-PolicyPwshArgument $arguments @('filepath', 'pspath', 'path') 0
            $list = Find-PolicyPwshArgument $arguments @('argumentlist', 'args') 1
            $directory = Find-PolicyPwshArgument $arguments @('workingdirectory') -1
            if ($null -eq $file) { continue }
            $fileWord = @(ConvertFrom-PolicyPwshElement $file)[0]
            $listWords = if ($null -ne $list) { @(ConvertFrom-PolicyPwshElement $list) } else { @() }
            if ($fileWord.Dynamic -or @($listWords | Where-Object Dynamic).Count) {
                New-PolicyInvocation @($headWords[0].Value) @($false) @{} $Cwd $source 'powershell' 'Start-Process arguments computed at run time'
                continue
            }
            $here = $Cwd
            if ($null -ne $directory) {
                $directoryWord = @(ConvertFrom-PolicyPwshElement $directory)[0]
                if (-not $directoryWord.Dynamic) { $here = Resolve-PolicyPath $directoryWord.Value $Cwd }
            }
            # Start-Process joins its argument list with spaces into one command line, so the words split again.
            $argv = @($fileWord.Value) + @(Split-PolicyWords (($listWords | ForEach-Object Value) -join ' '))
            Resolve-PolicyInvocation $argv ([bool[]]@($argv | ForEach-Object { $false })) @{} $here $source 'powershell' $Depth
            continue
        }
        $words = @($elements | ForEach-Object { ConvertFrom-PolicyPwshElement $_ })
        $argv = [string[]]@($words | ForEach-Object Value)
        $flags = [bool[]]@($words | ForEach-Object Dynamic)
        Resolve-PolicyInvocation $argv $flags @{} $Cwd $source 'powershell' $Depth
    }
}

# Emits every invocation in $Command. Dialect is posix, powershell, or auto (PowerShell's parser first, the POSIX
# tokenizer for text it cannot parse). An array command is an argument vector that no shell interprets.
function Get-PolicyInvocations {
    param($Command, [string] $Dialect = 'auto', [string] $Cwd = $null, [int] $Depth = 0)
    if ($Command -is [array]) {
        $argv = [string[]]@($Command)
        if ($argv.Count) { Resolve-PolicyInvocation $argv ([bool[]]@($argv | ForEach-Object { $false })) @{} $Cwd ($argv -join ' ') 'argv' $Depth }
        return
    }
    if ($Dialect -eq 'posix') { Get-PolicyPosixInvocations $Command $Cwd $Depth }
    else { Get-PolicyPwshInvocations $Command $Cwd $Depth }
}
