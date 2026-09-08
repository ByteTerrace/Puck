# Run from any directory. Package opt-ins live in each source project's csproj.
[CmdletBinding()]
param([string] $OutputDirectory = 'artifacts/packages')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path $output) {
        if (Get-ChildItem $output -Filter '*.nupkg') {
            throw "Package output already contains packages: $output. Choose an empty output directory."
        }
    }
    $projects = @(Get-ChildItem src -Directory | ForEach-Object { Get-ChildItem $_.FullName -Filter '*.csproj' } | Where-Object {
        ([xml](Get-Content $_.FullName -Raw)).SelectSingleNode('/Project/PropertyGroup/IsPackable[text()="true"]')
    } | Sort-Object FullName)
    if ($projects.Count -eq 0) { throw 'No packable projects found.' }

    $expected = @{}
    foreach ($project in $projects) {
        $metadata = (dotnet msbuild $project.FullName -nologo -getProperty:PackageId,Version | Out-String | ConvertFrom-Json).Properties
        if ($expected.ContainsKey($metadata.PackageId)) { throw "Duplicate package ID: $($metadata.PackageId)" }
        $expected[$metadata.PackageId] = $metadata.Version
        dotnet restore $project.FullName --locked-mode
        dotnet pack $project.FullName --configuration Release --no-restore --output $output
    }

    # Inspect the actual artifacts: a successful pack alone does not prove that
    # the referenced Puck packages are present in the release.
    $packages = @{}
    foreach ($file in Get-ChildItem $output -Filter '*.nupkg') {
        $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
        try {
            $entry = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
            if ($entry.Count -ne 1) { throw "Expected one nuspec in $($file.Name)." }
            $reader = [IO.StreamReader]::new($entry[0].Open())
            try { $spec = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
            $id = [string]$metadata.id
            $version = [string]$metadata.version
            if (!$expected.ContainsKey($id) -or $expected[$id] -ne $version -or $packages.ContainsKey($id)) {
                throw "Unexpected package identity: $id $version."
            }
            foreach ($name in @('README.md', 'LICENSE.md', 'LICENSING.md', 'icon.png')) {
                if (!$zip.GetEntry($name)) { throw "$id is missing $name." }
            }
            if (!(Test-Path (Join-Path $output "$id.$version.snupkg"))) { throw "$id is missing its symbol package." }
            $packages[$id] = $metadata
        } finally { $zip.Dispose() }
    }
    if ($packages.Count -ne $expected.Count) { throw 'The package set is incomplete.' }
    foreach ($id in $packages.Keys) {
        foreach ($dependency in $packages[$id].SelectNodes('.//*[local-name()="dependency"]')) {
            if ($dependency.id -like 'ByteTerrace.Puck.*' -or $dependency.id -like 'Puck.*') {
                if (!$packages.ContainsKey([string]$dependency.id)) {
                    throw "$id depends on missing release package $($dependency.id)."
                }
                # NuGet emits an inclusive minimum for a ProjectReference.
                $version = $expected[[string]$dependency.id]
                if ($dependency.version -notin @($version, "[$version, )", "[$version]")) {
                    throw "$id requires $($dependency.id) $($dependency.version), but this release carries $version."
                }
            }
        }
    }
    Write-Host "Validated $($packages.Count) packages and their internal dependency closure in $output."
} finally { Pop-Location }
