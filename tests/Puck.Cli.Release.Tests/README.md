# Puck.Cli.Release.Tests

These laws hold the release and automation verbs. They cover immutable deployment configuration references, exclusive
controller ownership, complete official package preparation, registry retention readback, bootstrap refusal over
existing gameplay, and cancellation of an owned child process. `WorldReleaseRollbackTests` verifies rollback directly
from an admitted commit, refusal during unfinished maintenance, and finalization closing eligibility.
`WorldReleaseGuestGuardTests` checks the Python guest guard against the C# durable group wire format, including stale
operations and recovery roles. It requires Python 3 on PATH (`python` on Windows, `python3` elsewhere).
`WorldReleaseAzureLeaseTests` uses the actual Azure SDK against an isolated local Azurite container. Load
`mcr.microsoft.com/azure-storage/azurite:3.35.0` and start Docker to run that law; it reports an asset-gated skip when
the image or Docker is unavailable. It does not contact a production storage account.

`WorldReleaseFixtureBuilderTests` covers bootstrap and captured exports, distinct test keys, retained checkpoint bytes
and machine identity, incomplete-export refusal, metadata publication in both directions, and refusal of simulation
edits before Docker starts. It checks original receipt lookups and excludes later receipts in materialized fixtures.
Build the checkout's world-silo image as `puck/world-silo:candidate`
(`docker build --file src/Puck.World.Silo/Dockerfile --tag puck/world-silo:candidate .`) to run an
unchanged-definition control and a metadata release pair, each through four Docker qualification legs. The metadata
control uses the public `qualify` command, including retention of both packages, and verifies that the source export
survives unchanged. This optional same-image control verifies the export and runner path; compatibility between builds
needs separate pair evidence. The outer test reads receipt history from container-written forward and rollback stores
and checks each image's receipt proof. `WorldReleaseReceiptProofTests` covers complete lookups, duplicate and
conflicting retries, unchanged roots, and new receipts after continuation. Those Docker legs and their hash evidence
are retained for inspection in `world-release-evidence` beside the test assembly, replaced by the next run.

`WorldReleasePackagedHostTests` uses that candidate image through the default silo entry point and both silo/CLI MCP
entry points. Each process must activate and checkpoint a world containing both Gaming Brick engine types, serve the
installed Azure health response, expose MCP discovery only when configured, and drain with a successful exit. This
catches missing runtime extensions that the CLI exercise's statically composed catalog cannot detect. No Azure account
or OAuth issuer is contacted; the test uses disposable local state and denies all MCP subjects. A conflicting MCP
listener must stop the worker with a failure exit code. Official package and bootstrap tests include unfilled boot
draws and exact-byte retry checks. The hosted composition control moves a nested machine document to the common worlds
directory and verifies its asset path still names the same asset without embedding the build directory.

`AzureHostTelemetryShutdownLawTests` reads each Azure host's built `runtimeconfig.json`, so the suite builds
`Puck.Actors` and `Puck.Azure.Functions` first; nothing here links against them.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Release.Tests/Puck.Cli.Release.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

`tests/Puck.Cli.Release.Tests/Official/OfficialBuildCommandTests.cs` builds a real official tree; run it alone with
`--filter-class "*OfficialBuildCommandTests"`.

## Documentation

📚 [Puck.Cli.Release](../../src/Puck.Cli.Release/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
