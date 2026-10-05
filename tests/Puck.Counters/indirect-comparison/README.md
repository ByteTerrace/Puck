# Indirect comparison workload

This workload reuses the [G9 scenes](../../Puck.World.Canaries/indirect-near/README.md)
through the existing document basis. Its 1920×1080 output has two 960×1080
views, two named shadow-capable directional lights, native scale and temporal
reconstruction. The second view stays fixed while the first exercises one
input class. No alternate shader or model lives in this data directory.

`comparison.batch.json` declares medium and high groups. Each group runs five
input classes—completed cadence, changing daylight, orbit, fixed-origin pan
and a cut into the sealed room—through the existing `cache`, `screen` and
`cone` session selections. The method changes while the same cache remains
installed. Each observation restores the stationary authored inputs, pauses
simulation and waits for the actual current shared-cache geometry/lighting
source fence. It then resumes, applies its input and advances 120 ticks before
its one `world.counters --json` read. A newer produced frame is required after
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
RTX 4070 rows, chosen tier defaults, the G5 body receive/cast policy and passing
checks remain owed. The production counter surface must account for every
cache, view, light map, traversal/constant ring and history byte; capacity
estimates do not satisfy that record. Only RTX 2060 hardware qualification may
remain as device debt after the local comparison closes.
