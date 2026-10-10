# Puck.World.Silo.Tests

These laws check the silo and the extension model every host composes through:
the silo's routing sessions and deferred console answers, row lifecycle and
deadlines, crossing recovery across silo checkpoints, release groups, cutover
and rewind, and the World and the silo composing one extension installation
identically. The suite references `Puck.World.Silo`, the MCP and agent-harness
extensions, `Puck.World.Embeddings` and both Gaming Brick cores, and not the
desktop composition root or the graphics backends.

Its fixtures come from [`tests/Shared/World`](../Shared/World/README.md), with the
client vocabulary hooks and the Gaming Brick machine catalog.

`WorldReleaseRewindProbe.cs` contains one focused local lifecycle check through
actual hosted rows, release controls, and the directory store. It deploys
metadata, advances gameplay, rolls back while retaining the latest ticks, and
intentionally rewinds a later internal transfer. It interrupts restore after the
first durable world publication, resumes with admission closed, and compares
complete restored checkpoints. Disconnected players' documented parking is
checked explicitly, including reconnect; current operation receipts and fresh
writer generations survive. Restart and completed-operation resume must retain
subsequent progress. A rejected private rewind recovers its fresh drain state;
interrupting recovery before activation must preserve that choice when a new
coordinator resumes. Repeating a fully published admission still checks fences
and the group CAS, and completes after one pump boundary without registering
routes again. This checks the coordinator and storage behavior in one compiled
engine; packaged runtime and live Azure acceptance remain separate.

`WorldReleaseCutoverLawTests` delays the live export's root read while a later
mutation arrives, and checks that cancellation leaves the publication queue
usable. Archive laws cover receipt pins, interrupted uploads, canonical decoding
and a row missing its receipt pin. Silo lifecycle controls cover local-only rows
signing as their stable instance names through replacement and stale-writer
refusal; `puck artifacts test-world` runs `WorldSiloLifecycleLawTests` on Linux
from the Windows build.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.World.Silo.Tests/Puck.World.Silo.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
