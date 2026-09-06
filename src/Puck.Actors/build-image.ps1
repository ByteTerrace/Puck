<#
    Builds and pushes the Puck.Actors image, then restarts the container app revision.

    ACR quick tasks are incompatible with this registry's hardened steady state (ABAC repository
    permissions + ARM-audience tokens disabled), so the registry is temporarily flipped to a
    compatible state for the duration of the build and always restored afterward — including on
    failure. The durable replacement for this script is an ACR Task resource with a managed
    identity (ABAC-compatible) or a CI pipeline; until then, run this.
#>
[CmdletBinding()]
param(
    [string] $ContextPath = $PSScriptRoot,
    [string] $Image = 'web-actors:latest',
    [switch] $NoRestart,
    [string] $Registry = 'bytrccrp000',
    [string] $ResourceGroup = 'byteterrace'
)

$ErrorActionPreference = 'Stop'

Write-Host "Flipping $Registry to a build-compatible state..."
az acr config authentication-as-arm update --registry $Registry --status enabled | Out-Null
az acr update --name $Registry --role-assignment-mode rbac --only-show-errors | Out-Null

try {
    az acr build --registry $Registry --image $Image $ContextPath

    if ($LASTEXITCODE -ne 0) {
        throw "az acr build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Write-Host "Restoring $Registry hardened state..."
    az acr update --name $Registry --role-assignment-mode rbac-abac --only-show-errors | Out-Null
    az acr config authentication-as-arm update --registry $Registry --status disabled | Out-Null
}

if (-not $NoRestart) {
    $revision = az containerapp show --name bytrccap001 --resource-group $ResourceGroup --query 'properties.latestRevisionName' --output tsv --only-show-errors

    Write-Host "Restarting revision $revision..."
    az containerapp revision restart --name bytrccap001 --resource-group $ResourceGroup --revision $revision
}

