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
The solution build produces the browser AppBundle for the CLI integration tests,
which resolve it inside the current checkout. GPU tests skip when D3D11 reports an unsupported
device, including the video capability needed by the shared-texture cleanup test.
The native timing benchmark is tagged `Category=Performance` and excluded
from this shared-runner gate; its three-second ceiling remains available locally.
Tests stop after fifteen minutes without a test event and collect a small hang
dump. The workflow uploads the MSBuild binary log, available TRX results, and
test diagnostics even when a later step fails.

`verify.yml` owns the emulator batteries, browser AppBundle and Node harness,
and generated schema/name-registry checks. Its nightly frontier measures
known failing or inconclusive emulator cases separately from release gates.
Test reports are retained as artifacts and job summaries on every event, including
fork pull requests. Verification needs only a read-only repository token.

## Automatic PR formatting

`format.yml` runs on every pull request. It builds the candidate Puck CLI and
the solution, formats only the PR's added or modified C# files, verifies that a
second pass would make no further changes, and compiles the result. Renamed
files use their new paths. Generated source and `experimental/` are excluded.
An unchanged file is never swept into the formatting commit. Standalone C# apps
are formatted in disposable SDK projects with their declared references and
linked helpers; their operational code is compiled, never executed.

For branches in this repository, `format-submit.yml` automatically appends a
`style: apply Puck formatting` bot commit. There is no developer installation,
Git hook, or build-time Git configuration. Pull the updated branch before
continuing work locally. A stale formatting run cannot overwrite a newer push:
the submission checks the PR's base and head and uses GitHub's atomic
`expectedHeadOid` commit operation. Protected, default, base, and deployment
branches are not bypassed. A branch shared by multiple open PRs is left alone.

The formatter runs with a read-only token. A separate `workflow_run` job runs
the SDK-only `build/FormatSubmit.cs` from the default branch and reads the
artifact as data. Its shared `build/FormatSubmission.cs` policy is also compiled
by the CLI tests. It checks the producing workflow and successful build job,
limits artifact size and file count, and accepts only ordinary C# files already
changed by that PR. The write token never reaches the PR's build or formatter.

After committing, the submitter explicitly dispatches formatting, Azure's
build/verification workflow with deployment disabled, packaging, and docs for
the updated branch. This avoids relying on unattended checks from a
`GITHUB_TOKEN` push. A retry after a successful commit resumes these dispatches
without creating another commit.

The **Formatting** check fails on the original commit while a fix is needed and
passes on the formatted commit. Add this check to the repository's required
status checks after the workflow lands on the default branch. Workflow YAML
cannot make a check mandatory in a GitHub ruleset. Both formatting workflows
must be on the default branch before automatic submission can operate.

Fork PRs receive the same check and a `puck-format` artifact containing
`format.patch`; the repository token cannot push to a contributor's fork.
Download the artifact from the Format run, then apply and commit the patch:

```sh
git apply --check /path/to/format.patch
git apply /path/to/format.patch
```

The artifact is retained for seven days. Failed builds or nonconvergent
formatting produce no applicable commit. The Apply formatting job summary
explains stale, fork, or otherwise inapplicable results.

The local equivalent, in a clean disposable checkout at the PR head, is:

```sh
puck format ci <base-sha> <head-sha> artifacts/format
```

It prepares the validated artifact and patch without committing or pushing.

## Shared repository tooling

Repository tools, verification projects, and C# file apps share `build/RepositoryPaths.cs`.
It finds the checkout by walking from the executable directory, then the working
directory, to `Puck.slnx`. Runtime data lookup therefore works with CI's mapped
compiler source paths; it never treats a PDB path such as `/_/` as a disk path.

`pack.yml` runs the same command a contributor can use locally:

```sh
puck nuget pack artifacts/packages
```

Run from the repository root and use an empty output directory. The CLI discovers
explicit source-project `IsPackable` opt-ins, reads IDs and versions through
MSBuild, restores locked dependencies, and packs every opted-in library and the CLI tool. It
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

## The CLI used by CI

`ByteTerrace.Puck.Cli` is a .NET tool package with the command name `puck`.
It participates in the shared version and selectable package batches. Its tool
payload contains its runtime dependencies; installing it does not require
publishing every Puck library in the same batch.

The official CLI version CI consumes lives in `.config/dotnet-tools.json`.
The `setup-puck` action installs that exact package into an isolated tool
directory using the repository's `nuget.config`, then adds it to the job's PATH.
A failed official restore fails the job. CI never silently replaces a missing
or broken published package with a local build.

