[CmdletBinding()]
param([string] $ResourceGroup = 'byteterrace')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest
$env:BICEPPARAM_OWNER_OBJECT_ID = (az identity show --name bytrcidpzzz --resource-group $ResourceGroup --query principalId --output tsv).Trim()
$env:BICEPPARAM_OWNER_PRINCIPAL_TYPE = 'ServicePrincipal'
# Fail before changing Azure resources when CI cannot reconcile the declared Entra app roles.
$graph = az ad sp show --id 00000003-0000-0000-c000-000000000000 --query '{id:id,roles:appRoles}' --output json | ConvertFrom-Json
$assigned = az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/$env:BICEPPARAM_OWNER_OBJECT_ID/appRoleAssignments" --query value --output json | ConvertFrom-Json
foreach ($permission in @('Application.ReadWrite.OwnedBy', 'Application.Read.All', 'AppRoleAssignment.ReadWrite.All', 'GroupMember.Read.All')) {
    $role = @($graph.roles | Where-Object {$_.value -eq $permission -and 'Application' -in $_.allowedMemberTypes})
    if ($role.Count -ne 1 -or !($assigned | Where-Object {$_.resourceId -eq $graph.id -and $_.appRoleId -eq $role[0].id})) {
        throw "CI is missing Microsoft Graph $permission. Complete the operator identity setup before deployment."
    }
}
# CI already has its deployment grants; a release does not assign itself Owner.
$env:BICEPPARAM_DEPLOY_OWNER_ROLE_ASSIGNMENTS = 'false'
New-Item artifacts -ItemType Directory -Force | Out-Null
az bicep build --file src/Puck.Azure.Resources/main.bicep --outfile artifacts/production-infrastructure.json
az bicep build-params --file src/Puck.Azure.Resources/main.bicepparam --outfile artifacts/production-parameters.json
$parameters = Get-Content artifacts/production-parameters.json -Raw | ConvertFrom-Json
# Infrastructure reconciliation must preserve the currently deployed application image.
$image = (az containerapp show --name $parameters.parameters.resources.value.actors.name --resource-group $ResourceGroup --query 'properties.template.containers[0].image' --output tsv).Trim()
$parameters.parameters | Add-Member -NotePropertyName actorsImage -NotePropertyValue @{ value = $image } -Force
$parameters | ConvertTo-Json -Depth 100 | Set-Content artifacts/production-parameters.json -Encoding utf8NoBOM
az deployment group what-if --resource-group $ResourceGroup --template-file artifacts/production-infrastructure.json --parameters artifacts/production-parameters.json --no-pretty-print > artifacts/production-plan.json
$plan = Get-Content artifacts/production-plan.json -Raw | ConvertFrom-Json
if ($plan.status -ne 'Succeeded') { throw 'Production infrastructure planning failed.' }
if (@($plan.changes | Where-Object changeType -eq Delete).Count) { throw 'Production plan includes resource deletion; inspect the retained plan.' }
$plan.changes | Group-Object changeType | Select-Object Name, Count | Format-Table
az deployment group create --name puck-production-platform --resource-group $ResourceGroup --template-file artifacts/production-infrastructure.json --parameters artifacts/production-parameters.json --query properties.outputs --output json > artifacts/production-outputs.json
