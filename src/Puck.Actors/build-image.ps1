<# Builds from the repository root, pushes a commit-tagged image, and deploys its digest. #>
[CmdletBinding()]
param(
    [switch] $NoRestart,
    [string] $Registry = 'bytrccrp000',
    [string] $ResourceGroup = 'byteterrace',
    [string] $ContainerApp = 'bytrccap001'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $root
try {
    if (git status --porcelain --untracked-files=normal) { throw 'Commit the release sources before building an image.' }
    $commit = (git rev-parse HEAD).Trim()
    docker build --file src/Puck.Actors/Dockerfile --tag "puck/web-actors:$commit" .
    $digestFile = Join-Path ([IO.Path]::GetTempPath()) "puck-actors-$([Guid]::NewGuid()).txt"
    try {
        & ./build/Publish-Container.ps1 -Registry $Registry -Repository web-actors -Commit $commit -OutputFile $digestFile
        if (!$NoRestart) {
            $image = (Get-Content $digestFile -Raw).Trim()
            az containerapp update --name $ContainerApp --resource-group $ResourceGroup --image $image --revision-suffix "git-$($commit.Substring(0,12))" --output none
        }
    } finally { Remove-Item -LiteralPath $digestFile -Force -ErrorAction SilentlyContinue }
} finally { Pop-Location }
