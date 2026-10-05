# Sky and shadow default counts

The batch reuses the dense [still-sky scene](../sky-still.puck), its fixed camera
and 1920×1080 output. Every preset keeps its view scale at the existing floor
value, so changing the sky field scale does not change primary hit samples.
The added floor and two pillars reuse the [shadow-slot court](../../Puck.World.Canaries/shadow-slots/fixture.world.json).
Both named suns remain installed throughout the binary-shadow group; its west
sun rotates on the existing state-backed timeline route.

`comparison.batch.json` declares four ordered groups:

| Group | Observations | Independent inputs |
|---|---:|---|
| Low sky | 10 | Still, drift, twinkle, keyed and isolated clouds, each at field scale 1 and 0.5 |
| Medium sky | 10 | The same inputs and scales at the middle sky form |
| High sky | 10 | The same inputs and scales at the fullest sky form |
| Binary shadow | 6 | Shadow slots 0, 1 and 2, each with amortization off and on |

The three changing sky classes contribute eighteen settings; isolated clouds
contribute six and binary shadows six. Six matched still settings provide the
comparison controls. Field scale changes only the independent sky field grid;
`world.render-scale` remains fixed. Low intentionally removes twinkle work, while
its cloud form is the existing reduced form. The shadow group uses field scale
one and temporal reconstruction, zero fade slots/ticks and instant overflow.
It changes no receiver or caster geometry between observations.

Each observation selects the existing cache method but disables indirect work,
pauses through its own `world.wait ready` engine verdict, resumes for 120 active
ticks and reads JSON once. An engine verdict proves installed pipelines and
completed initial frames, not an indirect-source fence. The manifest's `engine`
completion keeps those meanings separate. Its preludes contain no observation
wait. The scripts reset their state-backed input before each reading, and keep
their own script, ordinal, field-scale command and completion transcript identity.

Run once, serially, with the current private candidate CLI under the GPU grant:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --batch tests/Puck.Counters/sky-defaults/comparison.batch.json --output <reports>/sky-defaults
```

Four groups launch Vulkan and Direct3D 12 each: eight serial boots collect
seventy-two actual backend observations and thirty-six ordinary paired reports,
within the collector's one fifteen-minute phase cap. These are raw shared-session
observations: GPU rows describe each node's newest completed submission, while
cumulative host rows include the prelude and earlier observations. They are not
subtracted into isolated 120-tick costs. Saved reports retain that context.

Inspect every relevant pass and detail: `gpu.sky.evaluations`, `gpu.sky.hashes`,
`gpu.sky.texture-loads`, `gpu.march.steps`, dispatches, field/composite texels,
allocated bytes, and `gpu.shadow.slot0.steps` through `slot5.steps`. The sky rows
must retain their actual named air/sun/stars/clouds and field-run identities; the
cloud-only case must not acquire those removed layers' work. Required zero values
must be present records, not an absent row interpreted as zero. The unused shadow
slots must report zero, and K=0 must not acquire a shadow march. Read the newest
completed-work scope honestly: a retained older node submission is not a claim
that the current scheduled frame executed that work. Actual current field extents,
finite values and the corresponding image evidence are also qualification duties.

The batch runs on Vulkan and Direct3D 12 on the RTX 4070. Its thirty-six ceilings
files record actual paired observations, including measured zero values; every
saved report holds its recorded ceilings. Image quality and the resulting tier
choices remain open. Record and check saved reports against their declared
ceilings without launching the batch again. For the first observation:

```text
dotnet <cli-copy>/Puck.Cli.dll counters --report <reports>/sky-defaults/low-still-full.report.json --ceilings tests/Puck.Counters/sky-defaults/low-still-full.ceilings.json --record
dotnet <cli-copy>/Puck.Cli.dll counters --report <reports>/sky-defaults/low-still-full.report.json --ceilings tests/Puck.Counters/sky-defaults/low-still-full.ceilings.json --check
```

Apply the same saved-report commands to all thirty-six declared report/ceilings
pairs. An initial batch `--check` refuses while any declared ceilings are missing;
a future batch `--check` collects fresh observations against the recorded set.
Keep this phase separate from the
[G10 indirect comparison](../indirect-comparison/README.md), whose warm-up proves
current shared-cache fences. Final tier decisions must close locally; only RTX
2060 hardware qualification may remain as device debt.
