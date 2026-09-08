# Operator setup for the single CI identity. These Microsoft Graph application
# permissions are tenant-wide; this script is never invoked by a deployment job.
[CmdletBinding()]
param([string] $ResourceGroup = 'byteterrace')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ci = (az identity show --name bytrcidpzzz --resource-group $ResourceGroup --query principalId --output tsv).Trim()
$graph = az ad sp show --id 00000003-0000-0000-c000-000000000000 --query '{id:id,roles:appRoles}' --output json | ConvertFrom-Json
$assigned = az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/$ci/appRoleAssignments" --query value --output json | ConvertFrom-Json
foreach ($permission in @('Application.Read.All', 'AppRoleAssignment.ReadWrite.All')) {
    $role = @($graph.roles | Where-Object {$_.value -eq $permission -and 'Application' -in $_.allowedMemberTypes})
    if ($role.Count -ne 1) { throw "Cannot resolve Graph permission $permission." }
    if ($assigned | Where-Object {$_.resourceId -eq $graph.id -and $_.appRoleId -eq $role[0].id}) { continue }
    $body = @{principalId=$ci;resourceId=$graph.id;appRoleId=$role[0].id}
    $file = Join-Path ([IO.Path]::GetTempPath()) ('puck-ci-graph-' + [Guid]::NewGuid() + '.json')
    try {
        $body | ConvertTo-Json | Set-Content $file -Encoding utf8NoBOM
        az rest --method post --url "https://graph.microsoft.com/v1.0/servicePrincipals/$ci/appRoleAssignments" --body "@$file" --output none
        Write-Output "Granted $permission to the CI identity."
    } finally { Remove-Item -LiteralPath $file -Force }
}
