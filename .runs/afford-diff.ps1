# Diffs two `world.affordances` captures on EVERYTHING the payload carries: per-verb
# metadata (routing / valueKind / bindable) AND per-channel metadata (shape / consumer).
# A name diff cannot see this class of change (a bindability flip moves no name), so any
# difference in either table FAILS: added and removed rows are listed with their metadata,
# changed rows field by field, and the exit code is 1 the moment either side disagrees.
# Exit 0 means the two captures carry identical verb and channel metadata.
#
#   pwsh -NoProfile -File .runs/afford-diff.ps1 <baseline-capture> <fresh-capture>
#
# Each capture is raw stdout from a windowed boot fed `world.affordances`: the narration
# `[world.affordances: <verbs-array>,"channels":<channels-array>]` — two JSON arrays, no
# enclosing object. Everything before the marker is ignored; a capture missing the
# channels array is refused rather than half-diffed.
param(
    [Parameter(Mandatory)][string]$BaselinePath,
    [Parameter(Mandatory)][string]$FreshPath
)

Set-StrictMode -Version Latest

# Returns @{ Document = parsed JsonDocument; End = index of the array's closing bracket }
# for the first JSON array at/after $from. The capture has trailing text after each array,
# so the walk tracks string/escape state and bracket depth to find the array's own end.
function Get-JsonArray([string]$text, [int]$from, [string]$path) {
    $start = $text.IndexOf('[', $from)
    if ($start -lt 0) { throw "no JSON array at/after index $from in $path" }
    $depth = 0; $inString = $false; $escaped = $false; $end = -1
    for ($i = $start; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        if ($escaped) { $escaped = $false; continue }
        if ($inString) {
            if ($c -eq '\') { $escaped = $true } elseif ($c -eq '"') { $inString = $false }
            continue
        }
        if ($c -eq '"') { $inString = $true }
        elseif ($c -eq '[') { $depth++ }
        elseif ($c -eq ']') { $depth--; if ($depth -eq 0) { $end = $i; break } }
    }
    if ($end -lt 0) { throw "unterminated JSON array in $path" }
    @{
        Document = [System.Text.Json.JsonDocument]::Parse($text.Substring($start, $end - $start + 1))
        End      = $end
    }
}

# Builds a name-keyed table from a JSON array of rows, keeping the named string/bool fields.
function Get-Table($jsonArray, [string[]]$fields) {
    $table = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    foreach ($entry in $jsonArray.Document.RootElement.EnumerateArray()) {
        $row = [ordered]@{}
        foreach ($field in $fields) {
            $property = $entry.GetProperty($field)
            $row[$field] = if ($property.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
                $property.GetString()
            } else {
                $property.GetBoolean()
            }
        }
        $table[$entry.GetProperty('name').GetString()] = $row
    }
    $table
}

# Returns @{ Verbs = name -> {routing, valueKind, bindable}; Channels = name -> {shape, consumer} }.
function Get-AffordanceTables([string]$path) {
    $text = Get-Content -Raw -Encoding UTF8 -LiteralPath $path
    $marker = $text.IndexOf('world.affordances:', [System.StringComparison]::Ordinal)
    if ($marker -lt 0) { throw "no 'world.affordances:' marker in $path" }
    $verbsArray = Get-JsonArray $text $marker $path
    $channelsMarker = $text.IndexOf('"channels":', $verbsArray.End, [System.StringComparison]::Ordinal)
    if ($channelsMarker -lt 0) { throw "no '""channels"":' array after the verbs array in $path" }
    $channelsArray = Get-JsonArray $text $channelsMarker $path
    @{
        Verbs    = Get-Table $verbsArray @('routing', 'valueKind', 'bindable')
        Channels = Get-Table $channelsArray @('shape', 'consumer')
    }
}

function Format-Metadata($meta) {
    (($meta.Keys | ForEach-Object {
        $value = $meta[$_]
        if ($value -is [bool]) { $value = $value.ToString().ToLowerInvariant() }
        "$_=$value"
    }) -join ' ')
}

# Prints +/-/~ rows for one name-keyed table pair (straight to stdout, so the count is the
# function's only pipeline output) and returns the difference count.
function Compare-Table($old, $new, [string]$label, [string[]]$fields) {
    $count = 0
    $oldNames = [string[]]$old.Keys; [System.Array]::Sort($oldNames, [System.StringComparer]::Ordinal)
    $newNames = [string[]]$new.Keys; [System.Array]::Sort($newNames, [System.StringComparer]::Ordinal)
    foreach ($name in $newNames) {
        if (-not $old.ContainsKey($name)) {
            [Console]::WriteLine("  +  $label $name $(Format-Metadata $new[$name])")
            $count++
        }
    }
    foreach ($name in $oldNames) {
        if (-not $new.ContainsKey($name)) {
            [Console]::WriteLine("  -  $label $name $(Format-Metadata $old[$name])")
            $count++
        }
    }
    foreach ($name in $oldNames) {
        if (-not $new.ContainsKey($name)) { continue }
        foreach ($field in $fields) {
            $before = $old[$name][$field]
            $after = $new[$name][$field]
            if (-not [object]::Equals($before, $after)) {
                if ($before -is [bool]) { $before = $before.ToString().ToLowerInvariant() }
                if ($after -is [bool]) { $after = $after.ToString().ToLowerInvariant() }
                [Console]::WriteLine("  ~  $label $name ${field}: $before -> $after")
                $count++
            }
        }
    }
    return $count
}

$old = Get-AffordanceTables $BaselinePath
$new = Get-AffordanceTables $FreshPath
Write-Output "affordances: verbs $($old.Verbs.Count) -> $($new.Verbs.Count); channels $($old.Channels.Count) -> $($new.Channels.Count)"

$differences = 0
$differences += Compare-Table $old.Verbs $new.Verbs 'verb' @('routing', 'valueKind', 'bindable')
$differences += Compare-Table $old.Channels $new.Channels 'channel' @('shape', 'consumer')

if ($differences -gt 0) {
    [Console]::Error.WriteLine("afford-diff.ps1: $differences affordance metadata difference(s).")
    exit 1
}
Write-Output 'affordance metadata: identical (verbs and channels)'
exit 0
