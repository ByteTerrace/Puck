# Bootstrap administrator operation. The single CI identity owns deployment and
# access reconciliation within this resource group; runtime grants stay separate.
[CmdletBinding()]
param([string] $ResourceGroup = 'byteterrace')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$groupId = (az group show --name $ResourceGroup --query id --output tsv).Trim()
$ci = (az identity show --name bytrcidpzzz --resource-group $ResourceGroup --query principalId --output tsv).Trim()
$assignments = az role assignment list --assignee-object-id $ci --scope $groupId --output json | ConvertFrom-Json
$owners = @($assignments | Where-Object { $_.scope -eq $groupId -and $_.roleDefinitionName -eq 'Owner' })
if ($owners.Count -ne 1) { throw 'Expected exactly one existing CI Owner assignment at the resource-group scope.' }
$assignment = $owners[0]
if (!$assignment.PSObject.Properties['condition'] -or !$assignment.condition) { Write-Output 'CI already administers this resource group.'; exit 0 }
New-Item artifacts -ItemType Directory -Force | Out-Null
$assignment | ConvertTo-Json -Depth 10 | Set-Content artifacts/ci-delegation-before.json -Encoding utf8NoBOM
$assignment.condition = $null
$assignment | Add-Member -NotePropertyName conditionVersion -NotePropertyValue $null -Force
$assignment | ConvertTo-Json -Depth 10 | Set-Content artifacts/ci-delegation-update.json -Encoding utf8NoBOM
az role assignment update --role-assignment '@artifacts/ci-delegation-update.json' --query '{principal:principalId,scope:scope,condition:condition}' --output json
