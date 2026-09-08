[CmdletBinding()]
param([string] $Registry = 'bytrccrp000')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$registryState = az acr show --name $Registry --output json | ConvertFrom-Json
if ($registryState.roleAssignmentMode -ne 'AbacRepositoryPermissions' -or $registryState.policies.azureADAuthenticationAsArmPolicy.status -ne 'disabled') {
    throw 'Expected repository ABAC and disabled ARM-audience authentication.'
}
$server = $registryState.loginServer
$tenant = (az account show --query tenantId --output tsv).Trim()
$access = (az account get-access-token --resource https://containerregistry.azure.net --query accessToken --output tsv).Trim()
if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$access" }
$refresh = (Invoke-RestMethod "https://$server/oauth2/exchange" -Method Post -Body @{grant_type='access_token';service=$server;tenant=$tenant;access_token=$access}).refresh_token
if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$refresh" }
foreach ($repository in @('web-actors', 'world-silo', 'mcr/vsmarketplace/vscode-private-marketplace')) {
    $token = (Invoke-RestMethod "https://$server/oauth2/token" -Method Post -Body @{grant_type='refresh_token';service=$server;scope="repository:${repository}:pull,push,delete";refresh_token=$refresh}).access_token
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$token" }
    $payload = $token.Split('.')[1].Replace('-','+').Replace('_','/')
    $payload = $payload.PadRight($payload.Length + (4 - $payload.Length % 4) % 4, '=')
    $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
    $actions = @($claims.access | Where-Object name -eq $repository | ForEach-Object actions)
    if (('pull' -notin $actions -or 'push' -notin $actions -or 'delete' -notin $actions)) { throw "CI lacks expected repository access: $repository." }
    Write-Output "PASS repository scope: $repository; granted actions: $($actions -join ',')"
}
