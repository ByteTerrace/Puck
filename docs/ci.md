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

## Azure application staging

`azure.yml` builds Functions, the dashboard, and the browser engine/content
bundle, compiles both infrastructure templates, and builds Linux containers
for Actors and World.Silo. The actor image must answer `/healthz` locally.
The dashboard integration tests receive a real browser engine and official
content tree. Every application payload is hashed in `release.json` and tied
to the checkout commit. This workflow also runs on `codex/azure-ci` while the
first hosted deployment is being qualified.

The existing `Blobs` environment uses `bytrcidpzzz` (client ID
`7508a16b-0f9b-4322-9bb9-481ad836c052`). Its Azure login variables are the same
three listed above. The actual GitHub subject is
`repo:ByteTerrace@18753984/Puck@1271519029:environment:Blobs`; this repository
uses [immutable OIDC subjects](https://docs.github.com/en/actions/reference/security/oidc).
Federation allows login; Azure role assignments still determine which operations
can succeed. The identity job verifies login and reads the target resources.

Run **Azure** manually with `deploy: true` and `channel: staging` to deploy
the artifacts from that run. The staging deployment runs only from `main` or
`codex/azure-ci`, and deployments serialize rather than cancel one another.
`src/Puck.Azure.Resources/staging.bicep` provisions these applications inside
the existing `byteterrace` resource group:

| Application | Azure resource |
| --- | --- |
| Functions API | `bytrcfunctions-staging`, with its own Flex Consumption plan |
| Provisioning actors | `bytrcactors-staging` |
| World authority host | `bytrcsilo-staging` |
| Dashboard and official browser content | `bytrcdashboard-staging` |

Staging shares the existing network, runtime identities, registry, and backing
Azure services. It is an application deployment environment, not a separate
tenant or security boundary. Both Functions and Actors select only the
`staging` App Configuration label, including their refresh sentinel and feature
flags; an omitted `ConfigurationStore:Label` preserves the existing unlabelled
production behavior. Staging also uses its own Orleans service ID, grain-state
container, and DataProtection key-ring blob.

CI pushes commit-tagged container images and deploys their registry digests.
The registry keeps ABAC repository permissions and ARM-audience authentication
disabled throughout. `build/Publish-Container.ps1` exchanges an ACR-audience
token as described in [Microsoft's registry authentication guidance](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-disable-authentication-as-arm).
Infrastructure planning and deployment outputs are retained as run artifacts.
The Functions action deploys the prebuilt payload without a remote rebuild.
To retry deployment after fixing only its workflow or infrastructure, supply
`artifact_run_id`: CI checks that run's application and container jobs passed
and deploys those exact artifacts. The deployment record retains both their
source run and commit; it never relabels old binaries as the current commit.

**Azure staging rollback** accepts a successful deployment run ID. It resolves
that deployment's original application artifact and recorded container digests,
restores their configuration, and repeats the deployment checks without rebuilding.
Keep both runs' artifacts and the registry digests for the required rollback
window. Rollback restores application code and configuration; it does not undo
tenant data writes or roll back the shared platform infrastructure.

The staging dashboard serves an uncompressed copy of the dashboard and its own
official content through Nginx. The existing production Front Door deployment
still uses the separate Brotli `dashboard-storage` tree. Deploying staging does
not synchronize or delete anything in the shared production website container.

The deployment check requires healthy authenticated Functions dependencies,
HTTP 401 for anonymous API access, and matching dashboard/content commit IDs.
The World.Silo staging node initially admits no worlds: world documents and
their private federation keys are workload configuration. A successful empty
node deployment does not prove world checkpoint recovery, gameplay, or scaling.
Interactive dashboard sign-in also requires its staging origin in the Entra
application's SPA redirect URI list.
