# CI and releases

GitHub Actions validates each pull request and push to `main` through `azure.yml`.
It calls the reusable build and verification workflows and waits for their gates
before deployment. Packaging runs separately on the same changes; a versioned
NuGet release calls build, verification, packaging, and documentation for its own
commit before publishing.

All external actions are pinned to full commit SHAs, as required by this
repository's Actions policy. Keep the adjacent version comments when updating
the pins; a version tag by itself prevents the workflow from starting.

## Build and validate

`build.yml` installs the .NET SDK from `global.json`, the `wasm-tools`
workload, and a versioned, checksum-checked DXC archive on Windows. It restores
the solution in locked mode, builds Release with warnings as errors, runs the
solution's tests, and publishes `Puck.World` as a framework-dependent artifact.
The download requires .NET 10 and suitable graphics hardware to run. A build
artifact is not a signed installer or a verified GPU rendering session.
Tests stop after fifteen minutes without a test event and collect a small hang
dump. The workflow uploads the MSBuild binary log, available TRX results, and
test diagnostics even when a later step fails.

`verify.yml` owns the emulator batteries, browser AppBundle and Node harness,
and generated schema/name-registry checks. Its nightly frontier measures
known failing or inconclusive emulator cases separately from release gates.
Test-report uploads remain available on fork pull requests; posting GitHub
check annotations is limited to other events because fork tokens are read-only.

Repository tools and verification projects share `build/RepositoryPaths.cs`.
It finds the checkout by walking from the executable directory, then the working
directory, to `Puck.slnx`. Runtime data lookup therefore works with CI's mapped
compiler source paths; it never treats a PDB path such as `/_/` as a disk path.

`pack.yml` runs the same command a contributor can use locally:

```powershell
./build/Pack.ps1 -OutputDirectory artifacts/packages
```

Use an output directory without existing `.nupkg` files. The script discovers
explicit source-project `IsPackable` opt-ins, reads IDs and versions through
MSBuild, restores locked dependencies, and packs every opted-in library. It
then opens the actual packages to check their identities, symbol packages,
README, licenses, icon, and internal dependency closure. Missing release
dependencies fail before the artifact can reach NuGet.org.

For the full local build, install DXC on `PATH` and the workload first:

```powershell
dotnet workload install wasm-tools
$env:CI = 'true'
dotnet restore Puck.slnx --locked-mode
dotnet build Puck.slnx -c Release --no-restore
dotnet test Puck.slnx -c Release --no-build --no-restore
```

