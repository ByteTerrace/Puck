[CmdletBinding()]
param([string] $OutputDirectory = 'artifacts/docs')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
dotnet tool restore
dotnet docfx docs/api/docfx.json --warningsAsErrors
if (!(Test-Path docs/api/_site/index.html)) { throw 'DocFX omitted its entry point.' }
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
foreach ($prefix in @('reference', '_theme')) {
    if (Test-Path (Join-Path $OutputDirectory $prefix)) { throw "Use an output directory without existing $prefix content." }
}
Copy-Item docs/api/_site (Join-Path $OutputDirectory 'reference') -Recurse
Copy-Item docs/site/index.html (Join-Path $OutputDirectory 'reference/overview.html')
Copy-Item docs/site/_theme (Join-Path $OutputDirectory '_theme') -Recurse
# The website owns index.html and its navigation. Documentation publishes only these prefixes.
