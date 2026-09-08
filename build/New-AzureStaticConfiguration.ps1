[CmdletBinding()]
param([string] $BundleDirectory = 'artifacts/azure')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$release = Get-Content "$BundleDirectory/release.json" -Raw | ConvertFrom-Json
$manifest = Get-Content "$BundleDirectory/official/$($release.channel)/manifest.json" -Raw | ConvertFrom-Json
$types = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
$objects = @($manifest.worldSchemaBundle) + @($manifest.documents) + @($manifest.composed) + @($manifest.assets) + @($manifest.engine.files)
foreach ($object in $objects) {
    if ($object.path -notmatch '^objects/sha256/[a-f0-9]{2}/[a-f0-9]{64}$' -or $object.contentType -notmatch '^[a-zA-Z0-9.+_-]+/[a-zA-Z0-9.+_-]+$') {
        throw 'Invalid object path or media type in the official manifest.'
    }
    if ($types.ContainsKey($object.path) -and $types[$object.path] -ne $object.contentType) { throw "Conflicting media types for $($object.path)" }
    $types[$object.path] = $object.contentType
}
# Content-addressed names have no extension. Exact locations preserve the manifest's
# media types so JavaScript module imports and WebAssembly streaming can execute.
$lines = foreach ($entry in $types.GetEnumerator()) {
    'location = /official/{0} {{ default_type "{1}"; try_files $uri =404; }}' -f $entry.Key, $entry.Value
}
$lines | Set-Content "$BundleDirectory/official-types.conf" -Encoding utf8NoBOM
