[CmdletBinding()]
param([Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit, [switch] $BeforeStaticPublication)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$base = 'https://puck.byteterrace.com'
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
        $app = az containerapp show --name bytrccap001 --resource-group byteterrace --output json | ConvertFrom-Json
        if ($app.properties.latestReadyRevisionName -ne $app.properties.latestRevisionName -or $app.properties.provisioningState -ne 'Succeeded') { throw 'Actors has not made the release revision ready.' }
        $expectedImage = (Get-Content artifacts/web-actors.digest -Raw).Trim()
        if ($app.properties.template.containers[0].image -ne $expectedImage) { throw 'Actors image digest differs from the release.' }
        $silo = az container show --name bytrcsilop000 --resource-group byteterrace --output json | ConvertFrom-Json
        $expectedSiloImage = (Get-Content artifacts/world-silo.digest -Raw).Trim()
        if ($silo.containers[0].image -ne $expectedSiloImage -or $silo.containers[0].instanceView.currentState.state -ne 'Running') { throw 'Primary silo has not started this release image.' }
        dotnet run build/Test-WorldSilo.cs -- world.byteterrace.com 33333 artifacts/world-silo.public-key
        $token = (az account get-access-token --resource https://api.byteterrace.com --query accessToken --output tsv).Trim()
        if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$token" }
        $health = Invoke-RestMethod "$base/api/health-check" -Headers @{Authorization="Bearer $token"} -TimeoutSec 30
        if ($health.Status -ne 'Healthy') { throw 'Production API dependency health is not Healthy.' }
        if ($BeforeStaticPublication) {
            Write-Output "PASS: this release's Actors, primary Puck world, and API are ready for website publication."
            return
        }
        $release = Invoke-RestMethod "$base/release.json" -TimeoutSec 30
        if ($release.commit -ne $Commit) { throw 'Front Door dashboard release commit differs.' }
        foreach ($hostName in @('byteterrace.com', 'docs.byteterrace.com', 'puck.byteterrace.com')) {
            $home = Invoke-WebRequest "https://$hostName/" -TimeoutSec 30
            if ($home.Content -notmatch '<html') { throw "Website entry point is missing at $hostName." }
            $docs = Invoke-WebRequest "https://$hostName/reference/index.html" -TimeoutSec 30
            if ($docs.Content -notmatch '<html' -or $docs.Headers['X-Frame-Options'] -contains 'DENY') { throw "Embedded documentation is unavailable at $hostName." }
            $configuration = Invoke-WebRequest "https://$hostName/configuration" -TimeoutSec 30
            if ([string]$configuration.Headers['Content-Type'] -notmatch 'json') { throw "Configuration route is missing at $hostName." }
        }
        $manifest = Invoke-RestMethod "$base/official/stable/manifest.json" -TimeoutSec 30
        if ($manifest.build.commit -ne $Commit) { throw 'Front Door official manifest commit differs.' }
        foreach ($file in $manifest.engine.files) {
            $response = Invoke-WebRequest "$base/official/$($file.path)" -Method Head -TimeoutSec 30
            $type = [string]($response.Headers['Content-Type'] | Select-Object -First 1)
            if ($type.Split(';')[0] -ne $file.contentType) { throw "Incorrect engine response type for $($file.name): $type" }
        }
        Write-Output "PASS: production image digests, Puck QUIC, website/docs/content and API dependency health for $Commit."
        exit 0
    } catch {
        if ($attempt -eq 29) { throw }
        Write-Output "Waiting for production readiness: $($_.Exception.Message)"
        Start-Sleep -Seconds 10
    }
}