Before the first official release, `.config/puck-bootstrap.json` explicitly
enables source bootstrap and the tool manifest has no Puck entry. In that state,
`build/Toolchain.cs` restores and packs the CLI directly with the SDK, then
installs the local package. No Puck command is required to manufacture the first
CLI. A missing pin with bootstrap disabled is an error.

`pack.yml` installs the exact candidate package on clean Windows and Linux
runners and exercises command dispatch, native-backed search, declarations,
and a Roslyn workspace query. Publishing waits for both installation gates.
After a batch containing the CLI is uploaded, `publish.yml` verifies NuGet.org
installation and emits a `puck-cli-pin` patch artifact. Apply that patch in the
next source change to adopt the release and disable initial bootstrap. The same
operation can be run locally after the package becomes available:

```sh
dotnet run -c Release --file build/Toolchain.cs -- pin 0.1.0-alpha
```

The command verifies installation before editing the manifest or bootstrap
policy. It does not upload packages or commit files. Subsequent releases use the
previously adopted CLI to orchestrate their package release; the new CLI adopts
the shared version without forcing an immediate change to CI's tooling pin.

Source-dependent operations use the candidate CLI explicitly. Schema and name
registry checks already build the checkout's CLI, the Azure application job
requests `candidate: true`, and the silo Docker build publishes its own composer.
An older CLI's embedded world model must not validate a new checkout's schema.

For local use after a pin is committed, run `dotnet tool restore --configfile
nuget.config`, then `dotnet tool run puck -- <command>`. Before that first pin,
`dotnet run -c Release --file build/Toolchain.cs -- setup` installs a local CLI
at `.tmp/puck-ci/puck` (`puck.exe` on Windows).
Both `setup` and `candidate` accept an optional fresh tool-directory argument for
isolated local checks; they never replace an existing installation directory.

## Publish

The shared `Version` in `build/Packaging.targets` names the release. All selected
packages receive that version, including internal project-reference dependencies.
Packages omitted from a release keep their last published version; they can skip
versions and rejoin the shared version later. Use `major.minor.patch` with an
optional lowercase prerelease suffix such as `1.2.0-rc.1`. The pack gate rejects
per-project version overrides and ambiguous version spellings.

Publishing is explicit: bumping the version or merging to `main` does not upload
packages. After merging the intended version and source changes, open Actions →
**Release & publish** → **Run workflow** on `main`. Set `packages` to `all`, a full
package ID, or comma-separated full IDs. `publish` defaults to false: the run
validates and prepares downloadable artifacts without creating a tag, release,
or NuGet upload. Run with `publish` true when ready to release.

For example, these GitHub CLI commands validate one package, publish two, or
publish every opted-in package at the version in the selected source:

```sh
gh workflow run publish.yml --ref main -f packages=ByteTerrace.Puck.Maths -F publish=false
gh workflow run publish.yml --ref main -f packages=ByteTerrace.Puck.Maths,ByteTerrace.Puck.Abstractions -F publish=true
gh workflow run publish.yml --ref main -f packages=all -F publish=true
```

Every run builds, tests, packs, and validates documentation for its own commit.
Packing still checks the full opted-in package set. `puck nuget prepare`
then selects exactly the requested IDs from those artifacts. An omitted internal
dependency must already be available on NuGet.org at the shared version; otherwise
preparation fails with the missing ID. Include it and its dependencies, or use
`all`. The workflow never silently expands a selection or rewrites dependencies
to an older version. A newly uploaded dependency may need time to become visible
in NuGet's index before a separate batch can depend on it.

The `prepare` job summary lists the selected packages. Its `nuget-release`
artifact contains only those `.nupkg` and `.snupkg` files and a `release.json`
manifest recording the version, source commit, file checksums, and dependencies
already on NuGet.org. The publisher checks those checksums and uploads these
files in dependency order after all gates succeed. It does not rebuild them.
Selection and failure paths have offline tests in `tests/Puck.Cli.Tests`:

```sh
dotnet test tests/Puck.Cli.Tests -c Release --filter FullyQualifiedName~NuGetCommandTests
```

`puck nuget --help` lists the release commands. The tests construct package
fixtures and check selection, dependency failures, provenance, checksums, and
upload arguments without publishing anything.

