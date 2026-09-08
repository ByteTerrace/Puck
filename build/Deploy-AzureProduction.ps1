[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ArtifactsDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9]+$')] [string] $ArtifactRunId,
    [string] $ResourceGroup = 'byteterrace'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest
$bundle = Join-Path $ArtifactsDirectory 'azure-applications'
$release = Get-Content "$bundle/release.json" -Raw | ConvertFrom-Json
if ($release.commit -ne $Commit -or $release.channel -ne 'stable') { throw 'Expected a stable production bundle for the source commit.' }
$bundleRoot = [IO.Path]::GetFullPath($bundle) + [IO.Path]::DirectorySeparatorChar
foreach ($file in $release.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $bundle $file.path.Replace('\', '/')))
    if (!$path.StartsWith($bundleRoot, [StringComparison]::Ordinal)) { throw "Invalid artifact path: $($file.path)" }
    if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Artifact checksum mismatch: $($file.path)" }
}
$platform = az deployment group show --name puck-production-platform --resource-group $ResourceGroup --output json | ConvertFrom-Json
if ($platform.properties.provisioningState -ne 'Succeeded') { throw 'Reconcile the production platform successfully before publishing applications.' }
New-Item artifacts/azure -ItemType Directory -Force | Out-Null
Copy-Item "$bundle/*" artifacts/azure -Recurse -Force
@{commit=$Commit;runId=$ArtifactRunId} | ConvertTo-Json | Set-Content artifacts/release-source.json -Encoding utf8NoBOM
foreach ($repository in @('web-actors', 'world-silo')) {
    & ./build/Publish-Container.ps1 -Registry bytrccrp000 -Repository $repository -Commit $Commit -Archive "$ArtifactsDirectory/azure-$repository/image.tar.gz" -OutputFile "artifacts/$repository.digest"
}
$configuration = Get-Content artifacts/azure/configuration.json -Raw | ConvertFrom-Json
foreach ($item in $configuration.items) {
    if ($item.key -eq 'Onboarding:ActorsBaseUrl') { $item.value = $platform.properties.outputs.actorsEndpoint.value }
}
$configuration | ConvertTo-Json -Depth 32 | Set-Content artifacts/production-configuration.json -Encoding utf8NoBOM
az appconfig kv import --name bytrcappcsp000 --auth-mode login --source file --path artifacts/production-configuration.json --format json --profile appconfig/kvset --yes --output none
$before = az containerapp show --name bytrccap001 --resource-group $ResourceGroup --output json | ConvertFrom-Json
@{revision=$before.properties.latestReadyRevisionName;image=$before.properties.template.containers[0].image} | ConvertTo-Json | Set-Content artifacts/previous-actor-release.json -Encoding utf8NoBOM
$image = (Get-Content artifacts/web-actors.digest -Raw).Trim()
az containerapp update --name bytrccap001 --resource-group $ResourceGroup --image $image --revision-suffix "git-$($Commit.Substring(0,12))-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT" --output none
if ($env:GITHUB_OUTPUT) { 'function-app-name=bytrcfuncp000' >> $env:GITHUB_OUTPUT }
