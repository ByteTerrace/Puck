# Offline regression checks for the deployment boundary; never contacts Azure.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($script in Get-ChildItem $PSScriptRoot -Filter '*Azure*.ps1') {
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$errors)
    if ($errors) { throw ($errors | Out-String) }
}
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = [IO.Path]::GetFullPath((Join-Path $temporaryRoot "puck-ci-$([Guid]::NewGuid())"))
if (!$fixture.StartsWith($temporaryRoot, [StringComparison]::Ordinal)) { throw 'Invalid test directory.' }
$commit = 'a' * 40
try {
    New-Item "$fixture/functions/.azurefunctions" -ItemType Directory -Force | Out-Null
    Set-Content "$fixture/functions/host.json" '{}'
    Set-Content "$fixture/functions/.azurefunctions/worker.json" '{}'
    $files = @(Get-ChildItem $fixture -Recurse -File -Force | ForEach-Object {
        @{path=[IO.Path]::GetRelativePath($fixture, $_.FullName).Replace('\','/');sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}
    })
    $release = @{commit=$commit;channel='stable';files=$files}
    function Save-Release { $release | ConvertTo-Json -Depth 8 | Set-Content "$fixture/release.json" }
    function Check-Rejection([string] $Expected) {
        Save-Release
        try { & "$PSScriptRoot/Test-AzureBundle.ps1" -BundleDirectory $fixture -Commit $commit; throw 'Expected bundle rejection.' }
        catch { if ($_.Exception.Message -notlike $Expected) { throw }; Write-Output "PASS: $Expected" }
    }
    Save-Release
    & "$PSScriptRoot/Test-AzureBundle.ps1" -BundleDirectory $fixture -Commit $commit
    $originalPath = $files[0].path
    $files[0].path = $originalPath.Replace('/', '\')
    Save-Release
    & "$PSScriptRoot/Test-AzureBundle.ps1" -BundleDirectory $fixture -Commit $commit
    $files[0].path = '../outside'
    Check-Rejection 'Invalid or duplicate artifact path:*'
    $files[0].path = $originalPath
    $originalHash = $files[0].sha256
    $files[0].sha256 = '0' * 64
    Check-Rejection 'Artifact checksum mismatch:*'
    $files[0].sha256 = $originalHash
    $release.files = @($files[0])
    Check-Rejection 'The artifact file inventory is incomplete.'
    $release.files = @($files[0], $files[0])
    Check-Rejection 'Invalid or duplicate artifact path:*'
    $release.files = $files
    $release.channel = 'dev'
    Check-Rejection 'Expected a stable production bundle*'
    $release.channel = 'stable'
    $release.commit = 'b' * 40
    Check-Rejection 'Expected a stable production bundle*'
    Write-Output 'PASS: deployment artifact guards, including hidden Functions files and Windows/Linux paths.'
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
