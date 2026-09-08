[CmdletBinding()]
param([Parameter(Mandatory)] [ValidatePattern('^[0-9]+$')] [string] $RunId)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest
$repository = $env:GITHUB_REPOSITORY
$run = gh api "repos/$repository/actions/runs/$RunId" | ConvertFrom-Json
if ($run.head_repository.full_name -ne $repository -or
    $run.path -ne '.github/workflows/azure.yml' -or
    $run.event -notin @('push', 'workflow_dispatch') -or
    $run.head_branch -notin @('main', 'codex/azure-ci') -or
    $run.head_sha -notmatch '^[a-f0-9]{40}$') {
    throw 'Artifacts must come from the trusted Azure workflow in this repository on main or codex/azure-ci.'
}
$pages = gh api "repos/$repository/actions/runs/$RunId/jobs?filter=latest&per_page=100" --paginate --slurp | ConvertFrom-Json
$jobs = @($pages | ForEach-Object jobs)
foreach ($name in @('applications', 'containers (Puck.Actors, web-actors)', 'containers (Puck.World.Silo, world-silo)')) {
    $matches = @($jobs | Where-Object name -eq $name)
    if ($matches.Count -ne 1 -or $matches[0].conclusion -ne 'success') { throw "Artifact source has no successful $name job." }
}
"run-id=$RunId" >> $env:GITHUB_OUTPUT
"commit=$($run.head_sha)" >> $env:GITHUB_OUTPUT
Write-Output "Validated artifact source $RunId at $($run.head_sha)."
