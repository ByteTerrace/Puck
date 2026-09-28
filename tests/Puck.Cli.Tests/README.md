# Puck.Cli.Tests

This xUnit suite tests [Puck.Cli](../../src/Puck.Cli/README.md), including
formatting, authored-content tooling, `.puck` source migration, canary
accounting, parity comparison,
process handling, branding asset synchronization, and MCP behavior. Individual fixtures define their inputs
and service or process setup; the suite is not a replacement for running a
hardware-dependent CLI operation in its intended environment.

`StartupBenchmarkTests` checks that incomplete or failed samples cannot produce
a corpus average, pending or unrelated captures cannot prove rendered readiness,
missing overlays fail rendered samples, and process output carries elapsed observation times.
`CliProcessHandshakeTests` checks that output can release a final stdin command
without closing input early, and that early exit and timeout remain bounded. Actual startup performance
requires running `puck bench startup` against the built World executable.

`ShippedSourceLintLawTests` runs `puck lint --strict` over every tracked source
under `worlds`, `src/Puck.World/Assets` and `tests/Puck.World.Verdicts`, so an
error or warning in a shipped world or cartridge fails the suite with the report
the verb printed. `FormatProjectionLawTests` holds every tracked source outside
`experimental` to what `puck format` prints.

`CompileBatchTests` verifies ordered compilation, independent source bindings,
byte parity with single-source compilation, stopping on failure, and refusal
of batch requests with a shared output path or watch mode.
`CompositionCompileCommandTests` verifies that one source can publish several
named world documents, that a refused member publishes none of them, and that
`--output` names their directory. It also covers explicit asset-lock updates,
the semantic-validation gate on an update, stale-byte refusal without output
replacement, and a document written away from its source naming its asset and
graph files from where it lands.

`WorldArtifactBuildLawTests` count builds of the stored `Puck.World` artifact
over small git checkouts of their own, with a counting builder in place of
`dotnet build`. Two resolutions of an unchanged tree build once, and concurrent
resolutions of one source state share one build. A one-byte uncommitted change
under the World's closure produces a new key, while documentation and
unreferenced projects do not. A publish that loses the race keeps the winner's
build. Pruning keeps the most recently used builds and every leased one, and a
killed run's leftover directories and lock files are removed only after six hours.
`WorldArtifactClosureLawTests` evaluates the World's project graph with MSBuild
and requires every input it names to lie under a keyed path.
`CanaryListenerLawTests` checks that the canary port probe hands out UDP ports.
It also checks that a World refusing its listener is classified as an
infrastructure failure rather than unsupported.

The release laws cover immutable deployment configuration references, exclusive
controller ownership, complete official package preparation, registry retention
readback, bootstrap refusal over existing gameplay, and cancellation of an owned
child process. `WorldReleaseRollbackTests` verifies rollback directly from an
admitted commit, refusal during unfinished maintenance, and finalization closing
eligibility. `WorldReleaseGuestGuardTests` checks the Python guest guard against
the C# durable group wire format, including stale operations and recovery roles.
It requires Python 3 on PATH (`python` on Windows, `python3` elsewhere).
`WorldReleaseAzureLeaseTests` uses the actual Azure
SDK against an isolated local Azurite container. Load
`mcr.microsoft.com/azure-storage/azurite:3.35.0` and start Docker to run that law;
it reports an asset-gated skip when the image or Docker is unavailable. It does
not contact a production storage account.

`WorldReleaseFixtureBuilderTests` covers bootstrap and captured exports, distinct
test keys, retained checkpoint bytes and machine identity, incomplete-export
refusal, metadata publication in both directions, and refusal of simulation edits
before Docker starts. It checks original receipt lookups and excludes later receipts
in materialized fixtures. Build the checkout's world-silo image as
`puck/world-silo:candidate`
(`docker build --file src/Puck.World.Silo/Dockerfile --tag puck/world-silo:candidate .`)
to run an unchanged-definition control and a metadata release pair, each
through four Docker qualification legs. The metadata control uses the public
`qualify` command, including retention of both packages, and verifies that the
source export survives unchanged. This optional same-image control verifies the
export and runner path; compatibility between builds needs separate pair evidence.
The outer test reads receipt history from container-written forward and rollback
stores and checks each image's receipt proof. `WorldReleaseReceiptProofTests`
covers complete lookups, duplicate and conflicting retries, unchanged roots, and
new receipts after continuation.
Those Docker legs and their hash evidence are retained for inspection in
`world-release-evidence` beside the test assembly, replaced by the next run.

`WorldReleasePackagedHostTests` uses that candidate image through the default silo
entry point and both silo/CLI MCP entry points. Each process must activate and
checkpoint a world containing both Gaming Brick engine types, serve the installed
Azure health response, expose MCP discovery only when configured, and drain with
a successful exit. This catches missing runtime extensions that the CLI exercise's
statically composed catalog cannot detect. No Azure account or OAuth issuer is
contacted; the test uses disposable local state and denies all MCP subjects.
A conflicting MCP listener must stop the worker with a failure exit code.

`WorldReleaseRollbackTests` also starts real CLI processes for invalid status and
exercise inputs, checking a concise diagnostic, failure exit code, and no stack
trace in the operator output.
Official package and bootstrap tests include unfilled boot draws and exact-byte
retry checks. The hosted composition control moves a nested machine document to
the common worlds directory and verifies its asset path still names the same
asset without embedding the build directory.

## Verification

From the repository root, run in PowerShell or another shell:

```powershell
dotnet test tests/Puck.Cli.Tests/Puck.Cli.Tests.csproj -c Release
```

A successful run reports passing tests and exits with code zero. Use the
framework's test filter to narrow an investigation. Keep the test project's
fixtures with the test when evaluating what a passing result establishes.
The [CLI reference](../../src/Puck.Cli/README.md) owns command syntax and
operational prerequisites.

## Documentation

📚 [Puck.Cli](../../src/Puck.Cli/README.md) · 🛠️ [Development](../../docs/development/README.md)