The browser project owns its runtime identifier. Use its
[documented publish command](../src/Puck.World.Browser/README.md#build-and-publish)
without a global `-r browser-wasm`, which would change the shared libraries'
lock graphs. Machines with Cosmocc also need the documented `C_INCLUDE_PATH`
cleanup before building WebAssembly.

## Publish

The shared `Version` in `build/Packaging.targets` is the release version.
Bumping that file on `main` starts `publish.yml`; a manual run on `main` can
retry a failed release. Other branches cannot enter the publish gate. If a
GitHub Release already exists for `v<Version>`, the run does nothing.

After build, verification, and packaging succeed, NuGet Trusted Publishing
exchanges the job's OIDC token for a short-lived key. It pushes the exact
`nuget-packages` artifact from that run, skips already published versions on
retry, and creates the GitHub Release. No package API key is stored here.

`docs.yml` builds and validates documentation without Azure credentials. The
Azure application bundle includes that documentation, so the website, docs,
Functions, containers, and official content are built from one commit. Running
Docs manually builds an artifact; it does not overwrite the website.

Repository and service setup is still required:

- The `NuGet` environment and the ByteTerrace NuGet Trusted Publishing policy
  must agree on this repository, `publish.yml`, and the environment name.
  Configure environment protection and allowed branches to match the release
  policy.
- The `Puck` environment needs `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and
  `AZURE_SUBSCRIPTION_ID` variables. The identity needs a federated credential
  for that environment and blob data permissions for `bytrcstp001`.
- Branch protection should require the build, package, and verification jobs.
  The YAML does not configure repository settings or external identities.

Hosted validation does not prove GPU parity, licensed BIOS-dependent emulator
stages, or end-user installation and self-update. Those remain separate release
qualification work. The current release publishes NuGet libraries and the
documentation artifact; Azure deploys the website. Desktop builds are downloadable CI artifacts.

## Azure production deployment

`azure.yml` builds the Functions payload, the existing Storage/Front Door website
layout with its documentation page, the stable official browser engine/content,
and Linux images for Actors and World.Silo. Dashboard tests use the real engine.
Actors must answer `/healthz`; the silo must activate the primary Puck world,
checkpoint it, accept a QUIC connection with the expected key, and repeat those
checks after container replacement using the same store.

A push to `main` deploys after all build and verification jobs succeed. A manual
run exposes one `deploy` switch; setting it to false performs build and validation
only. `codex/azure-ci` also builds and supports manual deployment while this path
is qualified. Pull requests have no Azure credentials. Trusted infrastructure
builds authenticate before restoring the pinned `ts/bvm` Template Specs through
`src/Puck.Azure.Resources/bicepconfig.json`.

The `Puck` GitHub environment supplies `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and
`AZURE_SUBSCRIPTION_ID` for the single CI identity, `bytrcidpzzz` (client ID
`7508a16b-0f9b-4322-9bb9-481ad836c052`). Its federated subject is
`repo:ByteTerrace@18753984/Puck@1271519029:environment:Puck`.
The workflow targets production; it creates no staging environment.

One concurrency lock covers platform reconciliation and application deployment.
Once admitted, a run checks that its commit is still the branch tip. It consumes
only artifacts from its own run, verifies the exact commit, every path and hash,
and the complete file inventory, including hidden Functions payload files. An
older run cannot substitute its payload into a newer infrastructure deployment.
The platform reconciliation preserves the current Actors image and declared
subnets, records a what-if plan, rejects resource deletion, and applies
`main.bicep`. It does not grant CI Owner; that is operator-managed setup.

Production uses `bytrcfuncp000`, Actors in `bytrccap001`, and `bytrcfdp000` Front
Door over `bytrcstp001`. Image deployment uses immutable digests. Functions uses
the official action with the prebuilt payload; an `always()` cleanup restores
SCM restrictions after temporarily admitting the runner's IPv4 address.

The website owns `$web/index.html`. `/docs` selects its documentation page;
`docs.byteterrace.com` opens that page directly, and `puck.byteterrace.com` opens
World Studio. DocFX and the documentation overview occupy `/reference/`, with
shared styles under `/_theme/`. They ship inside the application bundle, never
from a competing Docs publisher. The dashboard staging script supplies Brotli
host files. Official objects retain their manifest media types and immutable
hash paths; the publisher uploads objects before the stable manifest, website
dependencies before its entry point, and the release marker last. Existing
hashed assets remain available to open clients. Transient upload failures retry
within a bound; publishing finishes with a Front Door purge and live checks.

The primary Puck world runs in `bytrcsilop000` (Azure Container Instances) at
`world.byteterrace.com:33333`. World uses QUIC/UDP; Container Apps ingress only
supports HTTP/TCP. `Prepare-WorldSilo.cs` uses the engine composer to package Puck
and its referenced neighbours, translating file references into hosted-world
names. Only Puck is pinned. Its checkpoints and journals use Azure Blob Storage;
Orleans membership is local to this single process. A replacement container
recovers persisted state. Recovery retains the checkpoint's world definition;
changing an already running world's authored state requires a world migration,
not deleting checkpoints during deployment.

The silo identity `bytrcidp008` can read only `world-silo` in ACR and has a custom
`Puck World Store` role on its own blob container: container read/create plus
blob read/write, with no delete or role-management grant. CI retains its signing
key in Key Vault and injects it as a secret volume; the runtime needs no vault
permissions. Secret parameter files are removed and excluded from artifacts.
The image installs `libmsquic`, which .NET requires for Linux QUIC support.

ACR uses `AbacRepositoryPermissions`. CI publishes through Repository Contributor;
Actors reads only `web-actors`, the marketplace only its image, and the silo only
`world-silo`. Runtime identities receive no Catalog Lister. Existing broad grants
need explicit reconciliation because incremental Bicep deployments do not remove
them. CI uses an ACR-audience token. ARM-audience authentication is enabled because
Container Apps managed-identity pulls require it; repository authorization remains
ABAC-controlled.

Deployment retains the source commit, image digests, previous Actors revision,
infrastructure plan and outputs, and SCM restoration snapshot. Live checks cover
container versions, the primary world QUIC endpoint, website/docs routes, official
manifest and engine media types, and API dependency health. These checks do not
replace interactive browser or GPU qualification.
