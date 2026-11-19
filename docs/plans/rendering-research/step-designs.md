# Rendering research: cross-cutting reference

Every decision's and bet's steps, checks and options from [the rendering plan](../rendering-research.md) have been folded into the plans that own them. This page keeps what belongs to no single owning plan: where each decision now lives, the table of missing measurements, the properties the order rests on, where each look is authored, what would change the answer, research still owed, existing systems extended, outside techniques not taken, duplicated work across areas, and the round's sources.

| Decision or bet | Owning plan(s) |
|---|---|
| 1. Measure beside the work | [The benchmark](../benchmark.md#comparing-runs) and [the job system plan](../engine-job-system.md#frame-thread-measurements-owed) |
| 2. Capture and prepare only what a frame shows | [Streaming and memory](../streaming-and-memory.md#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render), [the job system plan](../engine-job-system.md#6-build-order) and [disclosure](../disclosure.md#presentation-and-derived-product-disclosure) |
| 3. Shorten the tick, and settle where it runs | [The job system plan](../engine-job-system.md#6-build-order) |
| 4. Background GPU work | [Illumination](../illumination.md#background-gpu-work-and-headroom) and [bake proportional to change](../open-items.md#bake-proportional-to-change) |
| 5. Convert scene light once | [Rendering](../rendering.md#colour-conversion--scene-light-to-display-values-once) and [the terrain pass](../terrain-pass.md#order-of-work) |
| 6. One shadow answer | [Illumination](../illumination.md#the-build-order) and [transmission](../transmission.md#implementation-steps) |
| 7. A lighting change costs what changed | [Illumination](../illumination.md#the-build-order) |
| 8. One error budget along one chain of meshes | [Virtual geometry](../virtual-geometry.md#the-build-order) and [bake detail bounded by demand](../bake-demand-tiers.md#steps-and-checks) |
| 9. Give memory back, and never show a hole | [Streaming and memory](../streaming-and-memory.md#step-5-the-floor-and-eviction) |
| 10. A bake costs what changed, and contour defects | [Bake proportional to change](../open-items.md#bake-proportional-to-change), [bake detail](../open-items.md#bake-detail) and [visibility](../visibility.md#the-build-order) |
| 11. One instance table, tested once per pass | [Visibility](../visibility.md#the-build-order) and [virtual geometry](../virtual-geometry.md#the-build-order) |
| 12. Motion and placement as authored programs | [Dense landscapes](../dense-landscapes.md#motion-and-placement-at-zone-scale), [the terrain pass](../terrain-pass.md#what-the-bake-needs) and [visibility](../visibility.md#the-build-order) |
| 13. One shading path, and a smaller image history | [Author shaders](../author-shaders.md#the-decision-matrix) |
| 14. A nested view at another size | [Streaming and memory](../streaming-and-memory.md#nested-views-fitted-windows-authored-quality-and-standing-feeds), [transmission](../transmission.md#implementation-steps) and [water](../water.md#eye-inside-a-medium) |
| 15. Presentation | [The benchmark](../benchmark.md#presentation-timing-and-admission-stage-8), [the UI overhaul](../ui.md), [machines and cartridges](../machines-and-cartridges.md#open-items) and [illumination](../illumination.md#the-build-order) |
| Bet 1: Still views on client records | [Runtime and delivery](../runtime-and-delivery.md#the-presentation-view) |
| Bet 2: Portal crossing prepared ahead | [Streaming and memory](../streaming-and-memory.md#the-seam-walk) |
| Bet 3: Merged worlds and shared-part crowds | [The valley and the moons](../valley-and-moons.md#phases) and [visibility](../visibility.md#v5-multi-view-and-shadow-work) |
| Bet 4: Shared products by meaning | [Disclosure](../disclosure.md#presentation-and-derived-product-disclosure), [bake proportional to change](../open-items.md#bake-proportional-to-change) and [derived products](../../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth) |
| Bet 5: Cold program shows surface quickly | [Streaming and memory](../streaming-and-memory.md#step-5-the-floor-and-eviction) |
| Bet 6: Light carried across an opening | [Illumination](../illumination.md#the-gaps-the-round-named) and [disclosure](../disclosure.md#presentation-and-derived-product-disclosure) |
| Bet 7: Tracing light with the field | [Illumination](../illumination.md#after-the-gpu-trace) |

## Missing measurements

An instrument is built by the lane that needs it first; a reading ranks or
sizes the work that uses it and never holds a lane that can show its own
result. Readings start on the RTX 4070 at 2560 by 1440 on both backends and in
both topologies; RTX 2060 and Steam Deck rows wait until the RTX 4070 meets
both bars.

| Missing measurement | Why it matters | Where it is built | Read on, and what uses it |
|---|---|---|---|
| Produce split by part: pumping, capture, packing, staging the lighting, exposure, scheduling, preparing packages, recording and flushing | At the bar's size produce takes about 26 ms and the tick 7 to 10 ms, and the main view's GPU passes take about 25 ms. Recording sums to about 1 ms. So most of produce is unexplained, and nothing says how much of it is the frame thread waiting for the GPU. The split keeps waiting apart from work | The job system's work packet, following the pattern of `RenderRecordWork` | The RTX 4070, both backends. Built by the first wave's `frame` lane. It ranks the capture order's smaller cuts and holds none of them |
| The tick split by phase, per chapter, and by world instance | Decision 3 shortens the tick by splitting two of its phases. Open items records the debug world's tick as about a third body movement and under a third rule evaluation, with serial causes named. Every world instance also steps in series on the frame thread (`WorldInstanceHost.StepInstances`). No report says which of these dominates | The job system's work packet | The RTX 4070. Built by `frame`. It feeds the design of where the tick runs |
| Delivery time and bytes for each session | A world nobody sees still steps and still delivers to its sessions. Decision 2 saves presentation work only, and this row sizes what it leaves | The worlds area ([open items](../open-items.md#transfers-federation-and-server)) | The RTX 4070, with the authority remote. It needs `bench`'s remote route driver, and it sets the budget's delivery row |
| For each attempt to present a frame: whether it was submitted, what happened to it (shown, skipped, dropped or unknown), how long it stood on the display, and which signal said so. The report states the share of attempts whose outcome is unknown | `host.frames` reports the loop's interval, not the display's. Every presenter already reports its latest confirmed present (`IPresentTimingFeedback`). On Direct3D 12 that is the scan-out time. On Vulkan it is the CPU's clock when a wait for the present from two frames back returned (`VulkanFramePresenter.RecordPresentTiming`), and a skipped present is counted but never placed on the timeline. No record is kept per present, so nothing says which frame was shown or dropped | The benchmark's displayed-time stage (stage 8), extending `PresentTimingSample`. A host that cannot report display time says so as a capability, and the column is left out | The RTX 4070, both backends. Used by any check that claims what was displayed. The other devices when they are on hand |
| From the earliest input a frame used to the time that frame was displayed, or submitted where no display time exists. Motion the tick integrates and pointer look applied between ticks (`WorldSeatViewState.PresentedLook`) are reported apart | Two decisions turn on latency and nothing measures it: whether the tick ever moves to its own thread (decision 3), and whether waiting for a present slot before rendering costs or saves latency (decision 15) | One joined column of the present record | The RTX 4070, both backends. Used by the design of where the tick runs and by decision 15's admission rule |
| Captures and prepares during produce, counted by what asked for each: a scheduled render, an upload that is owed, another consumer's minimum, or the question "has this view changed?" | Decision 2 saves the captures that nothing reads. That question triggers a prepare too, so it has to be counted | The job system's work packet | The RTX 4070. Used by the capture order |
| GPU time per benchmark chapter, split into the root view, nested views and background work. Over the same span: the background work's GPU time, the time some job was in flight, the elapsed time, and the credit the budget granted and held | Decision 4 raises the background work's use of its allowance. The only figure today divides GPU time by the time a job was in flight, which is not a share of the allowance, so nobody knows how much of the allowance is used | `world.gpu-timing`, `world.counters gpu` | The RTX 4070, both backends. Used by the background allowance (decision 4) |
| How long each frame's own GPU work took from submit to completion, per frame, with any background job that ran ahead of it | Pass timings today are means over a window of 32 frames (`ShaderPipelineRenderNode.TimingWindow`), so no per-frame GPU maximum exists. The headroom rule and its check need one | `world.gpu-timing` | The RTX 4070, both backends. Built by the lane that builds the background allowance, which needs it first |
| One cold bake that records worker activity, the wait on each GPU request, GPU timestamps and the total time together, on a quiet machine with the display composing and an empty store | It shows which of decision 10's two speed fixes is on the critical path | The bake's work packet | The RTX 4070, both backends. Taken after the bake is keyed by stage, because that changes the bake's cost. It ranks decision 10's two speed fixes |
| A cold arrival in play: a windowed walk into a place whose meshes are not stored | The cold-bake figure is read paused and in lockstep, where the service hands each job to the GPU before it waits (`SdfGpuWork.Drive`). In play the service only pumps (`SdfGpuWork.Pump`), which is the slow path decision 4 describes. The figure never exercises it | A benchmark chapter run from an empty store | The RTX 4070, both backends. Used by the second and fifth bets |
| The time from saving an edit to seeing it in the running debug world, by phase, for three kinds of edit: a `.puck` document, a shader, and C#. Beside each time, the work it did: groups rebaked, kernels recompiled, projects rebuilt | Decision 10 is judged by this, and it is the wait the owner's ruling puts first. Only scattered readings exist | The edit-to-running procedure's phases ([contributing](../../development/contributing.md#edit-to-running), [open items](../open-items.md#edit-to-running-hot-reload-and-boot)) | The RTX 4070. It judges the bake's keys by stage |
| Shadow queries the relight actually issued, and pairs of hit and light it actually tested, separate from the scheduler's predicted cost. Beside them, how many distinct pairs of surface and light those were, and the most work in one dispatch | Decision 7 sizes its stores by executed work. Traffic alone does not size a store: one pair asked a thousand times needs one entry, and a thousand pairs asked once need a thousand | `world.counters gpu` | The RTX 4070. Used by shared surface lighting (decision 7) |
| For each probe level, at a walk, a run and in flight: probe cells entering the grid, and probes traced and relit a second | The lighting's lag has only been read standing still. A moving camera brings new cells in, and whether tracing keeps up decides whether the lag check means anything in play | `world.counters gpu` | The RTX 4070. Used by the background allowance's lighting check |
| Triangles drawn per level, and per tier relative to the tier that was asked for. Bytes held by each level that no pass selected | Decision 8's reasoning about unused detail has no counter behind it | `world.counters gpu`, `world.residency` | The RTX 4070. Built by the first wave's `floor` lane, which needs it first. Used by the chain of meshes |
| Triangles submitted, culled and rasterized in each raster pass | Indexed draws claim to cut vertex work, and no counter shows vertex work | `world.counters gpu` | The RTX 4070. Used by the indexed-draw experiment. The other devices when they are on hand |
| How much of each cascade's grid of receiver cells is occupied | It decides whether shadow storage could be allocated on demand, and it sizes decision 11's caster culling. Only the far cascade has a receiver mark today, so the measurement builds a count-only mark for the other two | The virtual geometry plan's shadow measurement (D8) | The RTX 4070. Used by caster culling |
| For each pass, one node per residency and one per view of it, naming the residency each view reads | Decisions 2, 11 and 14 depend on it. Sessions and residencies do not map one to one in either direction. The boot world's sessions are views of its one residency, and every seat and window presented in one other world share that world's one residency (`WorldFramePresenter.AttachWindow`) | The visibility plan's owed measurements | The RTX 4070. Used by the capture order |
| The capture and GPU readings again, with one window onto a world that gives the viewer less than a full replica | Every world of the debug world admits everyone at full disclosure (`disclosure: Replica` in `worlds/debug/debug.puck`), so every window joins a shared residency. A restricted viewer gets a residency of its own (`WorldScreenBinder.CreateSessionResidency`). Without this row every sharing claim is measured only where sharing is cheapest | A scratch destination admitted below a replica, in the same process | The RTX 4070. Used by any check that claims work is shared between viewers |
| What an empty indirect draw command costs on each bar device | It decides whether compacting the command list is enough on its own | Visibility's compaction step (V1a) | The RTX 4070. Used by compaction. The other devices when they are on hand |
| One cascade pass split into walking the commands, vertex work, pixel fill and clears | It decides which of three fixes pays | A GPU capture in the lead's measurement window | The RTX 4070. Used by caster culling |
| What re-uploads about 2,600 draw records a frame when nothing is moving | It may be a defect that is cheaper to fix than any step that claims the same saving | Visibility | The RTX 4070. Used before the draw record is split |
| Produce, GPU work and counted work, read apart for three kinds of frame: a settled view whose inputs are stable, a continuous interval of moving sun and turning camera, and the measured window in which a player crosses a portal and two worlds each fill much of the screen. Captures are counted by residency and by cause, so a root capture owed to scheduling, a screen's refresh or settling light is told apart from a capture caused by change | A renderer whose work follows change has a different bottleneck in each. A stop alone is not a still frame: the clock, a machine's display and the lighting's jobs can all advance there. The frame bar is read on the slowest frame, which a saving on still frames does not move ([the four principles](../rendering-research.md#the-four-principles)) | The benchmark's route: a settled stop, a sun-and-turn span and a painting crossing, as named spans of its chapters | The RTX 4070, both backends. Used by the first bet |
| Garbage collections and bytes allocated per iteration, by source | One run had 47 collections that paused the process for 674 ms in 600 iterations | The job system's work packet | The RTX 4070. The `vkalloc` lane is reading Vulkan's, and the sources both backends share follow it |
| The mesh audit over every stored mesh of the world, at the tier each group is stored at, on each backend | Decision 10's contour rule reaches every bake, and the audit that would judge it does not cover the world today. A store audit over a cold store read 238 of 6,544 groups, and the CPU audit disagrees with the device below cells of about 8 mm ([bake detail](../open-items.md#bake-detail)) | `puck mesh audit` | The RTX 4070, both backends. Used by the contour rule. Groups the audit cannot judge (glyphs and sampled regions, which the CPU evaluator leaves out) and groups it did not sample are counted apart from the groups that passed |
| Any pass at all on the RTX 2060 and the Steam Deck, on every backend the device runs: Direct3D 12 and Vulkan on the two RTX cards, Vulkan on the Deck | Four plans wait on it: visibility's device measurement (M2), the author-shader matrix, illumination's first step and transmission's budgets | One GPU session that serves all four plans | The RTX 2060 and the Steam Deck, after the RTX 4070 meets both bars |
| For each bar device: the slowest frame of each chapter against the device's bar, split into the tick, produce, the root view, the cascades, nested views and background work | It says how far each device is from its bar and in which part, so that each later step can state its gain against that distance | The same device session | The RTX 4070 now, on its two backends and in both topologies; the first reading is in the plan's budget table. The other devices after the RTX 4070 meets both bars. Until then each device's authored table is run on the RTX 4070 as an estimate of its counted work |
| The benchmark's route driven against a remote authority, with a witness that the seat's body arrived in the remote world. The report gives the endpoint the route played against, what the local process simulated, what delivery cost and what rendering cost, each apart | The frame bar is read in both topologies, and the route driver reads the seat's body, its places and the navigation mesh from the server of the world the seat is in (`WorldSeatDriver`). A remote world has no server on the client. A run started with `--connect` still steps the boot world and transfers the body, so it reads the wrong topology | The first wave's `bench` lane: a driver that reads what the seat's client was delivered | The RTX 4070, both backends. A run that still observes the boot world does not count |
| What one more world stepped, and one more nested view in frame, cost the frame thread | Every world instance steps in series on the frame thread, and the budget's tick and nested-view rows assume today's few worlds and one view. The design of where the tick runs starts from this number | `frame`: a scratch world with one more instance, and one with one more nested view | The RTX 4070 |
| The time from receiving each creation's program to its first usable mesh, with timeouts and failures counted and the slowest reported. A time to a floor is recorded as unavailable until a qualified floor exists | The fifth bet ends on it, and a median alone passes with nearly half the creations missing | A benchmark chapter run from an empty store | The RTX 4070, both backends. Used by the fifth bet |

## Properties the order rests on

Each row is a property that the build order depends on, the one place that
enforces it, and where that guarantee stops. Most are not enforced yet, and
the row says so. The step that builds the mechanism builds the enforcement
with it.

| The property | What enforces it | Where the guarantee stops |
|---|---|---|
| Scene light is converted exactly once before anything reads it as a working image (decision 5) | Not enforced in the current tree. To build: every port of a render graph declares which of three kinds of image it holds, and composition refuses a mismatch. The conversion sits in the producing view's graph | It does not cover the values a kernel writes inside a declared kind. The card and patches through each path and each display output cover those |
| An authored colour is decoded exactly once on its way into shading, and a display colour never is (decision 5) | Not enforced in the current tree: one grammar is parsed at 22 places. To build: a scene colour and a display colour are two types, and only a scene colour can be packed for shading | A site that parses a colour into the wrong type. The list of parse sites and one swatch for each kind of colour cover that |
| Every cascade texel a receiver can read, for as long as that shadow map is shown, is marked, or else the cascade culls nothing (decision 11) | Today the far cascade's mark covers its one kind of reader in the frame it is built. To build: the marking uses one geometric bound, the authored maximum radius, and the receiver cannot sample outside it. A mask records its layer, its projection and the region it is valid for, and a receiver outside that region takes the probes' answer. A cascade with no mark keeps every caster | A kind of read added later without widening the bound. The culling comparison in motion covers that |
| A lit, admitted light holds the primary cascade role (decision 6) | To build: one resolver assigns roles, and `SdfMeshShadowCascades.Fit` reads roles and not slot 0 | The fade between roles. It is authored, and the walk through the exchange and the crossing check it |
| A view's last picture stands only if nothing it shows has changed (decision 2) | Today the packed hash, taken after the capture, and the rule that a standing view must still be bound to the same resolved view (`SdfWorldResidency.IsUnchanged`, `SdfWorldPasses.Refresh`). Both stay. If the shortcut of decision 2's last step is built: an input with no change record makes the view capture, and an audit compares each skip with the full decision, the packed hash and the residency's refusals together, counting views eligible, skipped and audited. Nothing forces a new input to add a record, so the shortcut stays under adversarial review ([hazards](../../development/hazards.md#silent-invariants)) | An input wrongly taken to be covered by a change record, where the audit is not running. Disclosure at the bind is not enforced today (decision 2, step 11) |
| Every residency does its upkeep every frame, captured or not (decision 2) | To build: upkeep is its own step, run for every residency before any is chosen for capture | Upkeep that is later added inside the capture. The checks on an unseen residency's memory and shader reload cover that |
| A reused published result was derived only from rows its consumer is delivered (decision 7's shared pool, decision 14's standing feeds) | Not enforced in the current tree. To build: the reuse key is built from the content identities of the delivered inputs a result read, so no reuse can be written without them. The same test runs when an image is bound to a reader and when a finished job installs its result. Until a check covers it, this stays under adversarial review ([hazards](../../development/hazards.md#silent-invariants)) | What the authority chooses to deliver, which belongs to [disclosure](../disclosure.md). Pixels already delivered |
| A group within reach always has a surface to draw, and a place whose floors do not exist yet is not published with holes (decision 9) | The streaming plan's floor, built by its decision 5. To build: the frame keeps the last complete scene, or a window's authored fallback, until the new place's floors and lighting stand | A floor that fails its acceptance test. Such a group keeps a finer tier |
| A frame whose lighting has converged shows light that is a function of its scene, its lighting and where its probe grid sits (decisions 4 and 7) | A frame the graph withholds drives its own background work to completion and does not pump it under budgets (`SdfWorldResidency.FrameWithheld`). The headroom rule and the per-second grant must stay out of that path. To build: every probe of a batch reads the bounce from before the batch | Ordinary presented play, where budgets apply and the lighting's age varies |
| A query that replaces shared probe state runs alone, and a batch's commit copies only the rows that batch wrote (decision 4) | The single point of admission (`SdfMeshProbeGpuWork.TryTake`) | A second way to admit a query. Review covers that until a type does |
| No background job runs longer than one frame's allowance, and no dispatch does more work than its cap (decision 4) | Today only the probe client sizes its jobs. To build: every client sizes a job through one rate type (`SdfGpuWorkRate`) against the live allowance, and a kernel counts its own work against a cap it is given and resumes later | A job whose cost has not been measured yet. The service's count of jobs over the allowance covers that |
| A background job handed to the GPU early keeps its inputs alive until it finishes (decision 4) | Not built. To build: one place hands a job to the queue and takes the holds on its inputs, and a job cannot be submitted another way. Adversarial review ([hazards](../../development/hazards.md#silent-invariants)) | A lost device |
| A position one GPU job wrote is bounded before another job reads at it (decision 4) | Not built. To build: the reading kernel clamps to a length it is given as an input. Adversarial review | Nothing beyond the clamp: a wrong position inside the bounds reads the wrong probe, which the drain comparison covers |
| The rule that finds a surface crossing has one definition (decision 4) | Today the rule exists on the CPU only. To build, with the GPU sampling: both evaluators derived from one source | Floating-point differences between the evaluators. The bracket and residual checks measure those |
| Size on screen has one definition (decision 8) | Not enforced: four places compute it. To build: one function in one home, called by all four, with the scale passed to shaders as data | Rounding differences between the host and a shader's use of the scale |
| A rung is drawn by choice only when its recorded deviation, measured over the authored group's own bounds, fits the consumer's budget (decision 8) | To build: the chain selects from records, and a rung with no record cannot be selected | What the measure's sampling can miss. A group with such features is recorded as not qualified |
| What play did to an occurrence survives the eviction of its cell (decision 12) | To build: changes are simulation state keyed by the canonical identity and filed under the cell an occurrence is in now. A cell's buffer is a cache | An identity that changes with how the scatter is written. The equivalence check covers that |
| The authority and a presenter evaluate the same occurrences for a cell (decision 12) | Today one list serves both, for a whole world. To build: one function evaluates a cell, and both demands call it | Nothing in the arithmetic, which is fixed-point. The comparison of a headless World's cell bytes with a presenting one's covers the rest |
| A present's outcome is credited to the attempt that made it (decision 15) | Not built: the driver's id starts again on a new swap chain. To build: the record's key is the host's own attempt number, with the driver's id and its swap chain stored beside it | A driver that reports an outcome with no id |

## Where each look is authored

A look value is an authored parameter with a neutral engine default
([AGENTS.md](../../../AGENTS.md#puck-is-pre-release), "Authors rule" and "The
Puck standard is authored"). The standard package (`worlds/standard/`)
chooses the values it wants, and any author can override them. The debug
world's values are set by its artists and judged against the references by
the lead and the look work. The owner corrects a look and is never asked to
choose one.

The table names, for each look value the rendering plan touches, the authored
setting that carries it. Where the state says "to build", the engine's work
is the mechanism and its neutral default, never the value.

| Look | Where an author sets it | State today | Engine default |
|---|---|---|---|
| The tone curve and its constants | `render.tonemap` (`WorldTonemap`) | A switch exists. It names one curve whose constants are in engine shader code. To build with decision 5: the curve as authored data in the standard package, chosen per world and per camera | No curve: the encode alone |
| Exposure: the manual value, the bounds, the compensation, the meter's band, percentiles and centre weight, the adaptation time and the local share | `render.exposure` (`WorldRenderExposure`) | Exists. The value, the bounds, the compensation and the hold can each be bound to a literal, a state row, or keys on a clock | `SdfExposure.Default` |
| Exposure per place | The same members, bound to a state row that a world's rules write when the player enters a place. The gallery's inspection lever pins its hold this way | Exists for a stepped change. To build: a blend across a doorway or a portal, as authored data | No per-place behaviour |
| A light's softness | A directional light's `angularRadius` (`WorldRenderLight.Directional`), which can be bound | Exists. A light that authors no angular size takes a soft edge the engine chose (`SdfLights.DefaultPenumbraSlope`), which is a look held in the engine. To build with decision 6: the constant goes, the standard package authors the soft edge it wants on its own lights, and an explicit size is honoured as today | A point source: with no authored size, the shadow's edge is as hard as the map's filter allows |
| The widest soft edge, and the blend band between cascades | Members of `render.shadowCoverage` (`WorldRenderShadowCoverage`) | To build with decision 6 | The light's own geometry with no cap, and a band one filter footprint wide |
| How far shadows reach: the near, far and caster distances, and later the cascade radii | `render.shadowCoverage` | Exists as three distances. To build with decision 6: the radii as one authored set | The schema's defaults |
| How far behind a distant shadow may lag | A refresh interval or an angular step on a far cascade entry | To build with decision 6 | No lag: an entry refreshes as its light moves |
| Which lights cast shadows, and how a handover fades | A light's `shadow` mode, and `render.shadowLights`, `shadowFadeSlots`, `shadowFadeTicks` and `shadowOverflow` | Exists | One slot and an instant handover |
| The night | The sky layers, the lights and the exposure bounds keyed on a day clock, as `worlds/debug/night.puck` and `worlds/debug/day.puck` author them | Exists. [The register](../../decisions/rendering.md#lights-screens-and-the-camera-are-photometric) rules that how night reads is the author's | No night look |
| Indirect light: the tier, the source gains, the bounces, the lighting grid, the relight and trace allowances and the lighting age | `render.indirect` (`WorldRenderIndirect`) | Exists. With decision 4 the allowances' unit becomes work per second. To build with decision 7: the probe levels (`SdfMeshIrradiance.LevelsOf`) as authored data | The Medium tier and unlimited allowances |
| The quality presets | `render.low`, `render.medium` and `render.high` (`WorldQualityPreset`), authored in `worlds/standard/quality.puck` | Exists | No presets |
| What a nested view gives up at each presented size | A table in the quality presets | To build with decision 14, replacing `WorldSessionSceneEmitter.ReducedQuality` and `WorldScreenBinder.CameraViewQuality` | No restriction |
| The detail budgets: a tier's share of a pixel, a level's error in pixels, and its hysteresis | Members of the quality presets | To build with decision 8, replacing `SdfMeshTier.PixelShare` and `SdfMeshDetail.CoarserPixels` | The values of the recorded rulings. This is the one row whose default is a ruled engine behaviour and not a neutral value |
| How ground cover thins with distance | A curve on a scatter | To build with decision 8 | Everything is kept |
| Bloom, sharpening, the cel style and the atmosphere | `render.bloom`, `render.upscaleSharpness`, `render.toon` with the materials' styles, and `render.atmosphere` | Exist. One default is not neutral: a world that authors no atmosphere still gets fog (`SdfAtmosphere.Default`). That fog is a look held in the engine, and it moves to the standard package with decision 5's retune | Off or absent, once the default fog has moved |
| What a fixed-refresh display shows when the loop cannot hold its rate | A presentation setting beside the quality presets, which a player's option overrides | To build with decision 15, over `PresentPacingPolicy` | Frames as they arrive |
| The back of a portal's glass | A property of the portal | To build. Decision 14's fitted window culls by facing where the authored back shows no image | No image |
| How much of its own exposure a window's world applies before the viewer applies the rest | A member beside the exposure settings | To build with decision 5, replacing the constant `SdfPhotometry.WindowAdaptation`. The standard package authors today's three quarters | None of its own: the viewer's exposure alone |
| The share of the display at which a nested view's update rate steps down | Members of the quality presets | To build with decision 14, replacing the thresholds behind `SdfUpdateRateTiers.ViewHertz` | Every view at the display's rate |

No step of the build order waits for a look to be chosen. A mechanism lands
with its neutral default and with the standard package's value beside it.
After decision 5, every place is judged again against its reference. The
exposure compensation that each place authors today stands in for the
missing encode.

Open items listed several of these values among the owner's sittings and
calls. That page now matches this section: its owner's list holds engine
behaviour and owner-only actions, and the look values point here.

## What would change the answer

Each item is a result that would change a decision, and what would happen
then.

- **The produce breakdown names a part that no decision covers.** Decision
  2's order follows the breakdown, not this page.
- **Capturing a shown but unchanged residency turns out to be cheap.** The
  cheap freshness check of decision 2's last step is not built.
- **The tick's breakdown shows the two partitions save little.** The tick's
  serial fixes come first, and stepping world instances in parallel is
  weighed.
- **One more world in frame costs most of a step.** Where the tick runs
  becomes the job system plan's next step, with the arrangements decision
  3 lists. An arrangement that adds latency for the world the player acts
  in goes to the owner with its measured hand-off.
- **The measured headroom is below the lighting's minimum.** If the GPU
  frame alone misses the bar once the CPU stops being the limit, the
  lighting gets its minimum and no more, in jobs bounded to one frame's
  allowance. The views pass (decision 13) and the cascades
  (decisions 6 and 11) then come first.
- **The planned gains do not add up to a bar device's distance from its
  bar.** The order is cut again for that device. What remains is the
  owner's call, with the default stated there.
- **The cold bake is limited by its processors and not by its GPU
  requests**, or the reverse. The fix on the critical path lands first
  (decision 10). The other waits for a measurement that shows it pays.
- **The cold bake is still well over its figure after the first speed
  fix.** The second fix moves up. Nothing else waits for the figure.
- **A bake stage's product is too large to keep.** That stage is not keyed
  on its own, and an edit to it still costs the stages before it.
- **A cube per source group costs more memory than a zone has.** Decision
  7's per-source storage narrows to the directional lights and the sky.
  Local lights keep the full relight.
- **A bar device behaves differently.** A Steam Deck limited by vertex work
  moves three things ahead of the instance table: the removal of redundant
  triangles, the indexed-draw experiment and the
  far cascade's floor tier. A device limited by memory bandwidth moves the
  per-pass savings and the planar visibility record up.
- **Fewer than four probe hits share each surface entry.** The visibility
  bits then need a different store, with compressed rows, and
  hardware ray queries are measured sooner.
- **The floor fails the lighting check for most groups near a fine probe
  level.** The lighting's tier becomes a third mesh there. Its bytes are
  priced against the budget, and the streaming plan's option C is the
  fallback.
- **The floor fails its acceptance test on thin geometry.** The far cascade
  keeps the coarsest collapse level for those groups.
- **Recording a rung's deviation from the field costs too much bake time.**
  The chain stops at the levels of the tier that was asked for. The floor
  is still drawn where a mesh is missing, as detail that is owed. Nothing
  steps down to it by distance, because its coverage test does not bound
  how far it moved an edge.
- **Per-copy tiers pop on thin parts in motion.** The chain stops the same
  way. Meshes baked over many copies and the thinning curve carry the far
  distance.
- **Indexed draws are unsupported or slower on a bar device.** They do not
  become the baseline's format, and the packed vertex layout does not
  return. Grouping goes ahead on today's draws. Where indexed draws pay on
  stronger hardware they may be an enhancement there.
- **The metre-sized filter cannot match a brute-force reference**, to
  within what the views filter leaves over. The blend band stays. The
  pyramid is replaced by more samples, in the near cascade only.
- **The fixed layers and every way of running shading programs miss on a
  device.** Shading programs are not the cause there. The per-phase
  readings name what is, and that is fixed first.
- **The trace shows that machine frames are not dropped** at the debug
  world's tick rate. The machine cadence work shrinks to not
  uploading unchanged frames.
- **An author wants today's appearance.** It is written as a tone transform
  over the corrected conversion, in the world or in the standard package,
  and judged on SDR and HDR. The working image's convention changes only if
  every producer and consumer changes together.
- **A bar device's driver provides no display time.** That device's
  displayed interval stays an absent column. Its frame bar is then read
  from the work of each frame the host produced, with skipped and dropped
  presents reported. That shows what the host did. It does not show the
  bar was met on screen.

## Research still owed

Each item names the work that needs it. Research holds only that work.

- **Display timing on the bar devices**, before the record for every
  present is designed. Which present-timing and present-id extensions each
  device's driver exposes, and which stages of a present each surface can
  time, read from the device. Answered in part by the round on GPU
  capabilities: NVIDIA's Windows driver and the open driver's main branch
  both list the present-timing extension
  ([its entry on present timing](../rendering-research.md#gpu-capabilities-puck-does-not-use-yet)).
  Still owed: the read from each device, and which driver version the
  Steam Deck ships.
- **Linux and the Steam Deck**, for the Deck's bring-up and for decision
  15's timing work. Presentation-time and tearing protocols, the
  compositor's frame limiter, timer wake-up jitter, and the driver's
  present wait.
- **Root search and compaction on the GPU**, before the bake's sampling
  moves to one round trip and before the command list is compacted. Nothing
  we read measures either, so each lane measures its own.
- **A mesh store shared across GPUs**, in the one store's check. Whether
  the bake kernel's floating-point results reproduce across vendors is read
  on each bar device there.
- **The temporal history**, as the first day of the first wave's `history`
  lane. Karis's notes and the body of the survey, before the window's
  layout or its sample count changes.
- **Terrain**, beside the first wave and before the tiles. Almost no
  terrain source was read.
  The open questions are which seam is right between dual-contoured tiles
  at different tiers, weighed against the overlap rule; how tiles scale;
  and what a hybrid with a height field costs. Clipmap and chunked terrain
  are read for the same round.
- **Many lights, and the shadow of an area light**, before emitting
  surfaces are lit as areas and before screens become selected lights. How
  a light-emitting face is blocked, sizing a
  light's range from its emitted luminance, and clustered light selection.
  These serve decision 7 and decision 15.
- **Light and shadow across the edge of a world**, before the check of the
  seam between worlds. Whether a neighbour's geometry casts shadows and
  bounces light
  across an adjacency. One limit is known: a seam's shared band holds at
  most eight placements and eight bodies (`WorldAdjacencyGeometry`).
- **Nested views in shipped engines**, and transparency with bounded
  memory, if decision 14 still leaves a portal-crossing frame costing close
  to two views.
- **Change propagation in incremental systems**, with the first bet's
  memo. How build systems and incremental compilers (Salsa
  among them) track what a result read, what tracking costs against the
  work it saves, and how they recover from a dependency they missed.
- **Light through openings**, before the sixth bet's prototype. Light
  fields sampled on a plane (Levoy and Hanrahan), how shipped engines light
  a room through a portal, and how a published sample's age and scope are
  bounded.
- **Verifying shared derived products**, with the fourth bet's memo on how
  a product is named. How shared caches are authenticated (Unreal's shared
  derived-data cache is unauthenticated by its own documentation), what
  checking a mesh against an analytic source costs and what a sampled
  check can miss, and the bandwidth at which fetching beats deriving on
  each bar device.
- **Tracing against distance fields**, before the seventh bet's
  comparison. Shaft
  culling (Haines and Wallace), distance-field shadows and occlusion in
  shipped engines, layouts for sparse bricks, and what Puck's own distance
  volume showed when it was measured against meshes
  ([the register](../../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs)).

## Existing systems each decision extends

Each decision extends a mechanism Puck already has. None adds a second.

| What the work needs | The existing mechanism it extends |
|---|---|
| Schedule a residency's capture | The runtime already hands a frame's footprints to its packages before they prepare (`RenderGraphRuntime`), and the scheduler of views (`RenderGraphScheduler`) takes capture as one more item with an update rate |
| Know how loaded the GPU frame is | Dynamic resolution's reading of the views' GPU time against the display's period (`IWorldFrameLoadSource`, `WorldDynamicResolution`), and the scheduler's unset ceiling on passes times pixels (`RenderGraphFrame.PassPixelBudget`) |
| Run every kind of background GPU job | The one background service and its budget (`SdfGpuWork`, `SdfGpuWorkBudget`), and its measured rate per kind of work (`SdfGpuWorkRate`). Its probe client (`SdfMeshProbeGpuWork`) already chains a cache kernel, a compaction and an indirect dispatch |
| Say what an image holds | How an image source's values are encoded (`ImageColorEncoding`) and the conversion of a source into the working image (`ImageSourceConversion`) |
| Give cascades to lights by role | The allocator's ranking of candidates (`WorldShadowAllocator`) |
| Compaction, grouping, every cascade's mask and the glass list | The one place that classifies draws (`SdfMeshOcclusion`) |
| Requests for detail, and tiers | `SdfMeshDemand` and `SdfMeshTier`, under the selector (`SdfResidencySelector`) and the memory account (`MemoryAccount`). Baked meshes stay in `SdfMeshLibrary` and `SdfMeshStore` |
| Measure a rung against the field | The existing measure of a level against the field's surface (`SdfMeshFieldLevels.Measure`) |
| Sample a cell more finely | The bake's refinement of a flagged cell (`SdfSampledLattice.Refine`, `SdfDualContouring.ExtractRefined`) |
| Work for worlds nobody sees, and a view's share of the display | The update-rate tiers (`SdfUpdateRateTiers`), the host's measure of each view's share (`WorldViewGraphHost`), the test of whether a portal is in sight (`SdfPortalSight`) and the cached field answers (`WorldFieldAnswer`) |
| Fit a window's camera to its glass | The existing fit through the whole opening (`WorldWindowFrustumFit`) |
| Shading, and the kernel for a motion program | `SdfShadeProgram` and its table of operations (`SdfShadeOps`), and the bins of `SdfShadeDispatch`, which glass reuses |
| The occurrences of a placement, and what play did to them | The compiled list both drawing and collision read (`CompiledPlacementStamp`), and keyed state over placements (`WorldPlacementDeal`, `WorldPlacementResponse`) |
| Sharing static geometry, and moved transforms | `WorldRigidAssets` and `SdfMovedTransforms` |
| When a present was shown, and waiting for room to present | What every presenter reports (`IPresentTimingFeedback`, `PresentTimingSample`) and the wait both backends share (`ISwapChainPacing`) |
| Waits, workers, GPU completion, pacing and the frame's timeline | `HostWaits`, `JobPool`, `GpuQueueTimeline`, `PresentPacingPolicy` and `HostFrameTimeline` |

## Outside techniques not taken

The round considered these techniques from outside Puck and did not take
them. The last column says what would reopen each.

| Technique | Verdict | Why | What would change it |
|---|---|---|---|
| Virtual shadow maps with cached pages | Rejected. The virtual geometry plan's shadow decision (D8) stands | A moving light and deforming casters invalidate cached pages, and the day clock and the wind run by default. The one account of a moving sun could not be fetched | A measurement showing that redrawing static geometry under unchanged projections dominates a cascade |
| Sparse shadow pages without caching | Waits for a measurement | The culling half is the receiver mask Puck already has. The storage half is unmeasured | The occupied share of a cascade's cells measured to be small |
| Refitting the projection every frame, scrolling cascades, static slices for the sun, baked time-of-day visibility | Rejected | Each gives up the snapped, held fit, or assumes a still light or a fixed world | Nothing, while the day clock is authored |
| Moment, variance and exponential shadow maps | Rejected for opaque shadows. Open for glass | Light bleeds between storeys. For translucent casters, the moment paper's experiments blend sorted geometry and name unsorted alternatives such as stochastic transparency (unverified). So sorting is one implementation and not a requirement. Puck's sum of optical depth is exact in any draw order, but only for the whole span down to the first opaque surface. It cannot say how much glass lies in front of a receiver that stands between two panes, which is what storage by depth answers | The check decision 6 names: a receiver in front of, between and behind two panes |
| Sun shadows by marching the field | Rejected | It puts a field march in the frame | The seventh bet's number |
| Sun shadows by ray tracing | Taken up where no map reaches, on a device with ray queries | The plan's second wave, after decision 6's maps by role ([GPU capabilities](../rendering-research.md#gpu-capabilities-puck-does-not-use-yet)) | Its measurement on the RTX 4070 |
| A cluster hierarchy traversed on the GPU. A simplifier library as a dependency | Watched | A second builder beside the one mesher. Weak on aggregates of small things. Puck is independent work | The virtual geometry plan's cluster measurement (G9) |
| Mesh shaders | Taken up as a lane of the plan's second wave, on a device that has them | Puck's backends have no mesh-shader path, and it is a second way to draw, so it keeps the indirect path as its fallback and stays only where measured to pay ([GPU capabilities](../rendering-research.md#gpu-capabilities-puck-does-not-use-yet)) | The split of one shadow pass on the RTX 4070 showing that commands and vertex work are not what it pays for |
| Work graphs, device-generated commands | Watched | A second implementation of a chain the background queue shortens on every device first. Device-generated commands were not read on any bar device | A GPU trace of the probe and bake chains on the RTX 4070 showing idle time between their dispatches |
| A rasterizer written in compute | Watched | A second rasterizer with unknown gain. A depth-only variant needs only 32-bit atomics. 64-bit atomics on buffers are required at Puck's shader model on Direct3D 12, but optional on Vulkan, where Puck would have to require them at device creation and add them to `VulkanShaderCapabilities` (unconfirmed on the bar devices). A visibility id with depth, clipping and depth ties, and Puck's cost on it, are unqualified. No pass has been shown to be limited by what it would replace | The split of one shadow pass on the RTX 4070 showing rasterization of small triangles is what it pays for |
| Arrivals requested by the GPU alone, pages below the mesh, slabs | Rejected | Decision 9: a group that is not in memory cannot report | Nothing |
| Hints for refinement from geometry already in memory | Held as an experiment | Decision 9: prediction keeps arrivals and camera turns, and the hint uses the observation channel | Over-fetching or slow refinement still measured once observation decides what is kept |
| Transition cells between tiers, geomorphing | Rejected | Both tie a tile to its neighbour's tier or add a position per vertex | Pops shown in motion after decision 8 |
| Contouring or placement on the GPU. Contouring without gradients | Rejected, and watched | A second mesher. Only an abstract was read | The paper read in full |
| A cap on triangles | Rejected | Detail follows size on screen, not a count | Nothing |
| An error measure for the collapse that includes attributes (quadrics with attributes) | Open to a measurement, inside the one collapse | It can model creases: the method penalizes moving a vertex off a sharp edge, though it does not promise every seam survives. As a second simplifier it stays rejected | It is tried inside the existing collapse, under its crease and material rules, and judged by deviation from the field, thin features and bake time |
| Radiance caches fed from the screen or by reservoirs, and per-pixel gathers, as whole systems | Rejected, with ideas taken | They are per view, stochastic or dependent on history, which breaks a reproducible drain | A sparse cache whose behaviour from cold is measured to be no worse |
| Hysteresis, random rotation, moment-based visibility, probes switched off, frozen lighting, a drain started warm | Rejected | The relight is a fixed quadrature, and stale light is the defect being fixed | Nothing |
| Hardware ray queries for probe rays | Taken up in the plan's first wave (`rays`) | Decision 7 removes most of the probe rays it would speed up, but the same structures carry shadows by ray where no map reaches, which nothing else removes. Its first reading is rays per second against the software walk ([GPU capabilities](../rendering-research.md#gpu-capabilities-puck-does-not-use-yet)) | Ray queries no faster than the software walk for probe rays, in which case probes keep the software walk |
| Ray-traced supersampling of edges, cone-traced supersampling, resolve weights that follow exposure | Rejected, or not judged | The first needs hardware ray tracing. The second is a paper nobody read. The third has no source | A shimmer on a bright edge in a moving capture |
| A visibility record of 4 to 8 bytes | Rejected | It moves work into a kernel that is already limited by registers | Nothing |
| A bloom threshold, exposure kept on the GPU, local tonemapping in image space, glare, a default grading table, fused post passes | Rejected or deferred | The bloom decision is settled. Exposure has consumers on the CPU. Local tonemapping makes halos. A grade or a glare is a look, which an author adds as data and the engine does not default | A measured need for the mechanism, or an author's program for the look |
| Weighted, moment or voxel transparency, per-pixel lists, a two-layer deep buffer | Rejected | They approximate sums Puck computes exactly, or they need unbounded storage | Many overlapping translucent layers |
| Stencil portals, layered multi-view, planar reflections, a screen-space march on glass as the baseline | Rejected or gated | A second mechanism for nesting views. The last waits on the water plan's gate | A portal-crossing frame costing close to two views |
| A tick on its own thread, or a pipelined tick | Open. Not rejected and not adopted: a design question the job system plan owns | Decision 3 | What one more world in frame costs, and the time from input to display |
| Parallel recording now, a submission thread, fibers, switching the garbage collector's mode | Held or rejected | In order: its own admission rule, contracts that assume one thread, the rule about the host, and a stopgap beside a fix at the cause | Recording measured at several milliseconds |
| Generation every frame, generated blades, wind as a vertex offset, impostors, a learned field, height-field terrain as the engine's own | Rejected | Decision 12. Each is a second representation | The terrain round's evidence |
| Outline text as the baseline, compute binning for 2D, an oblique near plane, texel shading, a vendor's latency kit, exclusive fullscreen | Rejected or later | Costlier at small sizes. No evidence was read. A second mechanism. Tied to one vendor. It takes the display away from the host | Large text in the world, or a measured need |

## Work the areas do more than once

Each row is one job that the code does in several places today. The decision
in the last column leaves one implementation, except where the row says the
places differ for a reason.

| The job | Where it is done today | Decision |
|---|---|---|
| Choosing a reduced quality for a nested view | Two identical constants, in two projects | 14 |
| Rendering the sky map | A GPU kernel, a CPU render that decides whether to refresh, and a CPU render for each lighting capture | 7 |
| Deciding what a pass can show | The GPU kernel. A CPU copy of the test (`SdfMeshCull`, `SdfMeshBox`) that glass and update rates still read. A projection onto the light's plane that is written out five times in shader code | 11 |
| Whether the sun reaches a point | Cascades in some views and a probe bit in others, chosen per view | 6 |
| Choosing detail by size on screen | Three constants, and four places that compute size on screen: a group's tier, a mesh's level, a body's update rate and a viewport's pixels per metre | 8 |
| Budgeting background work | A budget in pass pixels, a share of GPU wall time, relight credits per frame, a fixed cost per bake sample, and a CPU share per service | 4 |
| Reading how loaded the GPU frame is | Dynamic resolution's load source, which the budget rule of decision 4 extends and does not copy | 4: one reading |
| Compacting a list on the GPU | The probe client's compaction, which runs in series, and the shading bins' scatter, which keeps no order | 11: one ordered scan and scatter, written as a shared header |
| Counting frames in flight | Five different numbers: the render graph's 3, the tables' ring of 2, the Vulkan presenter's 2, the Direct3D 12 swap chain's 3 and the background service's 4 slots. These are not one number held five times. Each counts a different thing with its own readers: images, how far the CPU runs ahead, and how long a GPU reader keeps a resource. What they can share is the way a slot is admitted and released. Each capacity stays derived from its own readers | 15, 4 |
| Knowing a GPU submission finished | Completion signals shared with a batch's owner, beside the device's own timeline | 4 |
| Running CPU work on other threads | The bake's own pool, the lighting's loops, thread-pool tasks and the job pool | The job system's pool (J1) |
| Placing keyed ranges in a buffer | One allocator for the probe scene and another for the mesh region | The lighting work packet |
| What a colour value means | Linear for shading, decoded for keyed blends, encoded for image sources and the display encode | 5 |
| A tone curve | The root graph's filmic constants, the cel style's curve and the cel program's shoulder | 5, as authored data |
| Applying a view's bloom | The root's `place` pass for a placed view, and the view's own `bloom-apply` pass for a filmed one | 5 |
| Shading a surface | Authored programs and fixed layers | 13 |
| A motion's value and its bound | An interpreter that looks drivers up by name, the audit's fixed-point pose, a measured reach and a measured speed | 12 |
| Storing baked meshes | One store per backend | 10 |
| Reducing an image to a statistic | The composite's histogram for the exposure meter, and the grid that computes a screen's glow. These are two different statistics of two different images, and stay two. They can share a way of sampling | 13 |

## Sources

The decisions cite a source by its first author or its product; each list
below gives the title and the address it was read at. A source is listed as
confirmed when the verification pass found in it at least one quotation the
round uses.

**Confirmed.**

- Karis, Stubbe and Wihlidal, "A Deep Dive into Nanite Virtualized Geometry", SIGGRAPH 2021 Advances: <https://advances.realtimerendering.com/s2021/Karis_Nanite_SIGGRAPH_Advances_2021_final.pdf>
- Tatarchuk, "Destiny's Multithreaded Rendering Architecture", GDC 2015: <https://advances.realtimerendering.com/destiny/gdc_2015/Tatarchuk_GDC_2015__Destiny_Renderer_web.pdf>
- Wright, Narkowicz and Kelly, "Lumen: Real-time Global Illumination in Unreal Engine 5", SIGGRAPH 2022 Advances: <https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Lumen-Wright%20et%20al.pdf>
- Apers, "Shipping Dynamic GI on Frostbite", SIGGRAPH 2024 Advances: <https://advances.realtimerendering.com/s2024/content/EA-GIBS2/Apers_Advances-s2024_Shipping-Dynamic-GI.pdf>
- Leblanc and Conte, "Raytracing the World of Assassin's Creed Shadows", SIGGRAPH 2025 Advances: <https://advances.realtimerendering.com/s2025/content/Advances%202025%20-%20Raytracing%20the%20world%20of%20Assassin's%20Creed%20Shadows.pdf>
- "GI with AMD FidelityFX Brixelizer", GDC 2024: <https://gpuopen.com/gdc-presentations/2024/GDC2024_GI_with_AMD_FidelityFX_Brixelizer.pdf>
- "GI-1.0: A Fast Scalable Two-Level Radiance Caching Scheme": <https://ar5iv.labs.arxiv.org/html/2310.19855>
- NVIDIA Spatial Hash Radiance Cache, integration guide: <https://github.com/NVIDIAGameWorks/Spatial-Hash-Radiance-Cache/blob/main/docs/Integration.md>
- Majercik and others, "Scaling Probe-Based Real-Time Dynamic Global Illumination for Production": <https://arxiv.org/html/2009.10796v3>
- "Advanced Soft Shadow Mapping Techniques", NVIDIA, GDC 2008: <https://developer.download.nvidia.com/presentations/2008/GDC/GDC08_SoftShadowMapping.pdf>
- "Cascaded Shadow Maps", Microsoft: <https://learn.microsoft.com/windows/desktop/DxTechArts/cascaded-shadow-maps>
- Dybisz and Witanowski, "Shadows Optimizations in Cyberpunk 2077", SIGGRAPH 2021 Talks: <https://history.siggraph.org/wp-content/uploads/2022/06/2021-Talks-Dybisz_Shadows-Optimizations-in-Cyberpunk-2077.pdf>
- Logdahl, "27,000 dragons and 10,000 lights": <https://logdahl.net/p/gpu-driven>
- Haar and Aaltonen, "GPU-Driven Rendering Pipelines", SIGGRAPH 2015 Advances: <https://advances.realtimerendering.com/s2015/aaltonenhaar_siggraph2015_combined_final_footer_220dpi.pdf>
- Sakmary, "Virtual Shadow Maps", thesis: <https://dcgi.fel.cvut.cz/en/theses/2025/sakmamat/>
- Burns and Hunt, "The Visibility Buffer", JCGT 2(2): <https://jcgt.org/published/0002/02/04/paper.pdf>
- Lottes, "Advanced Techniques and Optimization of HDR (VDR) Color Pipelines", GDC 2016: <https://gpuopen.com/download/GdcVdrLottes.pdf>
- "Driving Toward Reality: Physically Based Tone Mapping and Perceptual Fidelity in Gran Turismo 7", SIGGRAPH 2025: <https://blog.selfshadow.com/publications/s2025-shading-course/pdi/s2025_pbs_pdi_slides_v1.1.pdf>
- Ju, Losasso, Schaefer and Warren, "Dual Contouring of Hermite Data": <https://www.cs.wustl.edu/~taoju/research/dualContour.pdf>
- Schaefer, Ju and Warren, "Manifold Dual Contouring": <https://www.cs.wustl.edu/~taoju/research/dualsimp_tvcg.pdf>
- Remedy at REAC 2024: <https://enginearchitecture.org/downloads/REAC_2024_Remedy.pdf>
- "Nanite Foliage", Epic: <https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-foliage>
- GPU Gems 3, chapter 16, "Vegetation Procedural Animation and Shading in Crysis": <https://developer.nvidia.com/gpugems/gpugems3/part-iii-rendering/chapter-16-vegetation-procedural-animation-and-shading-crysis>
- "Reduce latency with DXGI 1.3 swap chains", Microsoft (archived): <https://learn.microsoft.com/en-us/previous-versions/windows/apps/dn448914(v=win.10)>
- "Advanced API Performance: Swap Chains", NVIDIA: <https://developer.nvidia.com/blog/advanced-api-performance-swap-chains>
- The `VK_EXT_present_timing` and `VK_KHR_present_wait2` proposals: <https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_present_timing.html>, <https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_present_wait2.html>

**Confirmed, and used by no decision.**

- meshoptimizer release notes: <https://github.com/zeux/meshoptimizer/releases>
- Bevy pull request 17413, two-phase occlusion culling: <https://github.com/bevyengine/bevy/pull/17413>

**Partial**: the source holds the words but says something narrower.

- Sloan, Kautz and Snyder, "Precomputed Radiance Transfer for Real-Time Rendering in Dynamic, Low-Frequency Lighting Environments": <https://www.cs.jhu.edu/~misha/ReadingSeminar/Papers/Sloan02.pdf>
- Wihlidal, "Optimizing the Graphics Pipeline with Compute", GDC 2016: <https://gdcvault.com/play/1023109/Optimizing-the-Graphics-Pipeline-With>
- Bittner and others, "Shadow Caster Culling for Efficient Shadow Mapping" (draft): <https://www.cg.tuwien.ac.at/research/publications/2011/bittner-2011-scc/bittner-2011-scc-draft.pdf>
- "Distance Field Soft Shadows", Epic: <https://dev.epicgames.com/documentation/unreal-engine/distance-field-soft-shadows-in-unreal-engine>

**Read, and not checked word for word**: through a summarizing fetch, a text
extraction or an abstract, or under a candidate the pass did not reach.

- Geometry, bake and streaming: Evans, "Learning from Failure" (Dreams): <https://advances.realtimerendering.com/s2015/AlexEvans_SIGGRAPH-2015-sml.pdf>; Kapoulkine, "Billions of triangles in minutes": <https://zeux.io/2025/09/30/billions-of-triangles-in-minutes/> and "Meshlet size tradeoffs": <https://zeux.io/2023/01/16/meshlet-size-tradeoffs/>; Garland and Heckbert on quadrics with attributes: <https://mgarland.org/files/papers/quadric2.pdf>; Carrera and others, "Dual Contouring of Signed Distance Data" (abstract): <https://www.alphaxiv.org/abs/2604.00157>; Lengyel, "Voxel-Based Terrain for Real-Time Virtual Simulations": <https://transvoxel.org/Lengyel-VoxelTerrain.pdf>; Keeter, "Massively Parallel Rendering of Complex Closed-Form Implicit Surfaces": <https://www.mattkeeter.com/research/mpr/keeter_mpr20.pdf>; Kuth and others, "Towards Practical Meshlet Compression": <https://arxiv.org/abs/2404.06359>; the Sampler Feedback specification: <https://microsoft.github.io/DirectX-Specs/d3d/SamplerFeedback.html>; Direct3D 12 memory management strategies: <https://learn.microsoft.com/en-us/windows/win32/direct3d12/memory-management-strategies>; Vulkan sparse resources: <https://docs.vulkan.org/spec/latest/chapters/sparsemem.html>; Ziman on DirectStorage: <https://gpuopen.com/gdc-presentations/2023/GDC-2023-DirectStorage-optimizing-load-time-and-streaming.pdf>; RTX Mega Geometry: <https://developer.nvidia.com/blog/nvidia-rtx-mega-geometry-now-available-with-new-vulkan-samples>; Barrett, sparse virtual textures: <https://silverspaceship.com/src/svt>
- Visibility and submission: Arntzen, "Modernizing Granite's mesh rendering": <https://themaister.net/blog/2024/01/17/modernizing-granites-mesh-rendering/>; Tellusim's compute raster benchmark: <https://tellusim.com/compute-raster/>; FidelityFX SPD: <https://gpuopen.com/fidelityfx-spd/>; Mara, McGuire and Luebke, "Lighting Deep G-Buffers": <https://research.nvidia.com/sites/default/files/pubs/2013-12_Lighting-Deep-G-Buffers/Mara2013DeepGBuffer.pdf>; the `VK_EXT_device_generated_commands` proposal: <https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_device_generated_commands.html>; mesh nodes in work graphs: <https://devblogs.microsoft.com/directx/d3d12-mesh-nodes-in-work-graphs/> and <https://gpuopen.com/learn/work_graphs_mesh_nodes/work_graphs_mesh_nodes-intro/>; the work graphs specification: <https://microsoft.github.io/DirectX-Specs/d3d/WorkGraphs.html>; "Virtual Geometry in Bevy 0.14": <https://jms55.github.io/posts/2024-06-09-virtual-geometry-bevy-0-14>
- Illumination: Majercik and others, "Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Fields": <http://www.jcgt.org/published/0008/02/01/paper-lowres.pdf>; "Signed Distance Fields Dynamic Diffuse Global Illumination": <https://arxiv.org/html/2007.14394v1>; Godot's SDFGI article: <https://godotengine.org/article/godot-40-gets-sdf-based-real-time-global-illumination/>; Wright, "Dynamic Occlusion with Signed Distance Fields": <https://advances.realtimerendering.com/s2015/DynamicOcclusionWithSignedDistanceFields.pdf>; DDGI resampling: <https://arxiv.org/html/2108.05263v1>; holographic radiance cascades: <https://arxiv.org/abs/2505.02041>; split radiance cascades: <https://arxiv.org/pdf/2607.20384>; grid-based reservoirs: <https://research.nvidia.com/labs/rtr/publication/boksansky2021rendering>; Unity's probe volume streaming: <https://docs.unity.com/en-us/engine/6000.0/manual/lighting-overview/lighting/probevolumes/streaming>; Lumen's technical details: <https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine>
- Shadows: Lauritzen, Salvi and Lefohn, sample distribution shadow maps: <https://www.advances.realtimerendering.com/s2010/Lauritzen-SDSM(SIGGRAPH%202010%20Advanced%20RealTime%20Rendering%20Course).pdf>; Pettineo, "A Sampling of Shadow Techniques": <https://therealmjp.github.io/posts/shadow-maps>; "Virtual Shadow Maps", Epic: <https://dev.epicgames.com/documentation/unreal-engine/virtual-shadow-maps-in-unreal-engine>; Peters and others, improved moment shadow maps: <https://www.jcgt.org/published/0006/01/03/paper-lowres.pdf>; Tan and others, RTSDF: <https://www.scitepress.org/PublishedPapers/2022/109962/pdf/index.html>; FidelityFX Denoiser: <https://gpuopen.com/manuals/fidelityfx_sdk/techniques/denoiser/> and Hybrid Shadows: <https://gpuopen.com/learn/hybrid-shadows/>; ray traced shadows, Ray Tracing Gems chapter 13: <https://boksajak.github.io/files/RTG1_RayTracedShadows.pdf>; Olsson, Billeter and Assarsson, virtual shadow maps for many lights: <https://www.cse.chalmers.se/~uffe/More%20Efficient%20Virtual%20Shadow%20Maps%20for%20Many%20Lights.pdf>; Lefohn, Sengupta and Owens, resolution-matched shadow maps: <https://faculty.digipen.edu/~gherron/references/References/ShadowAlgorithms/ResolutionMatchedShadowMaps.pdf>; Giegl and Wimmer, fitted virtual shadow maps: <https://www.cg.tuwien.ac.at/research/publications/2007/GIEGL-2007-FVS>; Stephano's virtual shadow maps: <https://ktstephano.github.io/portfolio/virtual-shadow-maps>
- Shading and the image: the Gran Turismo Sport course outline: <https://history.siggraph.org/?p=188541>; Marrs and others, adaptive temporal antialiasing: <https://research.nvidia.com/sites/default/files/pubs/2018-08_Adaptive-Temporal-Antialiasing/adaptive-temporal-antialiasing-preprint.pdf>; Jimenez, next generation post processing: <https://www.iryoku.com/next-generation-post-processing-in-call-of-duty-advanced-warfare/>; Chapman, dynamic local exposure: <https://john-chapman.github.io/2017/08/23/dynamic-local-exposure.html>; Wronski, localized tonemapping: <https://bartwronski.com/2016/08/29/localized-tonemapping/>; FSR 2.1 notes: <https://gpuopen.com/meet-fidelityfx-super-resolution-2-1>; Godot on pipeline compilations: <https://docs.godotengine.org/en/stable/tutorials/performance/pipeline_compilations.html>; Karis, "High Quality Temporal Supersampling" (listed, not read by the judgement): <https://de45xmedrsdbp.cloudfront.net/Resources/files/TemporalAA_small-59732822.pdf>; Pedersen, temporal reprojection in INSIDE: <https://s3.amazonaws.com/arena-attachments/655504/c5c71c5507f0f8bf344252958254fb7d.pdf>; Temporal Super Resolution, Epic: <https://dev.epicgames.com/documentation/en-us/unreal-engine/temporal-super-resolution-in-unreal-engine>; the Khronos PBR Neutral tone mapper: <https://github.com/KhronosGroup/ToneMapping/blob/main/PBR_Neutral/README.md>
- Glass, water and nested views: adaptive voxel-based transparency: <https://advances.realtimerendering.com/s2025/content/AVBOIT_SIG2025_MDROBOT-final.pdf>; moment-based transparency: <https://momentsingraphics.de/?page_id=210>; McGuire and Bavoil, weighted blended transparency: <https://www.jcgt.org/published/0002/02/09/paper.pdf>; single layer water and Substrate, Epic: <https://dev.epicgames.com/documentation/unreal-engine/single-layer-water-shading-model-in-unreal-engine>, <https://dev.epicgames.com/documentation/unreal-engine/overview-of-substrate-materials-in-unreal-engine>; Claybook at GDC 2018: <https://gdcvault.com/play/1025030/Advanced-Graphics-Techniques-Tutorial-GPU>; "Rendering Portals in Virtual Reality": <https://arxiv.org/html/2601.20722v1>; the CS50 portal notes: <https://cs50.harvard.edu/games/2018/notes/11/>; Lengyel, oblique view frustum clipping: <https://www.terathon.com/lengyel/Lengyel-Oblique.pdf>
- Scheduling and the frame: O'Donnell, "FrameGraph": <https://www.slideshare.net/slideshow/framegraph-extensible-rendering-architecture-in-frostbite/72795495>; the Render Dependency Graph and parallel rendering overviews, Epic: <https://dev.epicgames.com/documentation/unreal-engine/render-dependency-graph-in-unreal-engine>, <https://dev.epicgames.com/documentation/en-us/unreal-engine/parallel-rendering-overview-for-unreal-engine>; Gyrling on fibers (overview only): <https://gdcvault.com/play/1022187/Parallelizing-the-Naughty-Dog-Engin>; Arntzen, "Render graphs and Vulkan": <https://themaister.net/blog/2017/08/15/render-graphs-and-vulkan-a-deep-dive/>; "Vulkan Dos and Don'ts": <https://developer.nvidia.com/blog/vulkan-dos-donts>; the Khronos async compute sample, queue priority and frame-in-flight pages: <https://docs.vulkan.org/samples/latest/samples/performance/async_compute/README.html>, <https://docs.vulkan.org/tutorial/latest/Advanced_Vulkan_Compute/08_Asynchronous_Compute/04_queue_priority.html>, <https://docs.vulkan.org/tutorial/latest/Synchronization/Frame_in_Flight/01_introduction.html>; NVIDIA on async compute and on synchronization: <https://developer.nvidia.com/blog/advanced-api-performance-async-compute-and-overlap>, <https://developer.nvidia.com/blog/advanced-api-performance-synchronization/>; the GDC 2017 async compute deep dive: <https://gpuopen.com/download/GDC2017-Asynchronous-Compute-Deep-Dive.pdf>; .NET latency modes: <https://learn.microsoft.com/en-us/dotnet/api/system.runtime.gcsettings.latencymode>, <https://learn.microsoft.com/dotnet/standard/garbage-collection/latency>; Reflex: <https://www.nvidia.com/en-gb/geforce/news/reflex-low-latency-platform>; Zanni, synchronized tracing of implicit surfaces: <https://arxiv.org/html/2304.09673>
- Foliage and terrain: van Muijden, "GPU-Based Procedural Placement in Horizon Zero Dawn": <https://www.guerrilla-games.com/media/News/Files/GDC2017_VanMuijden_GPUBasedProceduralPlacementInHorizonZeroDawn.pdf>; McLaren, deferred texturing in Horizon Forbidden West: <https://www.guerrilla-games.com/read/adventures-with-deferred-texturing-in-horizon-forbidden-west>; procedural generation with work graphs: <https://gpuopen.com/learn/work_graphs_mesh_nodes/work_graphs_mesh_nodes-procedural_generation/>; neural geometric level of detail: <https://research.nvidia.com/labs/toronto-ai/nglod/assets/nglod.pdf>; Simplygon's vegetation optimization: <https://www.simplygon.com/features/vegetationoptimization>; Broadleaf: <https://80.lv/articles/broadleaf-developing-a-real-time-solution-for-rendering-trees/>; instancing leaves with mesh shaders: <https://jysandy.github.io/posts/mesh-shaders>; Johansen's erosion filter: <https://blog.runevision.com/2026/03/fast-and-gorgeous-erosion-filter.html>; the Ghost of Tsushima grass and wind talks (abstracts only): <https://gdcvault.com/play/1027214/contactUs>, <https://gdcvault.com/play/1027350/Blowing-from-the-West-Simulating>
- Presentation: the `VK_KHR_present_wait` reference page: <https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_present_wait.html>; dynamic rate control, libretro: <https://docs.libretro.com/development/cores/dynamic-rate-control/>; Lengyel, font rendering from glyph outlines: <https://jcgt.org/published/0006/02/02/paper.pdf>; Chlumsky, Sloup and Simecek, multi-channel distance fields: <https://dcgi.fel.cvut.cz/wp-content/wpallimport-dist/publications/pdf/publications-2018-sloup-cgf-msdf-paper.pdf>; Vello's architecture page: <https://skia.googlesource.com/external/github.com/linebender/vello/+/refs/heads/sparse/ARCHITECTURE.md>; Levien, a sort-middle architecture for 2D: <https://raphlinus.github.io/rust/graphics/gpu/2020/06/12/sort-middle.html>; Hillesland, texel shading: <https://gpuopen.com/learn/texel-shading/>; the view instancing specification: <https://microsoft.github.io/DirectX-Specs/d3d/ViewInstancing.html>; Persson, practical clustered shading: <https://www.humus.name/Articles/PracticalClusteredShading.pdf>; the Wayland commit-timing protocol: <https://wayland.app/protocols/commit-timing-v1>

**Opened by a later review's reader.**

- Guennebaud, Barthe and Paulin, soft shadow mapping with a hierarchical shadow map, EGSR 2006: <https://www.labri.fr/perso/guenneba/docs/SoftShadowMapping-egsr06.pdf>
- The `VK_EXT_present_timing` reference page: <https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_present_timing.html>
- Peters and others, on transmittance to each surface, I3D 2018: <https://momentsingraphics.de/I3D2018.html>
- Kubisch, "Introduction to Turing Mesh Shaders", NVIDIA: <https://developer.nvidia.com/blog/introduction-turing-mesh-shaders/>
- The Mesa 23.0 release notes: <https://docs.mesa3d.org/relnotes/23.0.0.html>
- Lengyel's transition cells: <https://transvoxel.org/>

**Not fetched or not reachable.** Nothing in the plan rests on them.

- "Virtual Shadow Maps in Fortnite Battle Royale Chapter 4": <https://www.unrealengine.com/tech-blog/virtual-shadow-maps-in-fortnite-battle-royale-chapter-4>
- Acton, "CSM Scrolling": <https://advances.realtimerendering.com/s2012/insomniac/Acton-CSM_Scrolling(Siggraph2012).pdf>
- Sakmary, "Resolution Matched Virtual Shadow Maps": <https://cescg.org/wp-content/uploads/2025/04/Sakmary-Resolution-Matched-Virtual-Shadow-Maps.pdf>
- "ReSTIR GI": <https://diglib.eg.org/handle/10.1111/cgf14378>
- Conty Estevez and Kulla, many lights with adaptive tree splitting: <https://fpsunflower.github.io/ckulla/data/many-lights-hpg2018.pdf>
- Heitz and others, linearly transformed cosines course notes: <https://www.advances.realtimerendering.com/s2016/s2016_ltc_rnd.pdf>
- Epic on auto exposure in 4.25, and on Lumen in Fortnite: <https://www.unrealengine.com/tech-blog/how-epic-games-is-handling-auto-exposure-in-4-25>, <https://www.unrealengine.com/tech-blog/lumen-brings-real-time-global-illumination-to-fortnite-battle-royale-chapter-4>
- Yang and others, "A Survey of Temporal Antialiasing Techniques" (abstract only): <https://research.nvidia.com/labs/rtr/publication/yang2020survey>
- "Cone-Traced Supersampling With Subpixel Edge Reconstruction": <https://doi.org/10.1109/TVCG.2023.3343166>
- Fry, HDR colour grading and display in Frostbite: <https://ea.com/frostbite/news/high-dynamic-range-color-grading-and-display-in-frostbite>
- Lagarde and de Rousiers, "Moving Frostbite to Physically Based Rendering": <https://blog.selfshadow.com/publications/s2014-shading-course/frostbite/s2014_pbs_frostbite_slides.pdf>
- Schmitz and others, contouring on the GPU: <https://diglib.eg.org/items/c94e2e75-fc6a-4c55-8e77-c6ef52460288>
- "Nanite GPU-Driven Materials", GDC 2024: <https://schedule.gdconf.com/session/nanite-gpu-driven-materials/899486>
- "Skipping Spheres": <https://diglib.eg.org/bitstream/handle/10.2312/cgvc20241219/cgvc20241219.pdf>
- "Real-Time GPU Tree Generation": <https://diglib.eg.org/handle/10.2312/hpg20251168>
- Loubet and Neyret, hybrid mesh-volume levels of detail: <https://diglib.eg.org/handle/10.1111/cgf13138>
- Concurrent linked lists on the GPU: <https://diglib.eg.org/handle/10.2312/CGF.v29i4pp1297-1304>
- Rough refraction, screen-space refraction by Newton's method, and constant-time refraction: <https://core.ac.uk/download/49936561.pdf>, <https://jcgt.org/published/0015/01/03/paper.pdf>, <https://highperformancegraphics.org/2025/publications/posters/Constant-Time%20Light%20Refraction%20Simulation%20-%20HPG2025.pdf>
- Umbra's introduction to occlusion culling: <https://medium.com/@Umbra3D/introduction-to-occlusion-culling-3d6cfb195c79>
- Qualcomm on multiview extensions: <https://www.qualcomm.com/developer/blog/2023/10/reducing-rendering-work-and-memory-operations-stereoscopic-scenes-new-multiview-extensions>
- The Reflex developer page: <https://developer.nvidia.com/Reflex>
- Quilez, distance functions: <https://iquilezles.org/articles/distfunctions>

---

[Plans](../README.md) · [Open items](../open-items.md) · [Rendering](../rendering.md) · [Decisions](../../decisions/rendering.md)
