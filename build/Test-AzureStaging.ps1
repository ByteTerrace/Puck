[CmdletBinding()]
param([Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$outputs = Get-Content artifacts/staging-outputs.json -Raw | ConvertFrom-Json
$portal = $outputs.dashboardEndpoint.value
$api = $outputs.functionEndpoint.value
$token = (az account get-access-token --resource https://api.byteterrace.com --query accessToken --output tsv).Trim()
if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$token" }
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
        foreach ($name in @('bytrcactors-staging', 'bytrcsilo-staging', 'bytrcdashboard-staging')) {
            $app = az containerapp show --name $name --resource-group byteterrace --output json | ConvertFrom-Json
            if ($app.properties.provisioningState -ne 'Succeeded' -or !$app.properties.latestReadyRevisionName -or $app.properties.latestReadyRevisionName -ne $app.properties.latestRevisionName) {
                throw "$name has not made its newest revision ready."
            }
        }
        $health = Invoke-RestMethod "$api/health-check" -Headers @{ Authorization = "Bearer $token" } -TimeoutSec 30
        if ($health.Status -ne 'Healthy') { throw 'Functions dependency health check did not report Healthy.' }
        $deployed = Invoke-RestMethod "$portal/release.json" -TimeoutSec 30
        if ($deployed.commit -ne $Commit) { throw 'Dashboard release commit mismatch.' }
        $manifest = Invoke-RestMethod "$portal/official/staging/manifest.json" -TimeoutSec 30
        if ($manifest.build.commit -ne $Commit) { throw 'Official engine/content commit mismatch.' }
        foreach ($file in $manifest.engine.files) {
            $response = Invoke-WebRequest "$portal/official/$($file.path)" -Method Head -TimeoutSec 30
            $contentType = [string]($response.Headers['Content-Type'] | Select-Object -First 1)
            if ($contentType.Split(';')[0] -ne $file.contentType) { throw "Engine media type mismatch for $($file.name): $contentType" }
        }
        $unauthorized = Invoke-WebRequest "$api/health-check" -SkipHttpErrorCheck -TimeoutSec 30
        if ($unauthorized.StatusCode -ne 401) { throw 'Unauthenticated API access was not rejected with HTTP 401.' }
        Write-Output "PASS: authenticated Functions dependencies, rejected anonymous API access, dashboard and official content for $Commit"
        exit 0
    } catch {
        if ($attempt -eq 29) { throw }
        Write-Output "Waiting for staging readiness: $($_.Exception.Message)"
        Start-Sleep -Seconds 10
    }
}
