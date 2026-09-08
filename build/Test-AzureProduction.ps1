[CmdletBinding()]
param([Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$base = 'https://puck.byteterrace.com'
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
        $app = az containerapp show --name bytrccap001 --resource-group byteterrace --output json | ConvertFrom-Json
        if ($app.properties.latestReadyRevisionName -ne $app.properties.latestRevisionName -or $app.properties.provisioningState -ne 'Succeeded') { throw 'Actors has not made the release revision ready.' }
        $expectedImage = (Get-Content artifacts/web-actors.digest -Raw).Trim()
        if ($app.properties.template.containers[0].image -ne $expectedImage) { throw 'Actors image digest differs from the release.' }
        $release = Invoke-RestMethod "$base/release.json" -TimeoutSec 30
        if ($release.commit -ne $Commit) { throw 'Front Door dashboard release commit differs.' }
        $manifest = Invoke-RestMethod "$base/official/stable/manifest.json" -TimeoutSec 30
        if ($manifest.build.commit -ne $Commit) { throw 'Front Door official manifest commit differs.' }
        foreach ($file in $manifest.engine.files) {
            $response = Invoke-WebRequest "$base/official/$($file.path)" -Method Head -TimeoutSec 30
            $type = [string]($response.Headers['Content-Type'] | Select-Object -First 1)
            if ($type.Split(';')[0] -ne $file.contentType) { throw "Incorrect engine response type for $($file.name): $type" }
        }
        $token = (az account get-access-token --resource https://api.byteterrace.com --query accessToken --output tsv).Trim()
        if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$token" }
        $health = Invoke-RestMethod "$base/api/health-check" -Headers @{Authorization="Bearer $token"} -TimeoutSec 30
        if ($health.Status -ne 'Healthy') { throw 'Production API dependency health is not Healthy.' }
        Write-Output "PASS: production Actors digest, Front Door dashboard/content and API dependency health for $Commit."
        exit 0
    } catch {
        if ($attempt -eq 29) { throw }
        Write-Output "Waiting for production readiness: $($_.Exception.Message)"
        Start-Sleep -Seconds 10
    }
}
