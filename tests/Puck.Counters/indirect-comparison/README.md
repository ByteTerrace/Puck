# Indirect comparison workload

This workload reuses the [G9 scenes](../../Puck.World.Canaries/indirect-near/README.md)
through the existing document basis. Its 1920×1080 output has two 960×1080
views, two named shadow-capable directional lights, native scale and temporal
reconstruction. The second view stays fixed while the first exercises one
input class. One eager local-seat body wears a red creation look, a 0.06 m
sphere resting on the study floor at (-0.12, 0, 0.15). Its zero-speed kit keeps
the pose still; its default indirect participation receives at Medium and
casts and receives at High. The existing zero-seat catch-all layout keeps the
two named cameras when the seat joins. The static study, furnace and sealed
room are unchanged. The [body-motion canary](../../Puck.World.Canaries/indirect-body-motion/README.md)
separately exercises movement. No alternate shader or model lives here.

`comparison.batch.json` declares medium and high groups. Each group runs five
input classes—completed cadence, changing daylight, orbit, fixed-origin pan
and a cut into the sealed room—through the existing `cache`, `screen` and
`cone` session selections. The method changes while the same cache remains
installed. Each observation restores the stationary authored inputs, pauses
simulation and waits for the actual current shared-cache geometry/lighting
source fence. It then resumes, applies its input and advances 120 ticks before
reading `body.where 0`, `world.looks`, `world.budget` and its one
`world.counters --json` response. A newer produced frame is required after
arming the fence wait; submitted flags or guessed warm ticks cannot satisfy it.
The fence proves shared-cache convergence, not each view's receiver admission.

Run with the current private CLI, serially under the GPU grant:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --batch tests/Puck.Counters/indirect-comparison/comparison.batch.json --output <reports>/indirect-comparison
```

The two tier groups launch Vulkan then Direct3D 12 each: four serial World
boots collect sixty backend observations and write thirty ordinary paired
reports. Every report retains its real fixture and observation script path.
The sidecar retains the manifest, prelude and script hashes, prior-observation
ordinal, method, tick and exact stdout/completion-verdict transcript lines.
These report identities belong to this shared-session batch; they are not
borrowed from the standalone controls. The complete collection phase has a
fifteen-minute cap and retains diagnostic transcripts on refusal.

Each reading preserves the existing counters' scope. GPU work describes each
node's newest completed submission; cumulative host counters include warm-up
and earlier observations in that tier's session. These raw values are not
subtracted into independent 120-tick costs, so comparisons keep the recorded
prelude and observation order as part of their input context.

Read every instance and detail row, including both views, both light slots,
histories, cache updates, apply work, unresolved work and allocated bytes.
For each sidecar entry, use its `Transcript` and one-based `ResponseLine` to
retain the preceding body, look and budget responses in that observation's
interval. There must be exactly one of each, after its 120-tick wait and before
its counter response. `body.where` must report active body 0 at the authored
spawn; `world.looks` must report one active `comparison-body` creation look.
Keep this block with its report's name, tier, method, ordinal and backend.

The ordinary paired report contains work counts; it does not copy allocation
bytes. The raw JSON counter response retains each graph node's `owned-bytes`,
including its histories and readback storage. The adjacent `world.budget`
response supplies each unique residency's active and retiring `cache-device`
and `cache-host` bytes, including pinned sources, regions and borrowed light
banks. Add those residency totals once to the graph-owned totals. The budget's
`hits`, `cells`, `state`, `proofs`, `irradiance`, `radiance`, `publication`,
`receiver-proofs`, region and `light-view` figures are included breakdowns;
do not add them again. Its `light-fragment` bytes already belong to the graph
node total. Null or unavailable bytes, an unallocated required cache, a missing
view, or an absent body census leaves the comparison unqualified. These reads
are adjacent console observations, not an atomic GPU frame or a peak-memory
measurement.

Missing or extra readings, a malformed response, any rejected command, an
unsettled source fence or backend mismatch refuses qualification. Only measured
passing paired reports may create the declared `.batch.ceilings.json` files.
Record and check the saved reports after inspecting the collection, without
launching the batch again. For the first observation:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --report <reports>/indirect-comparison/medium-cache-cadence.batch.report.json --ceilings tests/Puck.Counters/indirect-comparison/medium-cache-cadence.batch.ceilings.json --record
dotnet <cli-copy>/Puck.Cli.dll counters --report <reports>/indirect-comparison/medium-cache-cadence.batch.report.json --ceilings tests/Puck.Counters/indirect-comparison/medium-cache-cadence.batch.ceilings.json --check
```

Apply the same saved-report commands to all thirty declared report/ceilings
pairs. An initial batch `--check` refuses while any declared ceilings are missing;
a future batch `--check` collects fresh observations against the recorded set.
Once all declared observation ceilings exist, `puck gate --gpu` routes their
actual workload/script associations through this manifest once. Ambiguous
manifest associations and incomplete measured sets refuse. The parent
[counter guide](../README.md) owns backend/device scope and offline comparisons.

The ten `medium-<input>.script.txt` and `high-<input>.script.txt` files remain
standalone fresh-session cache controls. They explicitly select `cache`, use
the same paused fenced warm-up and keep their own script/report/ceilings
identities. They do not substitute for screen/cone or the shared-session batch.

The authored batch and its current-source wait remain unqualified. Actual
RTX 4070 rows, the observed body receive/cast workload, chosen tier defaults and
passing checks remain owed. The retained budget and counter responses account for every
cache, view, light map, traversal/constant ring and history byte; capacity
estimates do not satisfy that record. Only RTX 2060 hardware qualification may
remain as device debt after the local comparison closes.
