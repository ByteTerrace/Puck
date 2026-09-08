[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Registry,
    [Parameter(Mandatory)] [ValidateSet('web-actors', 'world-silo')] [string] $Repository,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit,
    [string] $Archive,
    [string] $OutputFile
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest
$server = (az acr show --name $Registry --query loginServer --output tsv).Trim()
$tenant = (az account show --query tenantId --output tsv).Trim()
# Publishing uses an ACR-audience token; repository authorization stays under ABAC.
$accessToken = (az account get-access-token --resource https://containerregistry.azure.net --query accessToken --output tsv).Trim()
if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$accessToken" }
$response = Invoke-RestMethod -Method Post -Uri "https://$server/oauth2/exchange" -Body @{
    grant_type = 'access_token'; service = $server; tenant = $tenant; access_token = $accessToken
}
$refreshToken = $response.refresh_token
if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$refreshToken" }
$remoteImage = "${server}/${Repository}:$Commit"
try {
    $refreshToken | docker login $server --username 00000000-0000-0000-0000-000000000000 --password-stdin
    if ($Archive) { docker load --input $Archive }
    docker tag "puck/${Repository}:$Commit" $remoteImage
    docker push $remoteImage
    $image = docker image inspect $remoteImage | ConvertFrom-Json
    $digest = @($image[0].RepoDigests | Where-Object { $_.StartsWith("${server}/${Repository}@sha256:") })
    if ($digest.Count -ne 1) { throw "Expected one pushed digest for $remoteImage" }
    if ($OutputFile) { $digest[0] | Set-Content $OutputFile -Encoding utf8NoBOM }
    Write-Output $digest[0]
} finally {
    docker logout $server
}
