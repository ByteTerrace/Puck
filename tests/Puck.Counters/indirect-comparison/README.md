# Indirect comparison workload

This workload reuses the [G9 scenes](../../Puck.World.Canaries/indirect-near/README.md)
through the existing document basis. Its 1920×1080 output has two 960×1080
views, two named shadow-capable directional lights, native scale and temporal
reconstruction. The second view stays fixed while the first exercises one
input class. No alternate shader or model lives in this data directory.

| Input class | Medium script | High script |
|---|---|---|
| Completed cadence | `medium-cadence.script.txt` | `high-cadence.script.txt` |
| Changing daylight | `medium-day.script.txt` | `high-day.script.txt` |
| Orbiting camera | `medium-orbit.script.txt` | `high-orbit.script.txt` |
| Fixed-origin pan | `medium-pan.script.txt` | `high-pan.script.txt` |
| Camera cut into the sealed room | `medium-cut.script.txt` | `high-cut.script.txt` |

Every script selects its tier explicitly, waits for engine readiness, warms the
same stationary scene for 600 ticks, changes its one input class and advances
120 ticks before the single `world.counters --json` read. Day and orbit change
their existing state-driven clock or camera binding each tick. The pan keeps
the initial orbit camera's origin; the cut changes origin and framing together.
Read every instance and detail row in the report, including both views, both
light slots, histories, cache updates, apply work and unresolved work.

Run one leg with the current private CLI, serially under the GPU grant:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --world tests/Puck.Counters/indirect-comparison/fixture.puck --script tests/Puck.Counters/indirect-comparison/high-pan.script.txt --output <reports>/indirect-high-pan.json
```

Apply the same world and script to each production comparison implementation:
the cache, screen-space indirect light over its probe fallback, and cone
occlusion with one diffuse bounce. The latter two mechanisms are not implemented
by these data files. Their actual supported selection and counted bounds must
come from the renderer owner before alternate scripts are authored.

Only measured reports may create ceilings. For example, after the actual current
RTX 4070 run succeeds and every required count is present:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --report <reports>/indirect-high-pan.json --ceilings tests/Puck.Counters/indirect-comparison/high-pan.ceilings.json --record
```

That ledger retains the real fixture and script identity; no placeholder ceilings
are checked in. Repeat the measured recording for the other input classes and
tiers. The parent [counter guide](../README.md) owns backend/device scope and
offline comparison rules.

These sources are not yet compiled or qualified. The G5 body receive/cast policy
is still required before this workload can claim medium receiving and high
casting: an unrendered local-seat body is not evidence. The production counter
surface must also account for every cache, view, light map, traversal/constant
ring and history byte. Capacity estimates in a plan do not satisfy that record.
Both comparison mechanisms, all actual RTX 4070 rows, chosen tier defaults and
their passing checks remain owed. Only RTX 2060 hardware qualification may remain
as device debt after the local comparison closes.
