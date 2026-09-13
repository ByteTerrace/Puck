# Puck.Cli.Tests

This xUnit suite tests [Puck.Cli](../../src/Puck.Cli/README.md), including
formatting, authored-content tooling, canary accounting, parity comparison,
process handling, branding asset synchronization, and MCP behavior. Individual fixtures define their inputs
and service or process setup; the suite is not a replacement for running a
hardware-dependent CLI operation in its intended environment.

The release laws cover immutable deployment configuration references, exclusive
controller ownership, complete official package preparation, registry retention
readback, bootstrap refusal over existing gameplay, and cancellation of an owned
child process. `WorldReleaseRollbackTests` verifies rollback directly from an
admitted commit, refusal during unfinished maintenance, and finalization closing
eligibility. `WorldReleaseGuestGuardTests` checks the Python guest guard against
the C# durable group wire format, including stale operations and recovery roles.
It requires Python 3 on PATH, or `PUCK_TEST_PYTHON` naming the executable.
`WorldReleaseAzureLeaseTests` uses the actual Azure
SDK against an isolated local Azurite container. Load
`mcr.microsoft.com/azure-storage/azurite:3.35.0` and start Docker to run that law;
it reports an asset-gated skip when the image or Docker is unavailable. It does
not contact a production storage account.

`WorldReleaseFixtureBuilderTests` covers bootstrap and captured exports, distinct
test keys, retained checkpoint bytes and machine identity, incomplete-export
refusal, metadata publication in both directions, and refusal of simulation edits
before Docker starts. It checks original receipt lookups and excludes later receipts
in materialized fixtures. Set `PUCK_TEST_WORLD_IMAGE` to a locally built world-silo
image to run an unchanged-definition control and a metadata release pair, each
through four Docker qualification legs. The metadata control uses the public
`qualify` command, including retention of both packages, and verifies that the
source export survives unchanged. This optional same-image control verifies the
export and runner path; compatibility between builds needs separate pair evidence.
The outer test reads receipt history from container-written forward and rollback
stores and checks each image's version-2 receipt proof. `WorldReleaseReceiptProofTests`
covers complete lookups, duplicate and conflicting retries, unchanged roots, and
new receipts after continuation. Set `PUCK_TEST_PREVIOUS_WORLD_IMAGE` to a distinct
older image with version-1 exercise reports to verify its named refusal without a
qualification receipt; `PUCK_TEST_WORLD_IMAGE` must also be set for this control.
Set `PUCK_TEST_RELEASE_EVIDENCE_DIRECTORY` to retain those Docker legs and their
hash evidence outside the test's temporary directory for inspection.

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
