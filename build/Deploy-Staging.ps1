[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ArtifactsDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9]+$')] [string] $ArtifactRunId,
    [switch] $ReuseImages,
    [string] $ResourceGroup = 'byteterrace',
    [string] $Registry = 'bytrccrp000'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$bundle = Join-Path $ArtifactsDirectory 'azure-applications'
$release = Get-Content "$bundle/release.json" -Raw | ConvertFrom-Json
if ($release.commit -ne $Commit -or $release.channel -ne 'staging') { throw 'The bundle must be a staging release of the expected commit.' }
$bundleRoot = [IO.Path]::GetFullPath($bundle) + [IO.Path]::DirectorySeparatorChar
foreach ($file in $release.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $bundle $file.path))
    if (!$path.StartsWith($bundleRoot, [StringComparison]::Ordinal)) { throw "Invalid artifact path: $($file.path)" }
    if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Artifact checksum mismatch: $($file.path)" }
}
New-Item artifacts/azure -ItemType Directory -Force | Out-Null
Copy-Item "$bundle/*" artifacts/azure -Recurse -Force
@{ commit = $Commit; runId = $ArtifactRunId } | ConvertTo-Json | Set-Content artifacts/release-source.json -Encoding utf8NoBOM
if ($ReuseImages) {
    $server = (az acr show --name $Registry --query loginServer --output tsv).Trim()
    foreach ($repository in @('web-actors', 'world-silo', 'dashboard')) {
        $digest = (Get-Content "$ArtifactsDirectory/azure-deployment/$repository.digest" -Raw).Trim()
        if ($digest -notmatch ('^' + [regex]::Escape("$server/$repository") + '@sha256:[a-f0-9]{64}$')) { throw "Invalid retained $repository image digest." }
        $digest | Set-Content "artifacts/$repository.digest" -Encoding utf8NoBOM
    }
} else {
    foreach ($repository in @('web-actors', 'world-silo')) {
        & ./build/Publish-Container.ps1 -Registry $Registry -Repository $repository -Commit $Commit -Archive "$ArtifactsDirectory/azure-$repository/image.tar.gz" -OutputFile "artifacts/$repository.digest"
    }
    docker build --file build/azure/Dashboard.Dockerfile --tag "puck/dashboard:$Commit" .
    & ./build/Publish-Container.ps1 -Registry $Registry -Repository dashboard -Commit $Commit -OutputFile artifacts/dashboard.digest
}

$domain = (az containerapp env show --name bytrccaep000 --resource-group $ResourceGroup --query properties.defaultDomain --output tsv).Trim()
$configuration = Get-Content artifacts/azure/configuration.json -Raw | ConvertFrom-Json
foreach ($item in $configuration.items) {
    $item.label = 'staging'
    if ($item.key -eq 'Onboarding:ActorsBaseUrl') { $item.value = "https://bytrcactors-staging.$domain" }
}
$configuration | ConvertTo-Json -Depth 32 | Set-Content artifacts/staging-configuration.json -Encoding utf8NoBOM
az appconfig kv import --name bytrcappcsp000 --auth-mode login --source file --path artifacts/staging-configuration.json --format json --profile appconfig/kvset --yes --output none

$parameters = @{
    actorsImage = @{ value = (Get-Content artifacts/web-actors.digest -Raw).Trim() }
    siloImage = @{ value = (Get-Content artifacts/world-silo.digest -Raw).Trim() }
    dashboardImage = @{ value = (Get-Content artifacts/dashboard.digest -Raw).Trim() }
    revision = @{ value = "git-$($Commit.Substring(0,12))-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT" }
}
@{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'; contentVersion = '1.0.0.0'; parameters = $parameters } | ConvertTo-Json -Depth 5 | Set-Content artifacts/staging-parameters.json -Encoding utf8NoBOM
az deployment group what-if --resource-group $ResourceGroup --template-file src/Puck.Azure.Resources/staging.bicep --parameters artifacts/staging-parameters.json --no-pretty-print > artifacts/staging-plan.json
az deployment group create --name "puck-staging-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT" --resource-group $ResourceGroup --template-file src/Puck.Azure.Resources/staging.bicep --parameters artifacts/staging-parameters.json --query properties.outputs --output json > artifacts/staging-outputs.json
$outputs = Get-Content artifacts/staging-outputs.json -Raw | ConvertFrom-Json
# CI owns this registration. Preserve every existing callback while adding staging.
$applicationUrl = 'https://graph.microsoft.com/v1.0/applications/aa086681-3252-4166-9f53-eb0e3173ca30'
$application = az rest --method get --url "$applicationUrl`?`$select=spa" --output json | ConvertFrom-Json
$redirects = @($application.spa.redirectUris)
if ($outputs.dashboardEndpoint.value -notin $redirects) {
    @{ spa = @{ redirectUris = @($redirects) + $outputs.dashboardEndpoint.value } } | ConvertTo-Json -Depth 4 | Set-Content artifacts/staging-redirect.json -Encoding utf8NoBOM
    az rest --method patch --url $applicationUrl --body '@artifacts/staging-redirect.json' --output none
}
if ($env:GITHUB_OUTPUT) { "function-app-name=$($outputs.functionAppName.value)" >> $env:GITHUB_OUTPUT }
Write-Output "Staging infrastructure deployed for $Commit"
