[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Image)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$fixture = [IO.Path]::GetFullPath('artifacts/silo-smoke')
if (Test-Path $fixture) { throw 'Use a fresh silo smoke-test directory.' }
$owner = 'c3cba5cd-41c9-477e-a9de-15e1f4a4d1ec'
New-Item "$fixture/worlds" -ItemType Directory -Force | Out-Null
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    [IO.File]::WriteAllBytes("$fixture/federation.pk8", $key.ExportPkcs8PrivateKey())
    [IO.File]::WriteAllBytes("$fixture/public-key", $key.ExportSubjectPublicKeyInfo())
} finally { $key.Dispose() }
docker create --name silo-source $Image | Out-Null
try { docker cp silo-source:/app/worlds/. "$fixture/worlds" } finally { docker rm silo-source | Out-Null }
foreach ($source in Get-ChildItem "$fixture/worlds" -File) {
    $name = $source.Name.Replace('.world.json', '')
    $destination = "$fixture/store/$owner/private/puck/hosted/$name"
    New-Item $destination -ItemType Directory -Force | Out-Null
    $world = [Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($source.FullName))
    if ($name -eq 'puck') {
        $world['host']['authority'] = [Text.Json.Nodes.JsonValue]::Create('localhost:33333')
        $world['host']['listen'] = [Text.Json.Nodes.JsonValue]::Create('0.0.0.0:33333')
    }
    [IO.File]::WriteAllText("$destination/definition.json", $world.ToJsonString())
}
@{
    schema='puck.silo.def.v1'
    worlds=@(@{owner=$owner;world='puck';pinned=$true;federation=@{keyFile='/fixture/federation.pk8'}})
    doors=@{budget=1};store=@{kind='Directory';directoryPath='/fixture/store'};stateDir='/fixture/state';clustering=@{kind='Localhost'}
} | ConvertTo-Json -Depth 8 | Set-Content "$fixture/silo.json" -Encoding utf8NoBOM
$pointer = "/fixture/store/$owner/puck/hosted/puck/checkpoints/latest"
$previousCheckpoint = ''
for ($boot = 1; $boot -le 2; $boot++) {
    docker run -d --name silo-smoke --mount "type=bind,source=$fixture,target=/fixture" -p 33333:33333/udp $Image --silo /fixture/silo.json | Out-Null
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 90; $attempt++) {
            if ((docker inspect silo-smoke --format '{{.State.Running}}') -ne 'true') { throw 'Silo exited before checkpointing the primary world.' }
            # The store correctly creates private directories. Read through the container, which owns them.
            $checkpoint = [string](docker exec silo-smoke sh -c 'if [ -f "$1" ]; then sha256sum "$1"; fi' probe $pointer)
            if ($checkpoint -and $checkpoint -ne $previousCheckpoint) { $ready = $true; break }
            Start-Sleep -Seconds 2
        }
        if (!$ready) { throw "Silo boot $boot did not activate and checkpoint Puck." }
        dotnet run build/Test-WorldSilo.cs -- 127.0.0.1 33333 "$fixture/public-key"
        $previousCheckpoint = $checkpoint
        Write-Output "PASS: Puck boot $boot activated, checkpointed, and accepted an authenticated QUIC connection."
    } finally {
        docker logs silo-smoke
        docker rm -f silo-smoke | Out-Null
    }
}
