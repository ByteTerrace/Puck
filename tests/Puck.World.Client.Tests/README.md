# Puck.World.Client.Tests

These laws check the presentation-facing client seam on its own, with no server:
the state mirror and the reads presentation takes through it, seat bindings and
views, the overlay and HUD frame, creations as the stamp pool emits them, the
shadow slot allocator, picking, and the view graph's instances and pipelines.
The suite references `Puck.World.Client` alone, whose closure carries the SDF VM
and overlays, so a change to the authoritative simulation never selects it.
Laws that run a client against a live server or a compiled world are in
[`Puck.World.Presentation.Tests`](../Puck.World.Presentation.Tests/README.md).

Its fixtures come from [`tests/Shared/World`](../Shared/World/README.md), with the
client vocabulary hooks the composition roots install.

`ShadowSlotLawTests` exercises named light selection, holder-first ties followed
by authored order, stable slots, atomic instant handoffs and discontinuity
resets without a GPU. `ShadowFadeLawTests` covers bounded current and prior
handoffs, progress derived only from the presented tick, and queue recomputation
for busy slots, identities and fade capacity, including oldest-first service
ahead of fresh crossings. Its outgoing-identity law holds a waiting crossing
until the active handoff releases that identity, then starts it at the first
delivered tick the blocker clears. These classes also cover instant atomic
overlap, replay and frozen-tick agreement, and allocation-free steady reads.
`ShadowGpuFrameLawTests` holds the full frame slot table and active GPU controls
to those allocator outputs. Shader and device laws and the `shadow-slots` canary
cover the GPU handoff separately; these laws open no device. `ShadowFrameLawTests`
exercises complete delivered samples, skipped render frames, state-only
replacements, reordered light tables and the slot report. Boot delivery laws send
the client's actual definition revision through an install and completed
snapshot; default-policy coverage pins the sun in slot 0.
`SessionShadowDeliveryLawTests` checks that session observers see every complete
delivery, field cells included, while keeping counted observer samples separate
from frame samples.

`WorldCostLawTests` reads scoped and per-shape ownership from a live composed
program. `OverlayReservationRefusalLawTests` runs the actual composer with an
oversized writer: the whole run is refused, later content fits, and the narration
names the writer once per episode. `OverlayLeaseTableFitsBackstopsLawTests` holds
the shared text backing to the power-of-two sum of all declared reservations.
`OverlayPackageLawTests` counts its installed host/device regions and CPU
scratch/shadow payloads.

`WorldComposedPickMapLawTests` carries material names through the real SDF frame
composition: two emitters, global SDF ordinals, rebased mesh draws, and a
replaced identity map that cannot rename an earlier captured answer.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.World.Client.Tests/Puck.World.Client.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
