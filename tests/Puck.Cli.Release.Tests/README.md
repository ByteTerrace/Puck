# Puck.Cli.Release.Tests

These laws hold the release and automation verbs. They cover exclusive controller ownership, complete official package
preparation, bootstrap refusal over existing gameplay, and cancellation of an owned child process.
`WorldReleaseRollbackTests` verifies rollback directly from an admitted commit, refusal during unfinished maintenance,
and finalization closing eligibility. `WorldReleaseAzureLeaseTests` uses the actual Azure SDK against an isolated local
Azurite container. Load the `mcr.microsoft.com/azure-storage/azurite` image at the tag the test's `Image` constant pins
and start Docker to run that law; it reports an asset-gated skip when the image or Docker is unavailable. It does not
contact a production storage account.

`WorldReleaseFixtureBuilderTests` covers bootstrap and captured exports, distinct test keys, retained checkpoint bytes
and machine identity, incomplete-export refusal, metadata publication in both directions, and refusal of simulation
edits before any leg starts. It checks original receipt lookups and excludes later receipts in materialized fixtures.
Its qualify verdicts run over scripted engines (`IWorldReleaseQualificationEngines`) that resolve an install and answer
each leg without starting a process. `WorldReleaseReceiptProofTests` covers complete lookups, duplicate and conflicting
retries, unchanged roots, and new receipts after continuation. An installed host release's own legs are exercised by
CI's `host-release` job (`puck azure test-world-host` and `puck world release rehearse --install`), not by this suite.
Official package and bootstrap tests include unfilled boot draws and exact-byte retry checks.
Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Release.Tests/Puck.Cli.Release.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

The official tree's root is now the debug world.

## Documentation

📚 [Puck.Cli.Release](../../src/Puck.Cli.Release/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
