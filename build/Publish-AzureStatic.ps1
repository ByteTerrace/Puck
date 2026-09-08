[CmdletBinding()]
param(
    [string] $BundleDirectory = 'artifacts/azure',
    [string] $ResourceGroup = 'byteterrace'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest
$release = Get-Content "$BundleDirectory/release.json" -Raw | ConvertFrom-Json
if ($release.channel -ne 'stable') { throw 'Production requires the stable content channel.' }
$outputs = Get-Content artifacts/production-outputs.json -Raw | ConvertFrom-Json
$container = $outputs.officialContentContainerName.value
if ($container -notmatch '^[a-f0-9-]{36}$') { throw 'Missing official-content container in production deployment outputs.' }
$endpoint = [Uri]$outputs.staticSiteEndpoint.value
$account = $endpoint.Host.Split('.')[0]
$token = (az account get-access-token --resource https://storage.azure.com/ --query accessToken --output tsv).Trim()
if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$token" }
function Send-Blob([string] $Container, [string] $Name, [string] $File, [string] $ContentType, [string] $CacheControl, [string] $Encoding = '') {
    $path = (@($Container) + @($Name.Split('/')) | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    $headers = @{
        Authorization = "Bearer $token"
        'x-ms-version' = '2023-11-03'
        'x-ms-date' = [DateTime]::UtcNow.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
        'x-ms-blob-type' = 'BlockBlob'
        'x-ms-blob-cache-control' = $CacheControl
        'x-ms-meta-commit' = $release.commit
        'x-ms-meta-sha256' = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($Encoding) { $headers['x-ms-blob-content-encoding'] = $Encoding }
    Invoke-WebRequest "https://$($endpoint.Host)/$path" -Method Put -Headers $headers -InFile $File -ContentType $ContentType -TimeoutSec 300 -MaximumRetryCount 3 -RetryIntervalSec 5 | Out-Null
}
$manifest = Get-Content "$BundleDirectory/official/stable/manifest.json" -Raw | ConvertFrom-Json
$types = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($object in @($manifest.worldSchemaBundle) + @($manifest.documents) + @($manifest.composed) + @($manifest.assets) + @($manifest.engine.files)) {
    if ($object.path -notmatch '^objects/sha256/[a-f0-9]{2}/[a-f0-9]{64}$') { throw 'Invalid official object path.' }
    if ($types.ContainsKey($object.path) -and $types[$object.path] -ne $object.contentType) { throw 'Conflicting official media types.' }
    $types[$object.path] = $object.contentType
}
foreach ($entry in $types.GetEnumerator()) {
    Send-Blob $container "public/puck/official/$($entry.Key)" "$BundleDirectory/official/$($entry.Key)" $entry.Value 'public,max-age=31536000,immutable'
}
Write-Output "Published $($types.Count) content-addressed official objects."
Send-Blob $container 'public/puck/official/stable/manifest.json' "$BundleDirectory/official/stable/manifest.json" 'application/json' 'no-cache'
$mediaTypes = @{'.html'='text/html';'.js'='application/javascript';'.mjs'='application/javascript';'.css'='text/css';'.json'='application/json';'.map'='application/json';'.svg'='image/svg+xml';'.png'='image/png';'.ico'='image/x-icon';'.woff2'='font/woff2';'.wasm'='application/wasm';'.txt'='text/plain'}
$siteRoot = [IO.Path]::GetFullPath("$BundleDirectory/dashboard-storage")
# Keep old hashed assets for open clients; publish the website entry point after its dependencies.
$files = Get-ChildItem $siteRoot -File -Recurse | Sort-Object @{Expression={ if ($_.FullName -eq (Join-Path $siteRoot 'index.html')) { 1 } else { 0 } }}, FullName
foreach ($file in $files) {
    $name = [IO.Path]::GetRelativePath($siteRoot, $file.FullName).Replace('\','/')
    $type = $mediaTypes[$file.Extension.ToLowerInvariant()]
    if (!$type) { $type = 'application/octet-stream' }
    $encoding = if ($name -eq 'index.html' -or $name.StartsWith('assets/')) { 'br' } else { '' }
    Send-Blob '$web' $name $file.FullName $type 'no-cache' $encoding
}
Send-Blob '$web' 'release.json' "$BundleDirectory/release.json" 'application/json' 'no-store'
az afd endpoint purge --resource-group $ResourceGroup --profile-name bytrcfdp000 --endpoint-name default --content-paths '/*' --output none
Write-Output "Published production dashboard and stable manifest for $($release.commit) to $account."
