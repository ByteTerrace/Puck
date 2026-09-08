[CmdletBinding()]
param([string] $ResourceGroup = 'byteterrace')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest
$outputs = Get-Content artifacts/production-outputs.json -Raw | ConvertFrom-Json
$owner = $outputs.worldSiloOwner.value
$endpoint = $outputs.worldSiloStorageEndpoint.value.TrimEnd('/')
$vault = $outputs.deploymentKeyVaultName.value
$secretName = 'PuckWorldFederationKey'
$secretNames = @(az keyvault secret list --vault-name $vault --query '[].name' --output json | ConvertFrom-Json)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('puck-silo-' + [Guid]::NewGuid())
New-Item $temporary -ItemType Directory | Out-Null
try {
    if ($secretName -notin $secretNames) {
        $key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
        try { $encodedKey = [Convert]::ToBase64String($key.ExportPkcs8PrivateKey()) } finally { $key.Dispose() }
        if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$encodedKey" }
        [IO.File]::WriteAllText("$temporary/key.txt", $encodedKey)
        az keyvault secret set --vault-name $vault --name $secretName --file "$temporary/key.txt" --output none
    }
    $encodedKey = (az keyvault secret show --vault-name $vault --name $secretName --query value --output tsv).Trim()
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$encodedKey" }
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $key.ImportPkcs8PrivateKey([Convert]::FromBase64String($encodedKey), [ref]$null)
        [IO.File]::WriteAllBytes('artifacts/world-silo.public-key', $key.ExportSubjectPublicKeyInfo())
    } finally { $key.Dispose() }
    $token = (az account get-access-token --resource https://storage.azure.com/ --query accessToken --output tsv).Trim()
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$token" }
    foreach ($file in Get-ChildItem artifacts/azure/silo-worlds -File) {
        $world = [Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($file.FullName))
        $name = $file.Name.Replace('.world.json', '')
        if ($name -eq 'puck') {
            $world['host']['authority'] = [Text.Json.Nodes.JsonValue]::Create('world.byteterrace.com:33333')
            $world['host']['listen'] = [Text.Json.Nodes.JsonValue]::Create('0.0.0.0:33333')
        }
        $path = "$temporary/$($file.Name)"
        [IO.File]::WriteAllText($path, $world.ToJsonString())
        $headers = @{Authorization="Bearer $token"; 'x-ms-version'='2023-11-03'; 'x-ms-date'=[DateTime]::UtcNow.ToString('R'); 'x-ms-blob-type'='BlockBlob'}
        Invoke-WebRequest "$endpoint/$owner/private/puck/hosted/$name/definition.json" -Method Put -Headers $headers -InFile $path -ContentType 'application/json' -MaximumRetryCount 3 -RetryIntervalSec 5 | Out-Null
    }
    $silo = @{
        schema='puck.silo.def.v1'
        worlds=@(@{owner=$owner; world='puck'; pinned=$true; federation=@{keyFile='/configuration/federation.pk8'}})
        doors=@{budget=1}; store=@{kind='Azure'; accountUrl=$endpoint}; stateDir='/state'; clustering=@{kind='Localhost'}
    }
    $parameters = @{
        image=@{value=(Get-Content artifacts/world-silo.digest -Raw).Trim()}
        identityResourceId=@{value=$outputs.worldSiloIdentityResourceId.value}
        clientId=@{value=$outputs.worldSiloClientId.value}
        siloDocument=@{value=($silo | ConvertTo-Json -Depth 8 -Compress)}
        federationKey=@{value=$encodedKey}
    }
    @{parameters=$parameters} | ConvertTo-Json -Depth 12 | Set-Content "$temporary/parameters.json" -Encoding utf8NoBOM
    az deployment group create --resource-group $ResourceGroup --name puck-world-silo --template-file src/Puck.Azure.Resources/worldSiloContainer.bicep --parameters "@$temporary/parameters.json" --output none
} finally {
    # This exact newly created directory contains secret material; never upload it with release diagnostics.
    if (![IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Secret cleanup path escaped the temporary directory.' }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
