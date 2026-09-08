[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BundleDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($BundleDirectory)
$release = Get-Content (Join-Path $root 'release.json') -Raw | ConvertFrom-Json
if ($release.commit -ne $Commit -or $release.channel -ne 'stable') {
    throw 'Expected a stable production bundle for this workflow commit.'
}
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $release.files) {
    $relative = $file.path.Replace('\', '/')
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (!$path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -or
        !$paths.Add($relative) -or $relative -eq 'release.json') {
        throw "Invalid or duplicate artifact path: $relative"
    }
    if ($file.sha256 -notmatch '^[a-f0-9]{64}$' -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "Artifact checksum mismatch: $relative"
    }
}
$actual = @(Get-ChildItem $root -File -Recurse -Force | Where-Object FullName -ne (Join-Path $root 'release.json'))
if (!$paths.Count -or $actual.Count -ne $paths.Count) { throw 'The artifact file inventory is incomplete.' }
foreach ($file in $actual) {
    if (!$paths.Contains([IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/'))) {
        throw "Unlisted artifact file: $($file.FullName)"
    }
}
Write-Output "Verified $($paths.Count) files for $Commit."
