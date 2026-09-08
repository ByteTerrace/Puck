# CI and releases

GitHub Actions validates changes before they can be published. The build,
verification, and packaging workflows run on pull requests and pushes to
`main`; each also has a manual entry point in the Actions tab. Publishing calls
those workflows for the same commit and waits for all three to succeed.

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

`publish.yml` then calls `docs.yml` directly. Releases created with
`GITHUB_TOKEN` do not start another release-triggered workflow, so this explicit
call is necessary ([GitHub's event rules](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)).
If docs deployment fails after release creation, rerun the failed job or run
Docs manually against the release tag; rerunning the release workflow from
scratch sees the existing release and skips publishing.

Repository and service setup is still required:

- The `NuGet` environment and the ByteTerrace NuGet Trusted Publishing policy
  must agree on this repository, `publish.yml`, and the environment name.
  Configure environment protection and allowed branches to match the release
  policy.
- The `Blobs` environment needs `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and
  `AZURE_SUBSCRIPTION_ID` variables. The identity needs a federated credential
  for that environment and blob data permissions for `bytrcstp001`.
- Branch protection should require the build, package, and verification jobs.
  The YAML does not configure repository settings or external identities.

Hosted validation does not prove GPU parity, licensed BIOS-dependent emulator
stages, or end-user installation and self-update. Those remain separate release
qualification work. The current release publishes NuGet libraries and the
documentation site; desktop builds are downloadable CI artifacts.

## Azure production deployment

`azure.yml` builds the prebuilt Functions payload, the dashboard's existing
Storage/Front Door layout, and the stable official browser engine/content tree.
It tests the dashboard against the real engine and builds Linux images for
Actors and World.Silo. The actor image must answer `/healthz` before upload.
Every application payload is hashed in `release.json` and tied to its source
commit. Builds also run on `codex/azure-ci` while hosted deployment is qualified.

The platform build authenticates before restoring the pinned `ts/bvm` Template
Specs through `src/Puck.Azure.Resources/bicepconfig.json`. Private restore runs
on trusted pushes and manual runs; pull requests still build the applications
and containers without Azure credentials.

The existing `Blobs` GitHub environment uses `bytrcidpzzz`, client ID
`7508a16b-0f9b-4322-9bb9-481ad836c052`, with the three Azure variables listed above.
Its federated subject is
`repo:ByteTerrace@18753984/Puck@1271519029:environment:Blobs`.
The environment name identifies the federation boundary; deployment targets the
existing production resources. No separate staging environment is part of this
workflow.

Production uses `bytrcfuncp000`, Actors in `bytrccap001`, and the existing
`bytrcfdp000` Front Door profile over `bytrcstp001`. Dashboard files follow the
Brotli contract in `src/Puck.Dashboard/scripts/stageDeploy.mjs`; official files
carry the manifest's media types and content hashes. Publishing must upload
objects before switching the stable manifest, and preserve unrelated website
and tenant blobs.

World.Silo has a built container artifact but no production resource or workload
configuration in `main.bicep`. A container build alone does not deploy a hosted
world or establish checkpoint recovery.