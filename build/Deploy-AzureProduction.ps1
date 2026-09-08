[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ArtifactsDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit,
    [string] $ResourceGroup = 'byteterrace'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest
$bundle = Join-Path $ArtifactsDirectory 'azure-applications'
& ./build/Test-AzureBundle.ps1 -BundleDirectory $bundle -Commit $Commit
$outputs = Get-Content artifacts/production-outputs.json -Raw | ConvertFrom-Json
if (Test-Path artifacts/azure) { throw 'Use a fresh deployment workspace.' }
Copy-Item -LiteralPath $bundle -Destination artifacts/azure -Recurse -Force
@{commit=$Commit;runId=$env:GITHUB_RUN_ID} | ConvertTo-Json | Set-Content artifacts/release-source.json -Encoding utf8NoBOM
foreach ($repository in @('web-actors', 'world-silo')) {
    & ./build/Publish-Container.ps1 -Registry bytrccrp000 -Repository $repository -Commit $Commit -Archive "$ArtifactsDirectory/azure-$repository/image.tar.gz" -OutputFile "artifacts/$repository.digest"
}
$configuration = Get-Content artifacts/azure/configuration.json -Raw | ConvertFrom-Json
foreach ($item in $configuration.items) {
    if ($item.key -eq 'Onboarding:ActorsBaseUrl') { $item.value = $outputs.actorsEndpoint.value }
}
$configuration | ConvertTo-Json -Depth 32 | Set-Content artifacts/production-configuration.json -Encoding utf8NoBOM
az appconfig kv import --name bytrcappcsp000 --auth-mode login --source file --path artifacts/production-configuration.json --format json --profile appconfig/kvset --yes --output none
$before = az containerapp show --name bytrccap001 --resource-group $ResourceGroup --output json | ConvertFrom-Json
@{revision=$before.properties.latestReadyRevisionName;image=$before.properties.template.containers[0].image} | ConvertTo-Json | Set-Content artifacts/previous-actor-release.json -Encoding utf8NoBOM
$image = (Get-Content artifacts/web-actors.digest -Raw).Trim()
az containerapp update --name bytrccap001 --resource-group $ResourceGroup --image $image --revision-suffix "git-$($Commit.Substring(0,12))-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT" --output none
if ($env:GITHUB_OUTPUT) { 'function-app-name=bytrcfuncp000' >> $env:GITHUB_OUTPUT }