Before the first upload, the publisher reserves `v<Version>` at the validated
commit. A different commit needs a new version. To retry a partially completed
run, use GitHub's rerun command; to publish another batch at the same version
after `main` has advanced, dispatch from the existing tag:

```sh
gh workflow run publish.yml --ref v1.2.0 -f packages=ByteTerrace.Puck.Assets -F publish=true
```

Only `main` or that version's tag can release, and its commit must belong to
`main`'s history. Protect `v*` tags against modification and deletion. NuGet
uploads are not transactional: successful uploads remain if a later upload
fails. Retrying skips duplicate versions and explicitly retries symbol uploads.
An existing GitHub Release does not block another batch; each completed batch
adds its own manifest asset, named for the run ID and attempt. The tag binds the
whole version to one source commit, while the manifests record its package batches.

[NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
exchanges the publishing job's OIDC token for a short-lived key. No package API
key is stored here.

`docs.yml` builds and validates documentation without Azure credentials. The
Azure application bundle includes that documentation, so the website, docs,
Functions, containers, and official content are built from one commit. Running
Docs manually builds an artifact; it does not overwrite the website.

Repository and service setup is still required:

- The `NuGet` environment and the ByteTerrace NuGet Trusted Publishing policy
  must agree on this repository, `publish.yml`, and the environment name.
  Allow both `main` and `v*` tags in the environment's deployment rules so retries
  and additional batches can use the original source. Configure reviewers there
  if release approval is required. Scope the policy to `ByteTerrace.Puck.*` and
  allow new packages as well as new versions when publishing the first release.
- The `Puck` environment needs `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and
  `AZURE_SUBSCRIPTION_ID` variables. The identity needs a federated credential
  for that environment and blob data permissions for `bytrcstp001`.
- Branch protection should require the build, package, and verification jobs.
  The YAML does not configure repository settings or external identities.

Hosted validation does not prove GPU parity, licensed BIOS-dependent emulator
stages, or end-user installation and self-update. Those remain separate release
qualification work. The current release publishes NuGet libraries, the CLI tool, and the
documentation artifact; Azure deploys the website. Desktop builds are downloadable CI artifacts.

## Azure production deployment

`build/Azure.cs` owns cloud orchestration. Run it from the repository root with
`dotnet run -c Release --file build/Azure.cs -- <command>`; `--help` lists its
operations. It invokes Puck CLI for world preparation, documentation builds,
bundle manifests, and QUIC probes. `bootstrap.cs` remains a separate identity-team
operation. The workflows contain no PowerShell deployment scripts.
`build/InstallDxc.cs` installs the checksum-pinned compiler before source builds.


`azure.yml` builds the Functions payload, the existing Storage/Front Door website
layout with its documentation page, the stable official browser engine/content,
and Linux images for Actors and World.Silo. Dashboard tests use the real engine.
Actors must answer `/healthz`; the silo must activate the primary Puck world,
checkpoint it, accept a QUIC connection with the expected key, and repeat those
checks after container replacement using the same store.
The candidate CLI and browser build use pinned DXC. Both official manifests
check clean source provenance; a build that changes tracked shader artifacts
must reconcile those generated files before a release can claim a clean checkout.

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

The identity team runs `src/Puck.Azure.Resources/bootstrap.cs` outside CI to
create zzz, configure its GitHub and Azure DevOps federation, grant Graph access,
and author its constrained resource-group Owner assignment. Bootstrap delegates
an explicit set of deployment roles to service principals (including zzz itself)
and the application user group. That set excludes Owner, Contributor and access
administrator roles. It covers the custom World Store role before the silo
identity exists. Existing Owner assignments are updated in place. Bootstrap also
grants zzz ACR Repository Contributor at resource-group scope so the image build
jobs can push before platform reconciliation. This bootstrap grant applies only
to CI; runtime pull grants remain repository-specific.

Application deployment references zzz as an existing identity. It can assign
itself the declared data roles on App Configuration, Key Vault, ACR and publishing
containers, within the bootstrap delegation. Runtime assignments retain their
resource scopes and ABAC conditions. CI does not create zzz, change its federation,
or manage its Owner condition or Graph grants.

Microsoft Graph also requires
`Application.Read.All` and `AppRoleAssignment.ReadWrite.All` to reconcile the
declared application permission assignments; Azure Owner does not provide those
tenant-wide permissions. The identity-team bootstrap declares these alongside
`Application.ReadWrite.OwnedBy` and `GroupMember.Read.All`.
Bootstrap rejects execution inside CI. One-time repairs are performed by an
operator outside the repository and are never workflow steps. The infrastructure
script checks the Graph grants before changing resources. See Microsoft's
[app-role assignment permission requirements](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-approleassignedto?view=graph-rest-1.0).

One concurrency lock covers platform reconciliation and application deployment.
Once admitted, a run checks that its commit is still the branch tip. It consumes
only artifacts from its own run, verifies the exact commit, every path and hash,
and the complete file inventory, including hidden Functions payload files. An
older run cannot substitute its payload into a newer infrastructure deployment.
The platform reconciliation preserves the current Actors image and declared
subnets, records a what-if plan, rejects resource deletion, and applies
`main.bicep`. It does not grant CI Owner; that is identity-team setup.

Production uses `bytrcfuncp000`, Actors in `bytrccap001`, and `bytrcfdp000` Front
Door over `bytrcstp001`. Image deployment uses immutable digests. Functions uses
the official action with the prebuilt payload; an `always()` cleanup restores
SCM restrictions after temporarily admitting the runner's IPv4 address.
The API health check uses Front Door's existing origin authentication; it does
not need an additional application permission or a token from CI.

The website owns `$web/index.html`. `/docs` selects its documentation page;
`docs.byteterrace.com` opens that page directly, and `puck.byteterrace.com` opens
World Studio. DocFX and the documentation overview occupy `/reference/`, with
shared styles under `/_theme/`. They ship inside the application bundle, never
from a competing Docs publisher. The dashboard staging script supplies Brotli
host files. Official objects retain their manifest media types and immutable
hash paths; the publisher uploads objects before the stable manifest, website
dependencies before its entry point, and the release marker last. Existing
hashed assets remain available to open clients. Transient upload failures retry
within a bound. Services must pass readiness checks before website publication;
publishing finishes with a Front Door purge and live checks.

The primary Puck world uses a Flexible VM scale set, `bytrcvmssp000`, at
`play.puck.byteterrace.com:7825` (PUCK on a telephone keypad). A static public IP and UDP load balancer preserve
the endpoint during worker replacement. The first release permits exactly one
regular worker, with manual application upgrades, automatic guest patching, and
automatic replacement after sustained simulation failure. It does not
claim distributed placement, continuous availability, or Spot recovery.

`play` is the default entry alias. Reserve `w-<id>` for permanent world addresses
and `h-<id>` for hosting services under `puck.byteterrace.com`; friendly world
names are aliases. World moves and restores retain identity, while independent
clones receive new identities. Keep region, owner, and subscription tier in
metadata and resource tags so placement and ownership changes do not rename a
world. Connection information still carries the federation address and expected
server identity: DNS alone does not select a world on a shared host.

World workers use the pinned Azure Linux 3.0 Marketplace image selected in
`main.bicepparam`. The silo container retains its Ubuntu-based .NET runtime;
the host OS and container libraries are upgraded independently. The bootstrap
installs Microsoft's Moby packages on Azure Linux and retains Ubuntu support
for rollback. It preserves the host firewall policy and installs only the
configured QUIC UDP port and the health port from Azure's load-balancer probe
address. The service restores those rules after reboot. Host-image qualification
must exercise public QUIC with the expected world key, checkpoint recovery,
drain/readiness withdrawal, and reboot recovery before changing the pinned image.
The production release check compares the running VM's image reference as well
as its container digest. A distribution change requires draining and replacing
the worker; changing only the scale-set model does not establish that its
existing VM runs the new OS.

The `Puck world` workflow builds and tests the silo independently of website and
Entra application reconciliation. Before publishing, it runs the Entra admission,
silo schema, and lifecycle recovery laws with locked dependencies, then boots the
candidate container twice to verify checkpoint recovery and QUIC. It deploys with
the same `zzz` identity and production concurrency group.
`build/Azure.cs -- deploy-world-platform` creates the
runtime identity and its scoped grants. Its `deploy-world` command publishes composed
world definitions, deploys the VMSS model and applies it to existing workers.
The VM extension pulls the immutable image before declaring the release ready.
The existing process must finish its drain before published content or configuration
is replaced. Deployment records the current versions of hosted definitions,
checkpoint pointers, journals, and release markers in `world-rollback.json`.
After verification, the protected `PuckWorldReleaseState` vault secret retains
the successful image and compute parameters. A failed subsequent release stops
the candidate, restores the recorded blob versions and previous parameters,
and checks the previous public QUIC endpoint. Immutable checkpoint objects remain
addressed by their content hashes. Blob versioning must be enabled before release.
An existing worker without a recorded successful release requires operator
adoption or a drained replacement before this transaction can run.
A failed first deployment restores pre-release persistence and returns the
scale set to zero, allowing a clean retry.

`puck world prepare` packages Puck and its referenced neighbours. Only Puck is
pinned; Orleans membership is explicitly local. Checkpoints and journals live in
Azure Blob Storage. The lifecycle extension monitors Azure Scheduled Events
outside simulation. Its drain freezes all rows at one pump boundary, closes
ingress, waits for outstanding persistence, and writes final checkpoints. The
local HTTP retirement operation and normal host shutdown use that same drain.
A failed save fails deployment. VM events are never acknowledged early on behalf
of other processes. An Azure-requested reboot, including `az vm restart`, first
emits a Scheduled Event with a [15-minute notice window](https://learn.microsoft.com/en-us/azure/virtual-machines/linux/scheduled-events#event-scheduling).
The world drains when notified, so it remains unavailable while Azure waits to
reboot the VM. Allow that window before evaluating reboot recovery.

After checkpoint recovery, changed published content passes
through the existing world hot-reload submission. The release marker advances
only after the rebuilt world is checkpointed. An unchanged marker preserves the
recovered state; a failed checkpoint can be retried without applying the rebuild
twice. Changing the world's listening identity requires a fresh worker activation;
the deployment's container restart establishes the published binding before
reconciling an older checkpoint. A live reload cannot change that binding.
If a restart finds the rebuilt definition already in its checkpoint, it commits
the missing marker while retaining the recovered simulation state.

`/healthz` withdraws load-balancer readiness when simulation progress, checkpoint
freshness, or journal persistence fails. `/livez` observes simulation progress
independently, so a storage outage does not cause the scale set to replace a
worker holding unsaved state. The world platform defines its own action group
through the current AVM module and always includes it in the readiness alert.
Its name, email receivers, and pet-name tags are authored in
`resources.worldSiloActionGroup` in `main.bicepparam`; additional existing action
groups can be supplied through `monitoring.actionGroupResourceIds`.
The nonroot container has a read-only root filesystem, writable state mount,
bounded temporary filesystem, no Linux capabilities, and rotating local logs.
Image cleanup retains the running release and one previous unused release.

Testers use the existing ByteTerrace API identity and `user_impersonation` scope.
Membership in ByteTerrace API Users admits a Puck user; the deployed world allows
up to sixteen network players. The public `world-authentication.json` release
artifact selects the client authentication extension and pins the server key.
See [Puck's connection instructions](../src/Puck.World/README.md#run-it).

Before enabling Spot or adding workers, implement and exercise exclusive world
ownership, eligible placement, replacement capacity, and loss of the entire
Spot cohort. Azure eviction notice is best effort; abrupt termination must also
recover consistently. Measure Azure checkpoint latency and journal durability
under representative player traffic. The first regular worker provides a public
test target for this qualification rather than claiming those guarantees.

The silo identity `bytrcidp008` can read only `world-silo` in ACR and has a custom
`Puck World Store` role on its own blob container: container read/create plus
blob read/write, with no delete or role-management grant. CI retains its signing
key in Key Vault and injects it through protected VM extension settings into a private mounted file; the runtime needs no vault
permissions. CI temporarily admits its single runner IPv4 address to the vault
firewall; an `always()` step removes only the rule that run added. Secret
parameter files are removed and excluded from artifacts.
The image installs `libmsquic`, which .NET requires for Linux QUIC support.

Resource names and deployment settings come from `main.bicepparam`; the silo
publisher and readiness checks consume the resulting configuration outputs.
Pet names live in tags, following the
[Azure naming and module conventions](../src/Puck.Azure.Resources/README.md#naming-and-module-configuration).

Container Apps uses the current stable environment module with Log Analytics
for platform logs. Actors already exports OpenTelemetry logs, traces and metrics
directly through the Azure Monitor exporter with its managed identity. The
environment agent is not part of that path. Microsoft's
[2026 API change log](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/change-log/managedenvironments)
removes the preview environment-agent settings from the stable API. The
[managed agent documentation](https://learn.microsoft.com/en-us/azure/container-apps/opentelemetry-agents?tabs=azure-cli)
still describes the preview API and requires local authentication for its
Application Insights destination; our direct exporter uses Entra authentication.

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
