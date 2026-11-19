# Open plan items

The work that is ready to schedule, as work packets: each packet is one lane's
worth of work with one cause or one area. [Next](#next) ranks the packets the
next integration batch takes, [beside the queue](#beside-the-queue) lists what
runs next to it without a rank, and [owner decisions](#owner-decisions) holds
every call that waits on the owner. The phase-scale work of each programme
lives in its plan; [the plans index](README.md) names every plan.

How the file stays usable:

- **Filing a finding places it.** A review finding, a lane's leftover or a
  walkthrough flaw goes into the packet that shares its cause or area, as the
  defect, its failing case and the next action, with its symbol or file and
  its severity (`[high]`, `[medium]`, `[low]`). With no such packet, open one.
  A finding never sits in a list of its own.
- **Next is one list.** Each line names one packet and its severity. The lead
  re-ranks the whole list at each integration batch, by payoff with anything
  high first, and never prepends a second order. A plan states what its steps
  depend on, never a rank among the programmes.
- **A packet opens with its owner, its severity and its next action.** Where a
  plan owns the programme, the next action names the plan's step, and the
  packet holds only the defects and leads the plan does not own.
- **A finished packet is deleted, not ticked**, and a closed item with it.
  Before deleting, move the durable fact it carries (a cause, a hazard) to its
  owning home (AGENTS.md rule 9). History, one machine's readings and who
  found a thing go in the commit that files or closes it.
- **An owner call goes to [owner decisions](#owner-decisions)** with both
  sides in a sentence each. A ruling the owner has made goes to its owning
  home, never here.

## Hand-off

The rendering plan is the active programme, and another session orchestrates
it. Read this, then [the plan's first wave](rendering-research.md#the-first-wave).
The next lead rewrites this section when it hands off in turn.

- **Branch first.** All of this round's work is on `main`. Create the wave's
  integration branch from the latest `origin/main` (`git fetch`, then
  `git switch -c lead/rendering origin/main`), push it, and land every lane
  of the waves there; `main` moves only when the owner asks.
- **The owner's priorities** are velocity, code quality and feature
  expansion, in that order: code first, verification after (AGENTS.md).
- **Start here:** dispatch the plan's first wave of fifteen lanes at once, in
  the merge order the plan gives where lanes meet. Three of them come from
  the round on [GPU capabilities Puck does not use yet](rendering-research.md#gpu-capabilities-puck-does-not-use-yet),
  which ranks each technique by what it gives the RTX 4070 and names the
  path every other device runs. The evidence bar is
  building what a lane touched and running it in the debug world at 2560 by
  1440, never a test suite. Measurement runs beside the lanes. Adversarial
  review is only for the classes [hazards](../development/hazards.md#silent-invariants)
  names.
- **The 1440p baseline** is the plan's measured column ([the bars and the
  budget](rendering-research.md#the-bars-and-the-budget)), read on a quiet
  machine. The slowest frames are the lift chapter's simulation steps; the
  largest GPU part is the views pass. That run used the debug world's boot
  settings, not a device's `high` table, until the `bench` lane applies one.
- **Allocation, after `vkalloc` landed:** the per-frame recording path and
  the irradiance snapshot no longer allocate. What remains, for the plan's
  second-wave allocation lane:
  - the warm loop re-bakes on both backends, about 4 GB of allocation in a
    chapter where it runs, as the detail level falls over the route
    (`SdfResidencySelector` and the bake);
  - every code version shares one mesh store, so a less-used identity stays
    cold, and each backend has its own set because the bake identity
    includes the kernel bytecode; compare allocation across backends or
    builds only on a warmed store (the dotnet-performance skill says how);
  - `SdfMeshIrradiance.RelightGpu` uploads the whole volume whenever
    `Stands` fails, about twice a second while walking;
  - irradiance scene rebuilds allocate `Node[]` through `SharedArrayPool`
    misses;
  - the lift allocates in spikes (frame thread 2.2 MB mean against 60 KB
    median);
  - about twenty direct `NativeMemory.Alloc` and `AllocZeroed` calls use the
    C runtime's heap beside mimalloc (`IAllocator`, `MimallocAllocator`): two
    native allocators for one job, folded onto `IAllocator` by the same lane.
  Mimalloc for native storage, the engine's own `GpuMemoryPool` and
  `MemoryAccount` for GPU memory, and workstation GC with `ConserveMemory`
  stay the choices. The World now runs with the runtime's default dynamic
  PGO (it was off since the first commit for no recorded reason); the next
  quiet 1440p benchmark reads the effect, and its readings are the new
  baseline.
- **Beside the first wave, not before it:**
  - targeted skill evals for what this round taught: `delegate` (a lane hands
    back after build and boot instead of running windowed checks),
    `orchestration` (batched landings, the three-slot GPU cap) and a
    regression run of `dotnet-performance` after its .NET 11 rewrite, plus
    the graders the consolidation changed (`delegate` d11 and d17,
    `orchestration` o01); a skill that misteaches is fixed mid-wave;
  - a reduction pass over the orchestration skill (about 100,000
    characters; it repeats several rules three or four times): every rule
    kept, repetition cut, as AGENTS.md's consolidation did;
  - the rendering plan's step designs folded into the plans that own them.
    The owner's discussion that the fold waited for has happened, so the
    fold runs now, beside the wave.
- **In flight:** nothing. The GPU capabilities research round is in the
  rendering plan: its ranked list, its three first-wave lanes and its five
  second-wave lanes.
- **Settled this round and recorded in their homes:** the frame bar's sizes,
  topologies and device order (AGENTS.md); deployed data is scratch
  (AGENTS.md); named engine gaps are filled first, and a material improvement
  may land below the bar (AGENTS.md); a target lacking a capability never
  vetoes a technique, and each device meets its bar by the path its quality
  table enables (AGENTS.md); vendor binaries
  ([the register](../decisions/runtime-and-delivery.md#vendor-binaries)); the four engine questions of the
  rendering plan ([what the owner settled](rendering-research.md#what-the-owner-settled-1));
  shared products in the [decision register](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth).
- **Release rehearsal.** The rehearsal's fixes (retrying downloads, the AGB
  toolchain, the TraceEvent version, the registry) are on `main`, and each
  push to `main` runs Release Azure. Read the latest run on `main` before the
  owner pushes `lead/rendering`'s work there, and fix what it shows on
  `lead/rendering`.
- **Before the owner pushes `main`:** the owner deferred the
  [security review](#security-review)'s write-door leaks for this push, as
  AGENTS.md allows for a high finding; they stay high work, ranked first in
  Next. The hosted dev store is reset at that deploy. Pushing `main` is the
  owner's.
- **Restored branches awaiting the owner's word:** `lane/g5` (the
  occlusion-aware rate floor, review answered, needs a merge and its windowed
  check), `lane/toon-3` (the third cel model, prior art),
  `lane/companion-v1` (prior art for custody steps 9 and 10), and
  `lane/moth-suit`, `lane/moth-pilot` and `lane/swallow-7` (the owner's pair
  session; their rotation libraries are prior art).

## Next

Ranked whole at the latest batch. The frame bar, the cold-bake bar and the
visual bar are one campaign (AGENTS.md), so their packets interleave: a look
built on the engine's systems stresses them, and what it surfaces is fixed at
the cause. Work whose correctness rests on an invariant is designed and
reviewed before code ([orchestration](../../.claude/skills/orchestration/SKILL.md)).

1. [high; the owner deferred it for the current push to `main`] [Security review](#security-review):
   the write door still tells a withheld row from an absent one at fields
   and paths a per-field enumeration missed (an adversarial review of the
   read-door fix found five high leaks); the structural write door
   ([the disclosure plan](disclosure.md)) is in design.
2. [high] [Residency and streaming](#residency-and-streaming): the
   [streaming and memory campaign](streaming-and-memory.md#the-build-order),
   agreed with the owner: step 5's floor first (an engine gap), then step 0's
   baseline legs on both backends in a quiet
   window, steps 1, 2 and 3's windowed readings, and step 7's readings (the
   prototype is built: the capability read on this machine and the indexed
   read's paired cost window on both backends,
   [the measurement](streaming-and-memory.md#the-prototype-as-built)), and
   step 3's servicing readings (the slow present and the quiet cold bake,
   [step 3's check](streaming-and-memory.md#step-3-one-reservation)).
3. [high] [Bake detail](#bake-detail): its engine gaps first (a feature
   thinner than a bake cell, a scaled primitive's bound, moving blends step 4),
   then the contour's triangles that face out by every lattice fact and are
   still turned from the field (vertices the planes put off the surface,
   slivers beside a crease, a face narrower than a cell), which keep six blend
   exhibits and the Moth's shin groups red.
4. [medium] [Collision versus drawn mesh](#collision-versus-drawn-mesh): sub-cell
   seams where boxes touch, then the valley sectors' buried colliders.
5. [high] [Engine job system](#engine-job-system): the memo's next step:
   the scheduling stage's, C0.3's and C0.4's windowed readings, the control plane's
   step C0.5, then the pool's design.
6. [high] [The frame bar's owed frames](#other-frame-time-findings): arrival
   work proportional to change: R1h through R1c's windowed checks, then S2 of
   [its memo](arrival-proportional.md) in that order (R0's rule has read
   them), then the frame thread's field queries.
7. [high] [Refactoring campaign](#refactoring-campaign): the duplication
   backlog, as parallel lanes beside everything above.
8. [high] [The visual bar](#the-visual-bar): the engine gaps behind its
   misses first (the valley's walls, the water, the moving cutter), then the
   caldera water as its first proof, built by [Water](#water)'s lanes A3 to
   A5.
9. [high] [Dense landscapes](#dense-landscapes): the plan's next action, the
   repeated CPU dependency work, then memory attribution.
10. [high] [Lighting and GI](#lighting-and-gi): what the GPU trace still owes
   under a moving sun, then a cold area lit the moment a view arrives.
11. [medium] [Vulkan regressions and GPU robustness](#vulkan-regressions-and-gpu-robustness):
   one draw identity, then [visibility](visibility.md)'s V2a and V2b ahead of
   V1a, with `lane/g5` landed beside them.
12. [high] [Transmission](transmission.md): its implementation lanes in order;
    the defects it closes are in [Lighting and GI](#lighting-and-gi).
13. [high] [Bake proportional to change](#bake-proportional-to-change): one
    store for both backends, then the collapse's acceptance search.
14. [high, before `main`] [Disclosure](disclosure.md#the-build-order): steps 5
    and 6, with step 0's fuzzer and step 0b's principals.
15. [high, before `main`] [Rule faults](rule-faults.md): what its page lists
    as not built.
16. [high] [Delivery, launcher and CI](#delivery-launcher-and-ci): a green
    end-to-end Release Azure rehearsal, then the production deploy, which a
    push to `main` needs.
17. [high] [Transfers, federation and server](#transfers-federation-and-server):
    the custody protocol's steps 6 to 10, then
    [settlement](#settlement-trust-roles-and-revocation)'s specification.
18. [high] [Test runner and replay](#test-runner-and-replay): the body's write
    door from step D5.
19. [medium] [Certified field contact](#certified-field-contact): box and
    ellipsoid bodies, then clearance close to a slope.
20. [medium] [Language and authoring](#language-and-authoring) and
    [author leaks to the standard](#author-leaks-to-the-standard): the inline
    console command hang, the cost corpus, the overlay text advance.
21. [high] [The benchmark](#the-benchmark): the windowed reading of
    `puck benchmark` on both backends ([the memo](benchmark.md#the-build-order)'s stage 3). It replaces
    [the walkthrough](walkthrough.md)'s line, and the lead ranks it at the
    next batch.
22. [high] [Rendering research](rendering-research.md#the-build-order): the
    research round's order across the residency, lighting, bake, job-system
    and visibility packets above, run as lanes side by side and not as
    stages. Its [first wave](rendering-research.md#the-first-wave) is fifteen
    lanes that start now, each with its files, its model and its finish
    line; its [second wave](rendering-research.md#the-second-wave) names
    what unlocks each lane. Its
    [engine gaps](rendering-research.md#engine-gaps-filled-first) are filled
    first, ahead of further sculpting around them. Measurement runs beside
    the lanes and holds none of them; the lead ranks it at the next batch.
23. Then, by ordinary priority:
    [edit to running](#edit-to-running-hot-reload-and-boot),
    [the port](#port-the-experimental-worlds-missing-features) (each lane
    ahead of the job-system step it proves),
    [the valley's terrain](#the-valleys-terrain),
    [rigid bodies](#rigid-bodies-and-physics),
    [collision versus drawn mesh](#collision-versus-drawn-mesh),
    [CLI](#cli-and-lease-machinery), [MCP](#mcp-and-the-operator-attachment),
    [sweep](#sweep-and-visual-checks), [card tables](#card-tables-in-the-board-game-room),
    [debug tools](#debug-tools),
    [temporal](#temporal-shimmer-and-cadence-flicker), and
    [depth of field](#depth-of-field) once the frame bar is known.

## Beside the queue

Unranked work that runs next to the queue. Read-only and headless work runs
beside the GPU-bound lanes, and no more than one lane besides the lead's
batched window holds the GPU at once.

**Windowed readings owed** to the lead's next GPU window, on the integration
head, each named by the lane whose landing waits on it:

- crease1: the 24 tour exhibits' stored meshes against their CPU bakes, from
  a cold boot per backend with a fresh `--cache-dir` (the Moth is read; see
  [bake detail](#bake-detail)).
- js1 at `debug.go overlook` (the desktop reading is taken), and vrr1's
  `world.rates` burst at `debug.go desktop` on the variable-rate display
  (expect `screens` at most 4).
- relief1: a World boot and captures of a noise-relieved creation moving as a
  whole, on both backends.
- c03: the converged still at a lockstep stop against the build before c03,
  on both backends (the control gap is read on both).
- c04: a `world.reload` that changes the slot layout (an edit that adds an
  animated placement, unverified as the shortest one), windowed on both backends, plain and under
  `world.lockstep on`: `host.frames 300` around it reads the control gap under
  the wait budget plus a pacer period, `world.wait ready 5` issued right after
  the reload either reports ready or names the composition's build, and the
  first boot shows the window's first image after the encode builds, with no
  error line.
- sm7p: the indexed slab read's paired cost on Vulkan with the counted draws
  held equal (Direct3D 12 is read; see
  [the prototype as built](streaming-and-memory.md#the-prototype-as-built)).
- sm3r: the slow-present reading (`world.target 10`, then 5, then 20, from a
  cold store, both backends) and the quiet cold bake, as
  [step 3's check](streaming-and-memory.md#step-3-one-reservation) states
  what each should show.
- The collection pauses behind the slow frames (Engine job system): a
  `dotnet-trace --profile gc-verbose` attribution on the windowed debug world.

**Reviews owed**, findings-only, each by another family than its author, on
landed work in the review-only classes of
[hazards](../development/hazards.md#silent-invariants):

- The demand tiers' cache identity and publication
  ([memo](bake-demand-tiers.md)): an invariant kept by convention.
- The exposure readback's slot lifetime (`RenderGraphSlotResults` to
  `SdfViewExposures.Publish`): GPU fences and resource lifetimes.
- The tour's withhold: a frame held for lighting is withheld and the lighting
  drains on the frame's thread (`SdfWorldPasses.WithheldOf`,
  `SdfGpuWork.Drive`), a wait across threads.
- The hub plan's [durable images](hub-and-saves.md#durable-images) section, a
  design review before its step H7a.
- The temporal reconstruction, a cross-family renderer review
  ([sweep](#sweep-and-visual-checks)).
- Area fix passes owed: the dense-landscapes packet's code, and the
  reserved-component packing and pattern filtering in it.

**Verification requests** for the lead's batched windowed window. A packet's
own windowed checks stay in the packet; these cross packets:

- A burst capture across one hand-over with the group in every frame, and the
  views recorded per frame across one boot-time resample
  ([frame-time findings](#other-frame-time-findings)).
- Three cold bakes per backend from an empty `--cache-dir` with `paused` and
  `lockstep`, to show whether a late retier changes which tiers are stored.
- The moving-blend bursts over a whole cycle on every exhibit: only
  `motion-nod` ran one, and the others showed 16 to 20 frames.
- A body on a twisted static solid, repeated in a windowed World.
- `lane/g5`'s windowed recipe, on the
  [virtual geometry plan](virtual-geometry.md)'s G5 row.
- `world.wait` and `world.reload` over MCP against a windowed World on the
  mesh path at quality high, where frames are slowest; only headless runs
  have shown them.

**Measurement requests** for one quiet window, with no other lane building or
running a World. A tip-only reading runs
`puck lease quiet --wait 600 -- puck bench frames …`
([reference](../reference/cli.md#puck-bench-frames)); a before-and-after pair
is filed with `puck measure`.

- The frozen glass-shadow falls and meadow-rates requests wait on the
  streaming plan's step 4, which rebuilds the `world.wait residency` that
  refuses them; freeze the affected World builds again after it. A longer
  harness wait does not establish residency.
- The frame bar's readings on the RTX 2060 and the Steam Deck, which the
  [rendering plan](rendering.md) still owes.
- The cold bake against the one-minute bar,
  `bench startup worlds/debug --bake --backend <backend>` through
  `puck measure`: a cold windowed boot beside two writing lanes read the
  world residency's bake over the bar on both backends; and the cold-bake
  pause comparison, at least three
  alternating runs with collections read from the trace.
- The field-query counts ([frame-time findings](#other-frame-time-findings)),
  and the gen2 count and pause per 45 s settled, standing and walking.
- The shade matrix's remaining device rows (author shaders step 2) and the
  cascades' exit-layer frame time ([lighting](#lighting-and-gi)).
- The views pass and the relight's local part against an EV 7 exposure floor
  ([lighting](#lighting-and-gi)).

**Unlanded branches on origin**, each kept on purpose:

| Branch | Holds | Waits for |
|---|---|---|
| `lane/g5` | G5, the occlusion-aware rate floor; every review finding answered | a merge of the integration tip, then its windowed recipe |
| `lane/toon-3` | the third cel model (a form normal per pixel, the shade side restored from the continuous diffuse) | prior art for the cel treatment's shade side ([the visual bar](#the-visual-bar)); its `world.toon off` fix is on the integration branch |
| `lane/moth-suit` | the suited Moth's rebuild and the shared head | the owner's pair session |
| `lane/moth-pilot` | the off-suit pilot; `worlds/standard/turns.puck` | the owner's pair session |
| `lane/swallow-7` | the swallow rebuilt to a cited reference document | the owner's pair session |
| `lane/companion-v1` | prior art for the custody protocol's steps 9 and 10 ([transfers](#transfers-federation-and-server)) | step 9 porting it |

## Owner decisions

Every call that waits on the owner: what the engine should do, and the
owner-only actions. How a world looks is its authors' (AGENTS.md, "Authors
rule"), so a look value is not a call: [the rendering research page](rendering-research.md#looks-are-authored)
names where each is authored. The owner corrects the debug world's looks as
its art lead, in the sittings below, and no step waits for a sitting. An
engineering call is the lead's and sits in its packet as a "Decide" line.

Owner-only actions:

- **Pushing `main`**, which deploys to Azure. `lead/rendering` holds the
  rendering wave's work. Push once the read-door leaks ([Next](#next), line 1) and the
  Azure release's causes ([delivery](#delivery-launcher-and-ci)) are fixed and
  the milestone's riskiest areas have had a fresh fix pass (AGENTS.md); red
  tests hold nothing back while test requirements are suspended.
- **Reporting the .NET 11 JIT GC-hole** to dotnet/runtime, with the BCL-only
  reproduction ([delivery](#delivery-launcher-and-ci)).
- **An Azure OpenAI chat and embedding deployment**, which real-model runs of
  the agent harness and embeddings need
  ([debug world content](#debug-world-content)).
- **Restoring test requirements** and the suite's keep-list: which of the
  deleted guards over non-code files return, if any ([tests](#tests-suspended)).
- **Calling for qualification**, which returns the
  [quarantined gates](#quarantined-gates).
- **Hardware for the rendering measurements**: an RTX 2060, a Steam Deck and
  an HDR display, once the engine meets both bars on the RTX 4070. No
  fundamental work waits for them
  ([rendering research](rendering-research.md#the-bars-and-the-budget)).

Sittings, each the owner's eye on a running World:

- The Moth and the swallow, in a pair session; its agenda is
  [the visual bar](#the-visual-bar)'s pair-session item. Within it: gloves or
  bare hands on the off-suit pilot (sheet 04 draws gloves).
- The outdoor lighting direction: sky colour and fill, sun colour and height,
  shadow softness and haze ([lighting](#lighting-and-gi)).
- Feel: the Moth's walk, run, turn and lean values, the dash, first person in
  the lift and stairwell ([motion](#dual-stick-play-and-the-moths-motion)).
- Looks: the faerie; the board room and the card tables, the card faces
  included; the lift panel and reach boxes; the gallery inspection lever's
  values; the glass exhibit on both backends; whether `motion-pulse` reads
  right now that its petals and core blend in their own groups; the bloom's
  scatter of 0.04 and radius of 0.08; how strongly distance fades toward blue
  ([terrain pass](terrain-pass.md#risks-and-owner-calls)).

Open calls, each with its sides:

- **The mirror construct.** G16 as recorded: each limb a template over `side`,
  with nothing new in the language. Or the
  [modeling authoring plan](modeling-authoring.md)'s `mirror` block, lowered
  through `CreationFrame`, on the evidence that three lanes hand-mirrored three
  creatures; it would supersede G16. It rides on the owner's review of that
  plan, which comes before its slices are dispatched.
- **Which of the Moth's halves is right** where they do not mirror: the right
  back pod's 40 parts (ids 1200 to 1240) are rotated differently from the left
  pod's (1100 to 1140), and `sideLock`'s position, `blinkCrease`'s position and
  `kneeGuard`'s profile differ. Either construct above then mirrors the chosen
  half.
- **The chase camera with its back to the stair wall**: swing the boom, rise
  over the wall, or switch to first person there
  ([motion](#dual-stick-play-and-the-moths-motion)).
- **GameInput**: ship or require its redistributable, which the Share button,
  the Elite paddles and more than four pads need, or stay on the inbox runtime
  ([input](#input-channels)).
- **Feet inside session scenes.** Bodies in screens and portals plant their
  feet, which adds surface probes to those scenes, or stay as today with no
  field (`WorldSessionStampSource`).
- **A cube projection for screens on shapes**, beside the sphere the owner
  asked for, or sphere and cylinder alone
  ([screens](#screens-on-curved-and-general-sdf-surfaces)).
- **The lift**: go on its displays as one emulated machine and its ride as an
  authored motion program, or leave both as they are
  ([debug world content](#debug-world-content)).
- **An animation lane for the Moth**, judged through `puck motion` contact
  sheets: go, or wait ([motion](#dual-stick-play-and-the-moths-motion)).
- **Eval cases cut from real lane transcripts**: allowed, or hand-written
  transcripts only, since session transcripts are sensitive
  ([agent tooling](#agent-tooling-and-skills)).
- **A companion's wait at a door** while its hop home stays pending: the
  player may wait for it, or goes on alone and is told
  ([custody protocol](companion-redesign.md#10-owner-calls)).
- **Transmission's looks and the Steam Deck's budgets**, each with the
  design's choice first ([the plan's calls](transmission.md#the-owners-calls)).
- **The streaming plan's two questions**, which have defaults and block no
  step: how pressure feels on a turn, and what a character shows at its floor
  ([the plan](streaming-and-memory.md#the-owners-questions)).
- **Three calls deferred until their systems mature**, each with its
  engineering consequence in
  [the register](../decisions/worlds-and-federation.md#the-owners-deferred-calls):
  the casino product (table-local score, earned or purchased chips, tradeable
  stakes or real money, and for whom); whether Puck builds dealing that hides
  cards even from the dealer; and how the standard's layer move feels (a
  transition area out of combat, or a countdown in place with a rising
  cooldown).
- The ideas [awaiting review](#awaiting-the-owners-review).

## Work packets

### Dense landscapes

Owner: the lead, with performance, rendering and world-authoring owners per
step · high · Next: [the plan's next action](dense-landscapes.md#next-action),
the repeated CPU dependency work its normal-paced trace names, then readback
and physical memory attribution, then GPU instance buckets and the
terrain-following forest belt's qualification at the bars. The plan owns the
assessment, the workload and the order; the visibility, bake, memory, terrain
and job-system plans own the mechanisms.

- [medium] The bounded clearing relief adds real mesh and query work: scale it
  only with measured detail and visibility handling. Pattern filtering uses a
  conservative footprint approximation. Owner: material and bake.
- [medium] The forest's native clearing captures show sharply black crown
  undersides, which surface texture does not close: a lighting finding, apart
  from the intended soft coloured shadows. Repeated design-session reloads
  exceed the Deck-tier private-memory budget; a fresh-run attribution is owed.
- [medium] `WorldSessionSceneEmitter.TrySurface` rebuilds its static field
  after avatar-only visual program changes, and the root's general definition
  and neighbour revisions are broader than query geometry. Count emission
  requests, canonical geometry changes and evaluator constructions apart, then
  retain against the exact emitted inputs, keeping the root and routed scopes'
  different placement, screen and adjacency policies. Its frequency in the
  forest workload is unmeasured. Owner: presentation queries.
- Thousands of independently authored grass poses want generic pose-program
  work; caching repeated slots alone does not remove them.

### Engine job system

Owner: principal designs, another family reviews the design before any build,
a specialist builds · high · Next: the next step of
[the memo's build order](engine-job-system.md#6-build-order), which states the
order once: the scheduling stage's windowed reading, C0.3's and C0.4's, then
[the control plane's](engine-job-system.md#the-control-planes-phase-and-bounded-waits)
step C0.5 (C0.1 to C0.4 are built), then the pool's design (J1). The garbage-collection cuts
below, the replay burst's tick budget and `CodeDerivedTable`'s generations run
beside it as [ordinary work](engine-job-system.md#what-the-job-system-leaves-to-ordinary-work).
Where the tick runs is an open design question this plan owns
([its open question 7](engine-job-system.md#open-questions)); it starts when
the rendering plan's `frame` lane has measured what one more world in frame
costs.

- [medium] On Vulkan, during a lighting convergence under lockstep, the
  control gap reads p99 119 ms and max 247 ms over 300 iterations, made of
  simulation (`simulate` p99 93 ms, max 116 ms) and produce work (max 70 ms),
  not waits; Direct3D 12 read `simulate` at 0 over the same kind of window.
  No held tick catches up in a burst: under lockstep the loop takes at most one
  step an iteration (`FixedStepPump.TryStep`) and spends a held interval
  without stepping (`FixedStepPump.Hold`), and a paced burst sheds what is due
  once its steps spend one step's period (`FixedStepPump.Advance`). So each
  93 to 116 ms `simulate` is one step, and the Vulkan run stepped where
  Direct3D 12 held: its frames answered rendered between converging frames
  (inference, from the code; the reading has no per-iteration render
  completion). C0.5 owns the step's joins and drains, which bound the control
  gap once they run through the door; what the step does for 100 ms on Vulkan
  is a measurement: `host.frames` with the tick lane and a `dotnet-trace` of the
  pump over a stepping iteration during a convergence on Vulkan.
- [medium] A rated item's phase is held by its own cadence, so a long frame
  that spans an interval for one item and not another (a hitch, a missed
  frame on a fixed signal) can land two items that took a tier half a period
  apart on one frame, and nothing moves them back: coalescing is absorbing.
  A phase kept on the shared presentation clock (an item's position within
  the period carried across its updates, or a lattice of period boundaries)
  restores the spread but lets an interval pass the floor by up to a frame
  where the greedy rule never does, so it changes what `PresentationRate.Holds`
  promises; decide that before building it. `PresentationRateLawTests`
  pins the cadence and the irregular frames.
- [high] Gen-1 collections dominate the slow frames: at `debug.go desktop` on
  Direct3D 12, 600 iterations held 64 over 16.7 ms, the worst carrying 20 to
  28 ms pauses (47 collections pausing 674 ms), one iteration allocating
  68763816 bytes. The machine was loaded, so the times are indicative; the
  pauses and allocation are not load-bound. Attribute the allocation with
  `dotnet-trace --profile gc-verbose` and cut it at its sources (the churn
  item below names the probe cache and the dressed frame copies).
- [high] Concurrency is hand-rolled across the engine (`lock`, `Task.Run`,
  `new Thread`, `ThreadPool`, `Parallel.For`), densest in the bake and
  irradiance code; the memo's C0 and J1 move it. Beside them: make thread
  ownership of the state arena, the frame thread and each GPU queue a checked
  type, prefer publish-once handoff to shared state under locks, and research
  a systematic schedule explorer for .NET (Microsoft Coyote) beside the
  `--workers` harness, since these defects rarely show in one run.
- [medium · unconfirmed] A tick-cadence screen source asked for at the tick's
  own rate (a 30 Hz floor under a 30 Hz tick) has a new image only on a
  step's frame, so its floor releases it there and no lead can move it
  (`RenderGraphScheduler`'s `SourceDue`): it would upload beside every tick.
  Read it with the scheduler's phase law at 30 Hz, then decide whether such a
  source uploads on the frame after the step.
- [low] A rated item's phase is chosen against its own latest update
  (`PresentationPhases`), so items that take a tier on different frames are
  not balanced against each other, and nothing weighs a view's cost against a
  screen's. The frame plan J1 gives the `Frame` class is where a placement by
  cost belongs.
- [low] `WorldViewPaneMappingLawTests.APortalFaceNoConsumerSeesSchedulesNothingUnlessItsWorldArrives`
  fails its arriving case on the integration tip: a session whose world's
  meshes still arrive behind the camera is not scheduled. Owner: rendering.
- [medium · unconfirmed] `WorldDocument.AttachSink` delivers an attach primer
  that reaches `WorldDisclosureImages.Current` without taking
  `WorldServer.AuthorityGate`, while in-tick delivery and ordered-domain
  writes run under it. A frame-thread attach (loopback transport, a session
  screen) could read the arena while a federation or socket thread writes it.
  Route the attach through the authority operation as a peer's attach does,
  with a lock-order audit of its callers; it goes with C0.
- [high] Remaining managed allocation churn, read on the windowed Release
  debug World with `dotnet-trace --profile gc-verbose`; zero steady gen0 is
  not reached. In measured order:
  - Steady frame: `ProbeCache.Retain` replaces the probe cache's table whole
    and `Grow` again as probes land (what the cache keeps is the streaming
    plan's lighting step's). Publication now uses table-owned dense arenas:
    inserts within capacity allocate no per-key objects, and idle retired
    values release their blocks on retirement or the next write/accounting
    visit after their readers leave. Table replacement still allocates arrays;
    its byte report includes arena machinery, held retired values and block
    headers. Then the dressed `SdfFrame` copies.
    On the bake writer thread `CacheRetention.EnforceSetsCore` enumerates the
    store on every write. Small: `BindingBarSeatComposer.PlacedSlots`
    enumerating `CompiledBindingBarLayout.Banks.Values` once a slot,
    `BindableScalar.AuthoredValues`' iterator and `WorldMachineHost.State`'s
    strings.
  - Cold bake: `SdfMeshPathBake.ContourProjection`'s triangles,
    `SdfContour.SplitAtCreases`, `SdfDualContouring.ExtractRefined`'s vertex
    lists, `SdfMeshCollapse.Collapser`, `SdfMeshPlanarMerge.Merger`,
    `SdfMeshMaterialCut`, the encode path (`SdfMeshStoreCodec`), the lattice's
    gradient and crossing dictionaries and `CrossingRoot[]`, and the piece,
    dictionary and plane bookkeeping inside `Merge`.
  - GPU side: `SdfMeshProbeGpuWork` recreates a slot's origins buffer
    whenever the batch size changes.

  Continue with owned storage, through `IAllocator` and `MimallocAllocator`
  where it goes native; a shared cross-thread pool is the memo's. The
  streaming plan's managed-churn step reads this list.
- Open from the allocation cuts:
  - [medium] Retained large arrays compacted by `ConserveMemory=5`
    (`Puck.World.csproj`) are the likelier cause of long suspensions, and the
    settled LOH stays large after `host.memory collect`. The cold-bake pause
    comparison needs alternating runs on a quiet machine with collections
    read from the trace.
  - [low] The reused frame builder (`SdfCompositionFrameSource`) keeps the
    last emission's objects alive into gen2.
  - [low] A cached rigid template (`WorldRigidAssets`) matches only on the
    total material count, so one avatar joining or leaving invalidates every
    template; and `TemplateScratch` there is a second cache of retained
    emissions beside `WorldPlacementStamper`'s.
  - [low] `WorldSeatBindings` checks overlay gates in three places, with two
    change detectors for one fact.
  - [low] `WorldSessionSceneEmitter` and `WorldAdjacencySceneEmitter` still
    allocate the avatar material arrays.
- [high] Kit bodies' advance stays serial, and it is most of the creature
  garden's tick (`worlds/debug/garden-crowd.puck`, the parallel tick's forcing
  workload), so the per-partition kit transaction, not the rigid step (T2),
  carries the parallel tick's main gain. A kit body's advance is one arena
  transaction on the one journal: `BeginKitStep` opens a scope, validation
  writes and rewinds the arena, foreign effects mutate another body at once
  and selector commits synchronize every owner (`WorldInputRuntime.Effects.cs`).
  It is a State and Server design with its own cross-family review, taken
  after T1 to T3 measure what remains serial.
- [medium] The rule phase's remaining volatile and visit costs are the memo's
  ([rule evaluation stays serial](engine-job-system.md#rule-evaluation-stays-serial)).
- [medium] Snapshot emission's entity rows, which T1 partitioned, are a small
  share of its cost, so most lies in the serial tail or the subscribers'
  `DeliverSnapshot`: profile it before T3 or R1 assumes otherwise. The
  headless debug world also builds snapshots for its night and garden session
  instances every tick.
- [medium] Held work the scheduling stage leaves: a held body's motion that no
  pack has posed yet (a slide first driven while held) is outside its bound,
  and a held body shows its held pose for the one frame the rate views lag a
  camera cut, since the cameras resolve after the pack
  (`PresentationRate.Holds`, `MotionReach`).
- A remote `WorldSessionMirror`'s per-property pose reads
  (`WorldSessionSceneEmitter`) against its socket thread's in-place delivery
  can compose a frame from two ticks. Use the coherent-copy path the adjacency
  pin already uses (`CopySnapshotTo`). Owner: federation.
- `LatestSlotPublication.TryAcquireLatest` (the shared GPU texture ring a
  camera or probe kernel publishes into) retries in a loop, where
  `PublishBuffer<T>` enters a slot with one atomic increment and has nothing to
  retry. Move the ring's acquire onto that index mechanism, keeping its fixed
  slot count; a GPU-lifetime change that needs a camera or probe-kernel run.
  Owner: platform.
- [medium] The publication-reader audit also found compare/exchange retry
  loops in `SingleSlotPublication.TryAcquireLatest` and `Release`
  (`src/Puck.Platform/SingleSlotPublication.cs`), and the scene-claim reader
  `SdfMeshIrradianceScene.Acquire` (`src/Puck.SdfVm/SdfMeshIrradianceScene.cs`).
  Make acquisition bounded without waiting on another participant; keep GPU
  lifetime checks with the single-slot change. `CodeDerivedTable.CurrentGeneration`
  (`src/Puck.Abstractions/CodeDerivedTable.cs`) also retries while installing a
  changed code generation; its steady read returns directly. Owner: job system
  with platform and rendering.

#### Memory ownership

[The memo's section 4](engine-job-system.md#4-memory-ownership) designs memory
ownership with scheduling, and the account is described in the
[dotnet-performance reference](../../.claude/skills/dotnet-performance/references/data-and-memory.md#owned-storage-and-byte-admission).
Device memory, pools and owner rows are the
[streaming plan](streaming-and-memory.md#the-build-order)'s; the custom no-GC
runtime is [later work](memory-pooling.md). Open beside them:

- [medium] Managed working memory per admitted bake group (the contour's lists
  and per-vertex arrays, the refinement's per-cell masks, the lattice's
  gradient and crossing dictionaries and root array: `SdfSampledLattice`'s
  `Refine`, `CrossingPoints`, `GradientPoints`) is not in a group's peak, so
  the account cannot pace the collections it causes. The cure is the next
  native movers, the contour and the per-cell masks, not a count cap.
- [low] The library releases evicted claims, which frees native payloads,
  under its own gate (`SdfMeshLibrary`'s `Retain`), the gate the frame thread
  takes in `Restart` through `TryTake`.
- [low] `HostMemory.AvailablePhysicalBytes` on Linux reads `/proc/meminfo`
  with LINQ and `string.Split`, up to four times a second while baking.
- The job system's image check is a tolerant still comparison
  ([the memo](engine-job-system.md#6-build-order)). Build a verb, or an option
  on the capture path, that compares two stills against a noise floor measured
  from repeat captures and reports the regions beyond it, reusing the
  tick-exact motion comparison's tolerance
  ([procedure](../../.claude/skills/puck-world/references/schedules-and-tests.md#tick-exact-debug-capture)).

#### Other frame-time findings

The frame bar is read with `bench frames`, `host.frames` (produce split into
`produce-work` and `produce-fence`), `world.residency`'s `gpu-work` line,
`world.counters sdf.mesh-gi` and sampled thread time. `bench frames` pauses
authority at each stop, so it reads the frame side alone; a played frame also
carries the tick.

What an arrival and a hand-over still cost the frame:

- [high] The arrival row implementation owes windowed verification and the
  lifetime fix pass: the tour and meadow cross-check on both backends, a
  presented burst retaining its group across a hand-over, delayed picks through
  a hand-over and the gallery portal, and two Worlds' canonical lockstep stills.
  [The exact R1b request](arrival-proportional.md#windowed-r1b-verification-request)
  names the commands, forcing counts and what decides each. R1a and R1h retain
  their outstanding windowed requests there too. Count-changing arrivals keep
  unrelated owner runs; only a start over rebuilds them. Indexed clip derivation
  now covers every owner kind; [the R1c request](arrival-proportional.md#windowed-r1c-verification-request)
  holds and withdraws the host-mesh witness and the rigid stand-in at the linked
  gallery paintings on both backends. Its straddle, picks and appearance remain
  unconfirmed until those counters and captures hold. Clustered trace scenes (S2)
  remain next; R2 stays with the visibility plan's V1b.
- [medium] Portal straddle bounds omit authored mesh triangles outside the
  creation's field bounds. `WorldCreationInstances.ReachOf` and `PartSpheres`
  read the field shapes, while `WorldRigidAssets.Metadata` derives its reach
  from the creation document; neither includes `WorldPrototype.Mesh`. A mesh
  extending past its field can therefore reach the glass before the detector
  publishes a clip. Include mesh bounds in those shared reach/sphere paths and
  verify host and rigid bodies whose triangles reach beyond their fields. The
  row-writer straddle witness keeps its triangles inside the keel's bounding
  sphere, so its owner-kind check does not settle this separate detector gap.
- [medium] Arrival at a new place can pay a settling cost of several seconds,
  produce and simulate both inflated as if the frame thread were starved,
  which steady readings do not show. A stop's window can also still hold part
  of the arrival (store loads, tier hand-overs, the trace scene rebuilt over
  them), which `world.wait bakes` and `world.wait residency` do not wait for.
  Attribute it with a `dotnet-trace` of the frame thread on arrival.
- [medium] A bake batch in play is cut to the live frame allowance at the
  bake's mean-following rate (`SdfMeshBakeGpuWork`), but a lattice is never
  split, so a first lattice of up to `SdfMeshPathBake.LatticeTaps` evaluations
  taken with nothing queued still runs past a small allowance (`over-allowance`);
  while other jobs are queued it waits for them. Whether allowance-sized
  batches keep enough groups in flight for a cold arrival in play is not
  measured: an earlier cut at a rate that rose to the costliest batch cut the
  groups in flight to a third. Measure a cold arrival in play against the bar.
- [low] A probe scene upload chunk can still measure past the frame allowance
  against its `SdfGpuWork.UploadSecondsOf` estimate (`world.residency`'s
  `over-allowance` and `most-job`).
- [medium] The volume's fill beside many point lights: a fresh probe's relight
  traces every hit texel toward every shadowing point light in range
  (`sdfProbeShade`), so the window beside the teal room's lamps takes minutes
  of the background share and its indirect light is stale that long. A
  per-light visibility shared across a probe's texels, or culling lights by
  each hit's distance before the trace, cuts it at the cause.
- [medium] Vulkan's frame interval under load: its p99 sits above the bar at
  every stop in windows whose busiest frame worked under 16.7 ms, with frames
  that worked a few milliseconds and then waited in the pacer
  (`PresentPacingPolicy`); a quiet window holds the bar. Whether a loaded
  machine should cost Vulkan's pacer more than Direct3D 12's is not found.
- [medium] A residency's final release waits on the frame thread for two
  builds: `SdfWorldPipelineSource.Release` cancels and waits its lease
  acquisition, releasing a last `GpuBuildLease` whose entry is still building
  waits out the creation in the driver (`GpuBuildCache.Retire`), and
  `SdfWorldResidency.CancelShaderReload` waits a reload's builds. A routed
  residency left while its kernels build cold stalls the frame. Retire them
  through `RetiredBuilds` as the specialization does, moving the cache's
  last-lease retire onto the same table. Unmeasured.

The frame thread's CPU work, largest first:

- [high] The tick runs on the frame thread (`HostLoop.Run` through
  `FixedStepPump.Step`), so a frame that steps carries a whole tick. The scan
  that does not follow change is the rule host's fact derivation
  (`WorldRuleHost.DeriveFacts` compares every population slot's pose and facts
  on each pass, `world.counters world.tick`'s `fact-derives` and `fact-slots`:
  about 11 passes over every slot a tick in the debug world, each slot's
  `WorldBody.Facts` evaluating every publishable fact's `FactHolds`, a quarter
  of rule evaluation in a trace), and a replay scope encodes each body's
  continuation every tick (`WorldScopedReplayRuntime.AfterTick`); both wait on
  [the body's write door](#test-runner-and-replay). The move off the frame
  thread is the presentation view's last cut
  ([runtime plan](runtime-and-delivery.md#the-presentation-view)).
  What a tick spends, from a `dotnet-sampled-thread-time` trace of a warm
  headless debug World playing the seam walk's valley crossing at a walk
  (shares of `WorldTick.StepCore`): body advance about
  a third (field support queries through `FixedStepUp.TrySnap` and
  `BodySupportMemo.Query` into `FixedFieldColliderContact.Solve`, whose
  `SdfBoundsCache` keys exact boxes and so misses on every box of a moving
  body over relieved ground; and the step's capture copies,
  `WorldBody.CaptureIntegrationResidue`, `TransferState` and
  `ExecuteProducer`'s motion scratch, which sample as zero-fill and
  write-barrier skid and want an untraced reading before any cut), rule
  evaluation under a third, snapshot emission about a tenth (session sinks
  bringing their audience's whole identity current after every snapshot,
  `WorldDeliveryCursor.DeliveredRows` into `WorldDisclosureImages.Current`,
  although only `DeliverDefinition` and `Holds` read it; and the session
  mirrors' `WorldSessionMirror.FillImage`), the event feed and addon reads
  about a twentieth each. No one cause holds more than a tenth.
- [medium] A composition of an audience's disclosed definition still
  serializes about 12 MB in the debug world (`world.counters
  world.disclosure`: 11 compositions and 140 MB of definition bytes over a
  boot and one seam chapter): every row's skeleton digest
  (`WorldDisclosureImages.DigestOf`) and the state section, on each
  composition (the split between the two is inferred, not measured). A
  session sink's audience recomposes twice a tick through the boot's first
  ticks, as the rows its placements read move its frame. Digest a skeleton
  once per declaration and disclosure revision rather than once per
  composition.
- [high] CPU field queries: the creation effectors' surface probes
  (`WorldCreationInstances.ApplyEffectors`) and the chase camera's clearance
  march (`WorldCameraClearance.March`), together about a third of the meadow's
  frame. Answers live on their owners through one door (`WorldFieldAnswer`).
  Remaining, on a rendered World: count `world.counters world.field-queries`
  and `sdf.point.evaluations` under `world.lockstep` at the meadow and the
  forest road, and confirm the clearance family reuses at rest; read the chase
  target at full precision to find whether its 0.1 mm a frame drift is the
  boom follower's tail (`WorldSeatViewState.Follow`) or a body that does not
  settle, and fix a settling defect at its cause. The clearance march is
  several times dearer on the rim walk's slow frames. Re-check that no probe
  runs while its effector's gate is shut and its weight at rest
  (`WorldCreationInstances.Effectors.cs`).
- [medium] Two thirds of a surface probe's cost is the hit's gradient, six
  exact walks (`SdfFieldEvaluator.TryFieldGradient`), shared with physics. A
  four-tap tetrahedral difference, the GPU interpreter's form, cuts every
  gradient consumer by a third; it moves contact normals, so it is a measured
  change of the deterministic query.
- [medium] Posing follows the rates, and occlusion is not consulted: a
  creation holds only while no camera's frustum and no cascade keeps its
  sphere (`WorldCreationInstances.Shown`), so at flight B the motion gallery's
  exhibits pose every frame behind the stairwell. `lane/g5`'s occlusion-aware
  rate floor is the fix.
- [medium] Bodies tier on an assumed 2 m/s part speed (`BodyPartSpeed`), so a
  sprinting body's planted foot can step more than two pixels at a far tier.
  Pass `max(BodyPartSpeed, measured)` through the `MeasurePartSpeed` placements
  use. `sdf.creations.registrations.rate-held` counts on every rendered frame,
  so it scales with frames; count it once per tick.
- [high] The meadow (`debug.go meadow`) is a forcing workload past both bars.
  What remains of [visibility](visibility.md)'s V2a: under lockstep the 30 Hz
  tier holds nothing; a registration is a whole tile at its nearest point's
  tier, and a tile too coarse is the author's lever (`windMeadowTile`); the
  measured speed bound is the fastest pair of packs yet seen, so
  motion faster than any seen shows one step past the two-pixel rule. Draw
  count is V1's. The meadow is also R1's forcing workload: measure produce's
  draw-recording share before and after.
- The visibility measurements still owed, and the unexplained readings beside
  them, are [the visibility plan](visibility.md#what-the-measurements-showed)'s.
- [medium] `PackagesBeginFrame` carries milliseconds of host work a frame
  (`RenderGraphRuntime`): the residency's capture
  (`SdfWorldResidency.Capture`), the pack and `StageChangedDraws`. Attribute
  it at the slow stops, then make each part proportional to what changed.
- [low] View placement (`WorldViewGraphHost.CompleteFootprints`) and screen
  binding (`SdfWorldResidency.PrepareOnce`: `WorldScreenDecals.Resolve`,
  `WorldScreenBinder.TryRippleOf`) run each frame for every screen.
- [low] Walks over every row or copy each frame that the arrival memo's row
  table does not absorb: the bake's copy layout (`SdfMeshPathBake`) and two
  walks over the frame's instances in `SdfMeshDresser` outside its topology
  build (its copy comparison and its input commit); and `Dress`'s
  `MeshAssetSlots` scan, which grows with the world. Unmeasured.
- [medium] The portal crossing scan visits every population slot for every
  portal face each tick, with an occupancy dictionary write per pair
  (`WorldInstanceHost.Adjacency.cs`). A body-major loop with a bounds test
  before the occupancy write cuts it; the memo's T3 partitions it.
- [low] The upload's fence wait (`SdfWorldTables.SubmitUpload`) is GPU
  back-pressure, counted in `produce-fence`; the ring is two slots deep
  (`SdfWorldTables.FrameRingSize`) while the graph allows three frames in
  flight (`RenderGraphRuntime.DefaultInFlightFrames`). Measure whether a frame
  waits on a ring slot before following the graph's count.

The frame's GPU side:

- [medium] `views` leads the root view at every stop. Open on it: its time now
  that a programless frame records the plain kernels; the plain kernel still
  spills to local memory on Vulkan and the gather's weights on Direct3D 12;
  `world.indirect off` still removes a third of views on Vulkan as the
  gather's work; the views filter tile is 11 words wide, so an 8x4-lane warp
  reading `sdfViewsFilterShadows` and `sdfViewsFilterNormals` puts rows 0 and
  3 on the same banks (read GPU Trace's bank conflicts, then try a row stride
  that clears it).
- [high] The far shadow cascade draws nearly every caster in its box at the
  coarsest level whose error fits its texel. Two geometry limits remain after
  receiver and light-space culling: a level chain stops at two contour cells
  of error, so a finely contoured mesh (the card backs, the console bricks)
  never coarsens toward a far texel (`SdfMeshCollapse.CellShares`), and draws
  with members (the falls' slots) have no host box and draw level zero in
  every pass. Measure each against the cascade's vertices before changing the
  bake identity; then measure receiver-conditioned depth's total saving
  against the box-only path's cadence
  ([gpu cost](../../.claude/skills/rendering/references/gpu-cost.md#pucks-open-cases)).
- [low] A frustum that meets a glass box a wall hides still runs the glass
  trio (`SdfWorldPasses.GlassSeen`); the glass pass's occlusion lies on the
  GPU, past the host test.
- [medium] The temporal resolve is DRAM-bound: its 40-word window is rewritten
  every frame in the per-frame history buffers. One buffer each pixel reads
  and writes for itself saves about 140 bytes a pixel but needs a cross-frame
  barrier: design and review it first. `sdf-composite` keeps local arrays at
  high register allocation, and the bloom passes, `Graph/place`,
  `sdf-mesh-bake` and the sky passes smaller ones.
- [medium] Confirm whether the bench's default `world.indirect medium` is the
  bar's highest quality; if not, every frame reading sits below the bar's
  setting.

Stalls and hitches to attribute:

- [medium] A sustained overload at the chess demo's cycle boundary
  (`[replay.scope: chess-demo RECORDED …]`): iterations run with no pacer
  wait, simulate and produce rising without a tick. Trace it across the
  demo's restore.
- [medium] The endless fall's first visit produces in pieces (the window
  feeds' first binding, `WriteOwedMeshes`, `PackDynamicTransforms`, the camera
  sample's trace), and frames block in `Present` with the GPU busy, since each
  painting draws the world. Measure GPU time per view and render each
  painting view at the resolution its projected glass demands.
- [medium] A window's glass resize allocates managed memory, so a long
  approach to a painting resizes many times and a gen2 collection pauses the
  frame. Make the resize allocation-free or step the extent with hysteresis.
- [low] Young collections: fewer young objects referenced from old containers
  (the server's `StateCell`, `WorldStateRow`, `UpsertStateRow` and their
  strings, the state journal) is what shortens a gen0 pause; server GC paused
  longer and was not kept.
- [low] A body resting at the overlook pays two ground snaps a tick:
  `WorldBody.IntegratePlanarAndVerticalVelocity` searches for support whenever
  grounded, and `ResolveProgramContacts`'s own sweep at rest does not report
  it grounded. Find whether the support normal's tilt leaves a residue the
  contact write-back keeps.
- [low] `WorldPopulation.SyncBodyCells` resyncs every body's scale, kit and
  look, resolving each register by name (`WorldBodyStateReads.TryResolveSlot`),
  whenever one body's register moves. Resync only the bodies whose lane cells
  the commit touched (`StateArena.CollectOpenLaneOrdinals`).
- [low] Every rigid advance zero-fills the step's capture snapshot
  (`WorldBody.CaptureMotion` into `CaptureIntegrationResidue`).
- [low] A reading taken while the compositor is not composing looks like a
  slow frame, and nothing flags it; a present-side signal (the frame-latency
  wait reaching its bound) would. The Linux windows hold no display request.
- [low] The hall lift ride's `host.frames` is unread: `gallery.level 0`
  stands her before the door. Pose her inside the car first.
- [low] Pacing on a high-refresh, variable-rate display under Windows'
  Dynamic Refresh Rate: confirm the bar and VRR pacing hold in front of and
  behind another window, and that `world.target display` reaches the top rate.
- [low] Dynamic resolution adapts on GPU timing only when a display period is
  known (`WorldDynamicResolution`'s update, `displayHertz > 0`), so a view with
  GPU timestamps but no display Hertz (an offscreen render, a frame streamed to
  a thin client; unverified that those pass zero) holds its grid. Give the
  controller a target period from the view's own cadence (its refresh divisor or
  the session's frame rate).

### Bake proportional to change

Owner: specialist (rendering) · high · Next: follow the five-step order below
(edit-wait measurements -> per-stage keys -> frame-sized bake jobs ->
critical-path cold bake and top speed fix -> shared cross-backend store). The
owner's ruling on waits sets the order: the wait between making a change and
seeing it breaks flow and comes first, and a cold bake is a long start that is
tolerated while it stays reasonable. The figure of about a minute per backend
from an empty store exists to stop that time drifting (bake times once grew to
nearly an hour); it is not a finish line, and it is read again at the close of
steps 1 to 4 because step 2 and the contour rule change what a bake costs. What
limits the cold bake under the recipe (quiet machine, display composing, empty
store; medians ~60.6 s on Direct3D 12 and ~64.7 s on Vulkan with ~4 s GPU work
in a 68 s span) is not yet measured — do not assume CPU-bound before step 4.
Three records disagree: one sampled trace had the workers busy 237 of about 660
thread-seconds and called the bake "paced by takes" (GPU sampling requests it
waits for); runs with the display composing beside other work read it as
processor-limited; the quiet runs of that record had the display off and the
GPU at idle clocks. None is the recipe. The contour rule (gap 7, [bake
detail](#bake-detail)) and the triangle removal ([visibility V3a](visibility.md#the-build-order))
are this order's other two parts and stand alone.
[The bake procedure](../../.claude/skills/rendering/references/mesh-bake.md#reproducing-a-cold-bake)
says how one is reproduced and profiled.

1. **Measure edit-to-running waits end to end.** Record time from saving an
   edit to seeing it in the running debug world by phase — alongside work done
   (groups rebaked, kernels recompiled, projects rebuilt) — for three edit
   kinds: `.puck` document (~15 s compile + reload tick stall), shader, and
   C#. This is the wait the owner's ruling puts first.
2. **Key each bake stage by what it reads.** The bake has three stages: GPU
   field sampling, contouring, and Land (simplifying and storing; ~34% of bake
   CPU with collapse at ~28.7%). Today `SdfMeshBakeIdentity` hashes the whole
   C# closure plus both kernel bytecodes, so one Land or contour edit forces a
   full cold bake. Build per-stage keys on `SdfMeshBakeClosure` and
   `SdfMeshStore.ExplainMisses` so a Land edit starts from stored contours
   without re-sampling on GPU (keeping each stage's key conservative over
   everything that stage reaches; check stored-contour disk/memory size before
   splitting a stage; amend
   [the derived-product decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)
   in the same change). A key too narrow is the dangerous direction: an edit
   that should have invalidated a mesh and did not shows as a stale mesh that
   nothing reports, so the split changes only which stage a part belongs to.
   Batch bake-kernel edits until stage keys land. Narrower per-operation keys
   remain a later question: they depend on whether the interpreter's source can
   be attributed to each operation, which nobody has looked at.
   - *Check:* editing Land rebakes with zero GPU sampling dispatches and lowers
     the C# edit-wait reading.
3. **Measure the cold bake under the live frame allowance.** Budgeted batches
   are cut to the allowance at a mean-following rate (`SdfMeshBakeGpuWork`);
   held frames keep `SdfMeshPathBake.BatchTaps`. Re-measure the cold bake held
   and a cold arrival in play, and split a lattice only if `over-allowance`
   shows the unsplit first lattice matters.
4. **Rank and build the top cold-bake speed fix.** After stage keys (and
   contour/triangle fixes) land, run one quiet cold bake under the recipe
   recording worker activity, GPU wait per request, GPU timestamps and total
   time together. Build whichever candidate ranks on the critical path first
   (GPU contouring is rejected as a second mesher):
   - *GPU sampling in one round trip per stage* (cutting ~20 round trips per
     group to ~4 by moving root search and gradients onto GPU with a per-stage
     sample cap): requires generating or reading `SdfSampledLattice`'s root
     search (bracket, tolerance, round limit, step) from **one single source**
     shared by CPU and GPU, verified on bracket, residual and thin-feature
     checks on both backends. Writing the root search again by hand in shader
     code would make two copies of one rule, whoever is asked to keep them
     equal. No source measures a root search on the GPU; Dreams ran a
     hierarchical GPU evaluator on a console, the nearest precedent.
   - *The Land stage on the job pool* (five changes: check the removed vertex
     first in `SdfMeshCollapse` before laying out the new fan, since the
     acceptance search makes about five attempts a collapse and `Measure`
     refuses half at the removed vertex's own samples; share one error sample
     set between `Measure` and `MeasureReverse`; restrict
     `SdfSampledLattice.CloseSheets`'s second slit pass, which re-reads every
     measured fine sample's six neighbours, to neighbours of first-pass
     closings; schedule chunks on `JobPool` ordered by visibility demand before
     size, largest first only among chunks of equal priority; move per-group
     contour allocations to per-worker storage). A cheaper collapse whose meshes
     differ but audit as well is a valid cut: judge changed witnesses with the
     field audit at each rendered level, raw millimetre errors beside threshold
     counts. Try the cheaper collapse and a different error measure (attribute
     quadrics) inside `SdfMeshCollapse` before considering a replacement
     simplifier ([visibility V3b](visibility.md#the-build-order)). Not checked:
     Kapoulkine's clustering pipeline falling from about 9 minutes to under 3
     on the same class of fixes.
   - *Check:* `puck bench startup --bake` under `puck lease quiet` reads about
     a minute or less on each backend from its own empty store, and turning
     into a new place during a bake delivers the first visible surface no
     later than before. If the sampling fix is the one that lands, a group
     takes about four round trips.
5. **One shared cross-backend store (last, judged separately from cold bake).**
   Key meshes by authored inputs rather than backend bytecode, recording which
   backend derived each entry and accepting cross-backend entries by audit
   within recorded error (never byte equality; `SdfMeshCpuField.TapSpacing`
   already differs near creases, and native forest triangle counts differ by
   backend). This is the first use of
   [shared product naming and verification](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)
   (with fetch disclosure owned by
   [disclosure](disclosure.md#presentation-and-derived-product-disclosure)).
   - *Memo and three-part prototype:* write a memo fixing (a) what goes into a
     shared product's name (source program, authored parameters, byte-format
     version), (b) attribute-by-attribute verification against the analytic
     source at receiver tolerance/coverage and inconclusive handling, (c)
     format version computed from both writer and reader code so a
     layout-preserving reader change still moves it, and (d) unified pricing of
     fetching versus local derivation (keeping local store reuse distinct from
     machine-to-machine exchange). Prototype over every stored debug-world
     group at route tiers: (i) one desktop store read by both D3D12 and Vulkan
     in both directions (verified by audit, materials, seams and motion); (ii)
     one public gallery mesh fetched from another process beside one refused
     fetch, one permitted local miss, timing cold bake vs warm read vs fetch
     on a stated link, plus rejecting tampered products (removed part, swapped
     material id, flipped normal patch) and a layout-preserving reader-change
     refusal/recheck; (iii) RTX ↔ Steam Deck cross-vendor exchange in both
     directions once Deck hardware is on hand (unmeasured until then), because
     two graphics APIs on one card compare no vendors. Parts (i) and (ii) run on
     the desktop inside the bet's time box; part (iii) has its own box. Amend
     the decision register when adopted.
   - *Judged apart from the cold bake.* The cold-bake figure is each backend's
     own bake from an empty store with floors on, timed with the other
     backend's store absent. A shared store has three hazards: the same shader
     source can compile under different options or generated interfaces;
     whichever backend writes an entry first can hide which backend derived
     what is drawn; and a warm hit from the other backend's store is not a cold
     bake. Step 2's keys are local cache keys and are not affected.
   - *What it shows in the debug world:* the second backend's first frame loads
     the first backend's meshes, which pass verification, and the fetched mesh
     is ready sooner than it bakes.

Remaining detailed findings and smaller cuts in this area:

- [high] The frame thread's `SdfWorldResidency.Capture` and the per-view
  recording while hundreds of views stand during a bake.
- [high] `CrossingEdges`' per-edge hash set, and `Refine`'s `Thickens` and slot
  numbering. The crossed-cell masks already flow from `CrossingEdges` to
  `ExtractRefined`; add no second topology scan there.
- [high] Admission: a third to a half of the bake's takes found no lattice to
  dispatch while hundreds of groups waited on the pool's backlog or on the
  memory account. The backlog is now wall time (`SdfMeshBakeBacklog`); read
  the done line's `idle` and `per-take` again, and what is left is the
  memory account's.
- [high] A cold bake's private memory climbs far past the Deck tier's 10 GB in
  its first minute, and its allocation is a frame cost too (hundreds of
  collections while it bakes). Splitting the bake into its stages needs a cold
  bake that stays under the machine's memory.
- [medium] Every view renders every frame while a bake is pending
  (`SdfWorldResidency.IsUnchanged` answers false on `MeshPathBake.Pending`),
  which the frame-paced bake needed and the background GPU work does not. Let
  a still view stand while the bake runs, uploading without rendering
  (`SdfWorldTables.BindsBackgroundWork`), and measure the frame while baking.
- [medium] Per-group allocation in the contour stages, read from source and
  unmeasured: `SdfContour.SplitAtCreases` passes a collection expression to
  `List<int>.AddRange` for every quad; `SdfSampledLattice.Contour` copies the
  contour's `double[]` normals and positions to `float[]` through LINQ after
  `SdfDualContouring` already copied its lists; every creased group builds a
  `ContourProjection` of its own. Count them with `--profile gc-verbose`, then
  write each into storage its worker owns.
- [medium] A placement whose creation is not a rigid asset (a timeline,
  drivers, effectors, field-changing lanes, cameras or faces) still opens a
  group per placement (`WorldPlacementStamper`'s `EmitPlacement`), where a
  rigid creation's static copies share one bake
  (`WorldRigidAssets.Static.cs`). Count the groups a cold
  `[sdf.mesh-bake] start` plans per unique geometry and name which placements
  still bake per copy before spending on it.
- [medium] Static bake grouping depends on spelling, against
  ergonomic-is-optimal. Design these with the item above, so grouping and
  detail derive from what the document means:
  - a static group's detail follows the whole group's size, so authors split
    one object into prototypes to keep detail (`devices.puck`, `desktop.puck`,
    `hall.puck`);
  - a warped shape bakes alone and forces the next shape alone, so shape order
    changes grouping (`WorldPlacementStamper`);
  - every text run bakes as its own group;
  - one non-union blend makes the whole creation one scope, refusing panels,
    trims and cells on every shape;
  - folds are cheaper than copies but cannot swing, slide, carry panels or
    always collide.
- [medium · unconfirmed] `SdfWorldResidency.ReconcileGlyphAtlas` swaps the
  bound atlas texture apart from the program that names its new identity
  (`SdfProgram.GlyphAtlas`), so a bake taken between the two could store old
  pixels under the new key. Confirm whether the order inside
  `SdfWorldResidency.PrepareOnce` rules it out; if not, bind both in one step.
  `WorldTextCatalog.Reconcile` also copies the whole packed image into
  `SdfGlyphAtlas`.
- [low] The bake closure reaches code that decides no content, so editing it
  moves the store identity: `WorkCounterSet`, `WorkCount` and `WorkKind`,
  `CameraSnapshot` and `SweptBox`, `CodeDerivedTable`, and a group's `Outcome`
  text literals, which only the done line reads. Make them stop types or
  values the report words (`SdfMeshBakeReport`), with a scratch-edit check
  that each no longer moves the identity; the `m_restarts`, `m_loads*` and
  `m_planMilliseconds` counters belong in `SdfMeshBakeReport.Accounts` too.
- [low] The audit counts a chunked mesh's seam edges as open edges: weld the
  joined query view's coincident seam vertices, or skip edges two chunks
  share.
- [low] Each residency's bake guarantees its own progress, so several cold
  residencies under a too-small budget each admit one group past it
  (`SdfMeshPathBake`'s `TryTake`, the "admitted alone" branch); the
  process-wide rule is the job system's J1.
- [low · unconfirmed] The bake indexes its `SdfBakeTaps[4]` by the `[loop]`
  counter (`sdf-mesh-bake.comp.hlsl`), the shape the rendering skill's
  local-array rule forbids. Read its local memory size in a Vulkan RenderDoc
  capture of a baking frame; if it spills, pick each tap's signs from bits,
  batched with the next bake-kernel change.
- [low · unconfirmed] `SdfWorldTables.SameProgramGeometry` compares the stream
  outside the shading words, so a material flipping between glass and opaque
  reads as the same geometry though it moves the draw between passes. Confirm
  with a glass flip under a live World.
- [low · unconfirmed] A group's material place list
  (`SdfMeshGeometryBuilder.Materials`) holds only the base material each shape
  names, not the ids a positional recolor reaches (`CellJitter`,
  `RepeatPolar`, `WallpaperFold` strides), so `SdfMeshLibrary.Renumber` maps
  only the listed ids. Check whether a recolored group taken from the library
  draws another palette's rows.

### Bake detail

Owner: specialist (rendering, sdf-authoring) · high, it blocks authoring ·
Next: fill the engine gaps first
([rendering research](rendering-research.md#engine-gaps-filled-first)):

1. **Contour rule for unresolvable cells (gap 7): a feature thinner than a bake
   cell is lost or faceted.** The count is in: `puck mesh audit --cpu` prints
   each failing shape's defect class (vertex off the surface at a crease,
   sliver beside a crease, face narrower than the cell, or thin tip stopping
   short) and whether its cell was flagged for finer sampling
   (`SdfSampledLattice.Refine`). The failing cells of the blend exhibits and
   nearly all of the Moth's were flagged and measured, so the fix is the
   contour rule, not a trigger. Built: a near-crease row takes the crease's
   vertices, and so does the crowd where creases meet (`SdfContour.Weld`, in
   `SdfContour.Finished`); a material boundary beside a crease vertex lies at
   the vertex (`SdfMeshMaterialCut.Boundary`), which closed most of the hard
   blends' rim folds; the audit counts a fold the field's own crease explains
   apart (a carve's rim meets a face at up to 125 degrees); and a lattice at its
   tier's floor still refines the cells it cannot resolve at all
   (`SdfMeshPathBake.FactorOf`), so the swallow's streamer tips are no longer
   lost. Still open, each read in the exhibit's own vertices: the junction
   where a groove's lips meet a box edge (`shape-groove-union`) and where a
   rounded rim fades into a sharp edge (`shape-smooth-subtraction`), where the
   field check moves solved vertices up to half a cell or refuses a kinked one;
   the Moth's shin blades (below); and closed thin sheets (the lawn's blades,
   the swallow's streamers), whose rims fold against a field the closing
   deliberately thickens. A crossing whose gradient blends two faces
   contributing no plane, and a sub-cell face closing to one crease, are the
   steps left for those.
   - *Whole-world stored-mesh audit check:* audit every stored mesh of the
     world at the tier each group was stored at on both backends (fixing the
     238-of-6544 tier-0 audit gap below). Count groups the audit cannot judge
     (glyphs and sampled regions) and unsampled groups apart from passed
     groups. Require no defect class worse than before, zero wrongly facing
     triangles on the fourteen blend exhibits, every audited group non-empty,
     `open` and `uncovered` no higher than before, blend seams and the Moth's
     rim captured in motion from failing sides, and cold-bake time reported
     beside each fix. State the audit's blind spot: it samples around the
     output mesh box, so vanished parts are caught only by authored-bounds
     deviation ([virtual geometry](virtual-geometry.md#the-build-order)).
     The fourteen exhibits are not enough because the rule reaches every
     bake: an earlier attempt that narrowed the lattice along one axis was
     worse over the whole world, and an empty or missing patch also lowers
     the wrongly-facing count. Whether the rule holds where a surface folds
     or a feature disappears is evidence Puck produces itself: no source
     covers those, or two creases in one cell (Ju and others chose their
     singular-value cut-off by experiment, which says nothing for or against
     dropping a blended gradient's plane; Schaefer and others note a manifold
     criterion "may still contain intersecting polygons"). Not checked: that
     the refinement takes the finest split that fits and is never skipped
     silently (a reviewer's reading).
2. **Non-uniform scale bounds (gap 11,** [rendering](rendering.md#bake-quality))
   and **[moving blends](moving-blends.md#engine-gaps-first) step 4 (gap 10)**
   beside it. Then the three kinds of triangle the lattice's facing does not
   judge, which the tour's remaining reds and the Moth's shin groups share
   (below).

- [medium] `SdfMeshLattice.Dims` reports two samples along a million-metre
  axis requested at 0.1 mm. The cell count saturates to `int.MaxValue` and
  adding the final endpoint wraps. Check dimensions and their product before
  forming array indices, and let the bake coarsen or partition a box that
  exceeds the sample budget.
- [medium] `SdfMeshGeometryBuilder.RelativePose` gives another slot at
  +3,000,000 m and +4,000,000 m the same key and bakes both at −2,097,152 m.
  Its rounded component shares an out-of-range sentinel with the stored
  coordinate. Carry the supported large offsets through the rounded pose,
  the key and the uploaded rows, with absence represented independently.
- [high] `puck mesh audit --cpu` still differs from the device wherever a
  lattice's gradient taps lie closer than the fixed-point evaluator resolves
  (`SdfMeshCpuField.ResolutionSpacing`, 8 × 2⁻¹⁶ m). That is every group at a
  tier finer than about 8 mm cells, which covers 178 of the Moth's 180 groups,
  each printing a `cpu taps:` line. Over those 180 groups the CPU and Direct3D 12 audits still
  disagree by 743 folds and 468 flipped triangles in sum. A judgement made with
  `--cpu` on such a group is confirmed against a store audit. Closing the gap
  needs a CPU answer of the field finer than 2⁻¹⁶ m. A narrower floor reads
  the evaluator's rounding (4 steps disagreed more than 8); the flare's loose
  bound, below, multiplies that rounding. `shape-groove-union` differs by its
  field alone (its taps match the kernel's): 32 folds and 20 flipped on the
  CPU against 47 and 14 on the device, so the two interpreters' groove-union
  arithmetic is the next pair to compare.
- [medium] `puck mesh audit` over a cold store audits only the groups stored
  at the tier it plans: over the whole debug world it read 238 of 6544
  groups and reported 6306 "with no stored mesh", although the residency
  baked 7525 meshes. A whole-world store audit needs the tier each group was
  stored at (the store records it), not tier 0 for every group.

- [medium] A small relieved union retains contour defects in both its static
  and root-moving forms: the relief1 CPU audit finds 6 folds, 2 flipped
  triangles and 16 slivers, with no open edges or uncovered crossings, in
  19,424 triangles. Reproduce with two Sphere shapes at `[-0.22, 0, 0]` and
  `[0.22, 0, 0]`, scales `[0.42, 0.32, 0.38]` and `[0.4, 0.36, 0.32]`, the
  second joining by SmoothUnion of radius 0.12, and creation noise frequency
  2, amplitude 0.12, octaves 3, seed 11. The static and moving audits agree
  in counted work and error; carrying the relief did not introduce these
  defects. Inspect the contour's narrow triangles before changing its detail.
- [high] The walkthrough tour is still red on its mesh audits:
  `puck mesh audit --cpu --prototype <id>` (`SweepMeshAudit`) finds flipped
  triangles on six of the fourteen blend exhibits (`shape-groove-union` and
  `shape-blend-subtraction` worst) and on none of the primitives. Those counts
  came from CPU taps wider than the kernel's; on the exhibits' tiers the CPU
  now places the kernel's taps, which turned `shape-chamfer-union` from 6 folds
  and 2 flipped to 0 and 0, as on the device, so the six are read again first.
  Every quad
  of those contours faces out as its lattice edge says
  ([the contour's facing](../../.claude/skills/rendering/references/mesh-bake.md#contouring)),
  so what remains is geometry the lattice's facts do not judge, in three
  kinds, each a step of its own:
  - A vertex its crossings' planes put millimetres off the surface where the
    field check refuses to move it (`SdfMeshPathBake.ProjectCrease`: a kinked
    gradient, or one turned from its triangles): the fins where
    `shape-groove-union`'s groove ends against the ball. Along that exhibit's
    rims a cell's crossings within a gradient tap of the crease read a blend
    of the two faces, and the cell's point is drawn off its own surface;
    whether the fins are the same planes is not traced. A crossing whose
    gradient is a blend should give the cell no plane, and a vertex the check
    cannot move should return to its crossings' mean. Check: the exhibit's
    `off surface` and `turned` counts.
  - A sliver between two rows of vertices a few hundredths of a cell apart
    along a crease, and a web across the junction of two creases (a carve's
    rim meeting a box edge): triangles a degree or two past edge-on whose
    facing is their vertices' rounding. A row the lattice puts that near a
    crease should take the crease's own vertices (one row, no strip). Check:
    `flipped` on `shape-blend-subtraction` and `shape-smooth-subtraction`.
  - A face narrower than the cell between two creases, below.
  The thresholds (`folds > 0` amber, `flipped > 0` red) are right and are not
  loosened. A per-axis narrowing was worse over the whole world, and moving
  the lattice off a face only moves which exhibit is red.
- [high] The Moth's shin blend groups (`shin-*`, `shinCavity-*`, `archBand-*`,
  `archBandHollow-*`, `archBandBack-*`, `sidePanel-*`) carry seven tenths of
  her flipped triangles in two groups, nearly all under a square millimetre
  and strung along the blades' edges: each blade tapers to a rim thinner than
  the 2 mm floor, two creases in one cell, which one vertex a patch cannot
  hold. `blinkCrease-*` and `nozzleGlow-*` are next on her, and
  `range-window` and the Humble Brick's face are the worst of a wider sample;
  none of those four is examined. The contour needs a rule for a face it cannot
  resolve (close it to one crease, as `SdfSampledLattice.CloseSheets` closes a
  sheet, or refine it), and the non-manifold edges on the shins' soles sit on
  the same edges. The one detail rule exposes the defect and does not cause
  it.
- [high] [Moving blends](moving-blends.md) steps 4 and 5: paints, insets and
  lanes across a blended seam, then the quiet-window cost of a skinned
  close-up and a crowd on both backends.
- [medium] The curvature gate refines sharp creases as turns (toes and soles
  about twice over): the samples cannot tell a crease from a fillet narrower
  than a cell. A measured cell whose fine samples are two affine faces takes
  its split as its contour stride
  ([the memo's step 4](bake-demand-tiers.md#steps-and-checks)).
- [high] A rim thinner than a group's final cell is lost (`range-window`'s
  2 cm rim): a declared feature narrower than the cell must refine or be
  reported. This is the engine gap above (gap 7), with the tapered tip of the
  tapered-tip item below it.
- [medium] Glyphs have no CPU reference: `SdfFieldEvaluator` has no
  interpreter for the `Glyph` shape, so `puck mesh audit --cpu` and
  `puck mesh preview` bake a plaque's text empty and no audit covers text. Add
  the interpreter as `sdfGlyph` reads it, then audit a plaque.
- [medium · unconfirmed whether new] The Humble Brick's face at close range
  shows thin red lines across the screen bezel and a ragged d-pad outline,
  alike on both backends. Look again, then audit the device's groups.
- [medium] Blend groups on the static union's lattice more than doubled the
  Moth's pooled bake. Measure her frame cost on the bar devices; if it misses
  the bar, coarsen at the cause (the union's base spacing, or the level
  budget), never by returning blend groups to the 15 mm floor.
- [medium] A carve that moves against the surface it cuts keeps its rest cut
  in both fallbacks (`CreationMotionPlan.Borrows`) with no diagnostic. PUCK122
  could name a cross-owner carve in a partitioned group as it names a creased
  blend.
- [medium] A flare's field bound is loose: `SdfProgramBuilder.AxialProfile`
  builds in `1/maxS` and `SdfProgram.FlareOperatorNorm` bounds the shear with
  the chain's whole reach, so a flared primitive's slope reads about 0.22,
  which multiplies the CPU twin's rounding and makes the bake refine more than
  it needs. The bound must stay sound.
- [medium] Contour folds a four-probe scan finds: a thin cone at its apex, a
  thin `ChamferedRectangle` prism, and a flared cylinder's top rim with open
  edges.
- [low] A tier between a group's narrowed final cell and its planned one is a
  second key for one mesh (`SdfMeshBakeGroup.CoarsestTier` reads the planned
  box). Measure how many such entries a tour writes before tightening.
- [high] A tapered tip thinner than about 1.5 bake cells does not bake, so a
  small creation cannot have a needle point (`WorldShapeDetail.FinestDetail`);
  a finer floor for small creations needs its cost measured.
- [low] No console command names a bake's pending groups or maps a slot to its
  creation, so a bake that does not settle is found by reading stderr.

### Residency and streaming

Owner: rendering lane (rendering, verification) · high · Next:
[the streaming and memory plan](streaming-and-memory.md#the-build-order)'s
step 5, the floor, which is an engine gap
([rendering research](rendering-research.md#engine-gaps-filled-first)) and goes
first; then step 0's baseline legs (`puck benchmark --route seams`, both backends, a
quiet window), steps 1,
2 and 3's windowed readings and step 7's remaining implementation after its
contiguous fallback, shared retirement timeline, destination page proofs,
native-pool pressure trim, Vulkan's ordered binding door and Direct3D 12's
mapped regions (`IGpuMappedBuffer`, `DirectXGpuMappedBuffer`: the mesh region
and the probe scene back 1 MiB chunks of one reserved buffer, grow without a
second buffer and give back chunks no placement covers): Vulkan sparse
resource ownership behind the same interface, page-only reservation credit
with pool-owned slack, and physical compaction. Vulkan mesh regions still use
contiguous storage.
The remaining indexed-read prototype window uses frozen `e9c1d485c9`;
current hardware qualification reads the 4070, 2060 and Deck, then
[what step 3 left](streaming-and-memory.md#step-3-one-reservation). The plan
owns the decisions, the build order
and each step's check, on [the residency decision](../decisions/residency.md):
the residency wait, background GPU work's budget, the region's budget, staging
and every memory owner are its steps. This packet holds the defects beside it.

- [high] Measure the session-residency fix on the RTX 4070. A cold debug-world
  boot built 55 residencies, one for each `self`, `wonderland` and nested
  session screen of the boot world, and the device pool held 15.5 GB, mostly
  the sessions' full-resolution cascades. Two causes are fixed: sessions whose
  scene proofs carry one identity now share one scene
  (`WorldFramePresenter.AttachSharedWindow`), and a session's own view takes
  its `render.nested` quality (`WorldSessionSceneEmitter.ViewName`). The
  boot sessions cannot share the boot world's own residency: the shedding
  table deals its faces from `sheddingCards`, which withholds from them.
  `WorldViewGraphHost.ReserveArrivals` keeps one window rendering for each
  arriving residency, which is right once windows share. Still to read: the
  residency count, the device pool's held and peak bytes, and the cascade
  families at the hero spawn and in the painting wing.
- [high] On Direct3D 12 the debug world's mesh region refused meshes instead of
  coarsening, so the valley's lake, the glacier's ice and the falls did not draw
  there. The causes are fixed at their source but not yet seen in a windowed run:
  the bake's budget now never passes the limit the region's grant reserves
  (`SdfResidencySelector.Budget`, `SdfResidencyGrant.LimitBytes`), a refused mesh
  presses the detail bias on its own (`SdfMeshPathBake.Retier`), and any
  residency's pressure raises the one bias, not only the largest one's. The
  check is a settled Direct3D 12 debug world reading `region-refused=0` with a
  raised `detail-bias` in `world.residency`, and the lake drawn from the
  `glacier` place.
- [medium] Before sparse external targets are used, retain the exact host-word
  coverage of disjoint writes across remapping. `GpuRegion.StageOwed`
  (`src/Puck.Abstractions/Gpu/Residency/GpuRegion.cs`) re-owes the known prefix;
  an external region can also write beyond that prefix. Its consumed disjoint
  words currently need its publication owner to re-owe them. Step 7 owns the
  coverage implementation and a nonzero-target, disjoint-write remapping law.
- [low] `GpuBufferPages.Lease.IsCurrent`
  (`src/Puck.Abstractions/Gpu/Residency/GpuBufferPages.cs:138`) scans a lease's
  pages after an unrelated global revision on every later check. Step 7 owns
  revision-aware retained-proof validation before sparse churn is enabled.

- [medium] Background GPU work is serviced inside the host's timer wait and
  not inside the swap chain's own (`ISwapChainPacing.TryWaitForFrame`) or a
  present that blocks in the driver, so a Direct3D 12 host the swap chain
  paces at two to four refreshes a present steps it at its pumps alone, and
  the serialized lighting takes a job a frame there (Vulkan paces those
  targets by the timer, which is serviced). The control plane's C0.6 puts the
  Direct3D 12 frame latency waitable on the door. Step 3's slow-present
  reading at `world.target 20` says how much is lost.
- [medium] The lighting's device memory (the trace scene's region, the
  feedback volume, the hit and meter caches of `SdfMeshProbeGpuWork`) is still
  fixed storage with no reservation; only a job's slot buffers reserve
  (`SdfGpuWorkHold`).
- [low] A mesh region's staging is named for its own owner
  (`SdfResidencySelector.RegionStagingOwner`) and states no class, so a flush
  that stages more than the pool has room for is admitted past the limit and
  counted. Reserve the staging with the flush that sizes it, so the elastic
  budget leaves room for it on unified memory.
- [low] A mesh store read that finds no workspace within
  `SdfMeshStore.WorkspacePatience` is a miss. A native mesh restoring its
  parts (`SdfMesh`'s restore) then ends lost and bakes its groups again; it
  could wait as a deferred job does instead.
- [low] A mesh region whose draw records pass the buffer it holds grows for
  them whatever the pool holds (`SdfResidencyGrant.ReserveFloorGrowth`), and
  the meshes the old buffer could not hold are refused until a later plan. If
  the device itself refuses that growth the region throws
  (`SdfWorldTables.EnsureMeshRegionCapacity`): the draw records are the floor
  and have no smaller form.
- [medium] A fixed reservation (a shader pipeline's replacement) is refused
  when its pool is full, and elastic storage does not yet give way for it: a
  resize or reload at a full pool keeps the installed graph
  (`SHADERPIPE_BUDGET`) until the bias has made room. The plan's step 5 is
  what gives way.
- [low] A test schedule admits no authored command (`WorldScheduleCommands`,
  `WorldSchedule`). Admit authored commands in schedules under the step's own
  principal.
- [medium] The `seams` route walks no free flight yet (its fastest leg is the
  run with the dash), and a benchmark run does not drive the mixed-sound
  recording `capture.start` offers.
- `world.instance.stop` on a routed world leaves that world's per-view
  residencies (`session$N$2`) in `world.residency`, and at the meadow stop the
  garden painting's windows stay scheduled as `waiting` for hundreds of ticks.
  Whether either is intended is not confirmed.
- `world.residency`'s per-group `resident` count and `all-resident` stamp are
  not reset by a device loss; its region line's `owed` is the truth.
- [unconfirmed] The pane-mapping law the plan's step 4 deletes fails because
  its fake host's boot session is never reached from the walk's seeds. Check
  whether an authored root leaves a world's views unseeded in a real World
  too.
- A world field's height-field reference (`SdfHeightField.Publish`, from
  `WorldFieldEmitter`) keys a new mesh whenever a column turns wet or dry, so
  the debug world's water bakes or loads one static group again and again as
  it settles, each a store miss on its first run. Whether a reference should
  hold every column that can ever raise, with displacement closing the dry
  ones, is a design question for the field's owner.
- One group in the debug world has a mesh no draw names, not yet identified.
- [medium] Step 2's heap walk on a headless debug world names two large-object
  tenants the plan's [large host owners](streaming-and-memory.md#the-large-host-owners)
  table bounds by no step: `FieldCellDelta[]` arrays held by
  `WorldDeliveryRows` (9 objects, 33 MB) and by `WorldDocument` (3.7 MB). They
  need an owner and a bound, or a reason they stay named only.
- [low] Step 2 names the trace scene's current hierarchy only: a scene a job
  still borrows after a rebuild sits in the managed row's residual until its
  last claim ends (`SdfMeshIrradiance`'s sampled owners).
- A hand-over's draw-list rebuild is
  [arrival work proportional to change](arrival-proportional.md)'s, filed
  under [frame-time findings](#other-frame-time-findings); the capture feed's
  idle cost is [media](#media-and-capture)'s.

Lifecycle lows, each argued from source and not run:

- [low] The scene upload's `Dispose` swallows faulted export work
  (`SdfMeshProbeGpuWork.SceneUpload.Dispose`). Count faulted work on cancel.
- [low · plausible] A probe job cancelled by the admission timeout never reads
  as stale-scene evidence (`SdfMeshProbeGpuWork`'s `AdmissionWait`, with
  `m_traceOwed` left set), so with an unplaced mesh every query is cancelled
  in turn and the scene cannot rebuild while the bake is pending. Count the
  timeout as stale-scene evidence.

#### Memory on the Deck tier

The memory bar and every owner under it are the plan's: the bar is read by
[its recipe](streaming-and-memory.md#how-the-bars-are-read), and
[what adds and what only names](streaming-and-memory.md#what-adds-and-what-only-names)
says how a reading is taken. The tour's memory peak at the garden crossing is
in [sweep](#sweep-and-visual-checks).

### Lighting and GI

Owner: next lighting batch (rendering, principal-grade review) · high · Next:
[what the GPU trace still owes](illumination.md#after-the-gpu-trace), the
plan's step 3: the moving-sun lockstep drain and the outdoor difference from
the CPU reference; then a cold area lit from the coarser levels or the sky
term the moment a view arrives.
[The plan](illumination.md#the-build-order) owns the steps, and the
[mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md)
holds the fixed poses and the approaches already rejected.

The volume's cost and convergence:

- [medium] Faster probe convergence costs host time while the volume works.
  With several probe jobs in flight, one commit a batch and placement trials
  sharing rounds (`SdfMeshProbeGpuWork.Run`, `SdfMeshIrradiance.PlaceBatch`),
  the worker now reaches the authored probe budget instead of waiting on round
  trips, and its threads (`SdfMeshIrradiance`'s `BackgroundWorkers`) and the
  frame thread's job recording compete with the simulation and the frame while
  it does. On one loaded machine, the valley chapter of the `debug` benchmark
  route ran with a cold window at a simulate median of 8.5 ms and a produce
  median of 17.8 ms with indirect light on, against 5.4 ms and 13.9 ms with it
  off, and the same chapter warm showed no difference. Next: in a quiet window,
  run `puck benchmark --route debug --backend directx vulkan` on the build
  before the probe-jobs change and on the head, then the head again with
  `world.indirect off`, and read the simulate and produce medians per chapter
  beside the `[sdf.mesh-gi] done` seconds; if the cost holds, find where the
  host time goes (a .NET trace of the worker and the frame thread's `Record`
  and `TryTake`, which walks the scene's draws each take through
  `SdfMeshIrradianceScene.HasGpuPlacements`) before bounding the worker.
- [high] A view-driven retier relights the whole window: a landed retier is a
  geometry change, every probe's local cube starts again
  (`SdfMeshIrradiance`'s `reuseLocal`), and a World that converges its
  lighting owes a drain per retier wave and per step of the day's lighting
  grid; the walkthrough tour pays it at every exhibit. It also holds back the
  first fill after a boot: while tiers land the main residency's static scene
  rebuilds every few seconds, its probes' traces barely advance, and its finest
  level publishes minutes late, so the gallery hall reads its camera sample's
  light (`sdfMeshIndirectSeen`) until then. The streaming plan's
  step 9 decides which tier the lighting traces; the tour's drains per still
  on the `[sdf.mesh-gi] done` lines measure whichever lands.
- [medium] A held frame's hold stops at the instance. A view withheld for its
  lighting records no pass (`IRenderGraphPackageFactory.WithheldOf`), but an
  instance that reads it is only marked stale and still records its passes,
  and a residency no scheduled render reads is not driven (`SdfGpuWork.Drive`
  runs from a view's staging). Propagate the hold to the consumers the
  schedule shows, and drive every residency that owes lighting.
- A lead for the plan's outdoor attribution:
  `SdfMeshIrradiance.DescribeRelightReference`'s next-bounce discrepancy shows
  at `world.probe -3 1 -5 0 1 0` after the fixed outdoor visit with the day
  held; separate retraced hit differences, shadow rays and the CPU and GPU
  feedback gathers before naming a cause.
- [medium] A camera sample is traced and relit on the frame thread, under the
  volume's gate, whenever the lighting version advances
  (`SdfMeshIrradiance.CameraRadiance`), outside the relight budget: always
  with no device trace, and on a device until its first meter answers. Move
  them into the worker's batches (`camera-relights` counts them).
- [medium · duplicate] Each lighting capture renders a 64×64 sky map on the
  frame thread with `SdfSkyEnvironment.Render`, the CPU reference of a GPU
  kernel. Read the GPU kernel's map, leaving one implementation.
- [medium] GPU probe work duplicates memory and copies: the feedback region
  keeps a CPU copy as large as the volume (`trace-feedback-shadow-bytes`)
  beside the volume's own words (`SdfMeshProbeGpuWork.TryTake`);
  `RecordCommit` records one buffer copy per probe where one scatter dispatch
  would do; `SdfMeshProbeGpuWork.Record` reads back
  `SdfMeshProbeRelight.ResultWords` while `SdfMeshIrradiance.RelightGpu`
  decodes only the `SdfMeshProbeRelight.OutputWords` prefix, and
  `SdfMeshProbeGpuWork.Complete` allocates the full readback and copies it
  again to strip the assertion word. Carry an explicit logical length, and
  keep the readback token's fence ownership through commit.
- [medium] The probe scene's capacity keeps its high-water mark:
  `SdfMeshIrradianceScene.GpuArena` never lowers its word count and
  `RecordSceneUpload` only grows the region. Reclaim capacity after a large
  scene leaves, without moving a published root or storage an in-flight
  submission reads. The arena and `SdfMeshRegionArena` both own keyed
  placement over shared free ranges: unify them, keeping their payloads and
  publication lifetimes.
- [medium] Full relight, selection, metering and hit-cache paths need CPU and
  GPU parity coverage; the trace law covers miss-only directional visibility
  and the gather law receiver lookup.
- [low] A teleport brings cells relit long ago back into the window, and the
  next cycle prints `behind` with their age, which reads as overload.
- [medium · unmeasured] The debug world's night floor (EV 2, keyed on `day`)
  lengthens every point and spot light's range by about five stops, day and
  night, since ranges follow the darkest exposure any key reaches
  (`WorldRenderExposure.Resolve`). Size ranges by the live floor if the cost
  shows.

What the light gets wrong:

- [high] Emissive faces light the world only where a probe's fixed rays
  happen to meet them. The gallery hall is lit mostly by its 0.4 m ceiling
  strips (`hallStrips`), which subtend less than a probe texel, so
  neighbouring probes catch a strip or miss it and the floor shows rows of
  dark and bright blotches; point lights in the strips' places light the same
  floor smoothly. Integrate an emitter over its solid angle: static emissive
  faces as area sources in the relight's local part and, for their near
  field, in the views pass. A design lane with review; it owns the interiors'
  floor blotches and the stair treads' stains where `world.probe` names an
  emitter.
- [high] The wrapped side's self-shadow term has failed three reviews, each
  in a new mesh-topology case (grazing self-shadowing from the receiver-plane
  clamp, multi-shell meshes oriented by one signed volume, T-junctions
  demoting a solid to a thin caster, skins whose deformation inverts winding).
  [Transmission](transmission.md) replaces it, and these close with it: the
  cascades' exit layer doubles each cascade pass's draws and adds a depth
  image per cascade; at a grazing light the filter's slope clearance
  (`SdfMeshShadowSlopeBias`) clears a parallel blocker within a few texels;
  a body clipped at a portal's glass or drawn under a mirror is a thin caster
  (`SdfMeshDraw.EnclosesSolid`), shadowed by its own lit side.
- [high] Night, from the owner's walkthrough at the lawn's trees:
  - The jump to night is wrong: forcing midnight leaves the scene near-black
    for seconds while the canopies glow green from inside, since daytime
    bounce stays in the probes while exposure adapts. From the overlook the
    valley floor still reads blown white at the EV 2 floor after a minute
    while `[sdf.mesh-gi] behind` reports a relight shortfall; a layout reset
    brings the moonlit blue at once.
  - The settled night reads as a dim overcast day. The night look is an
    authored standard style (exposure curve, low-light desaturation and blue
    shift, the moon's colour and strength), never an engine default; the
    night exposure is the lead's call.
  - A brief bright glow around 20:00, at one moment and angle, has not been
    captured; the same burst shows near-whole-frame changes about every 2.4 s
    at small mean luma, probably stepped relighting or exposure.
- [medium] The outdoor lighting has no direction: a pale grey-blue sky, a
  white sun key, hard black shadows with no sky fill, no rim and no aerial
  haze, and at noon the meadow lies in the east wall's shadow. A
  lighting-direction pass with the owner; the
  [terrain pass](terrain-pass.md) covers materials, haze and far shadows.
- [medium] A concave carving does not read as a hollow
  (`shape-blend-subtraction`'s bowl): 2 m probes cannot resolve a 0.6 m cavity
  and screen-space AO darkens it only mildly. The fix is a cavity term (baked
  AO or bent normals). Soft wedges also remain behind furnishing edges where a
  point probe is truly blocked; a finite-extent probe is the design to try.
- [medium] The debug world's light switch is a geometry change:
  `gallery.lights.all` swaps the hall's dark prototypes for lit ones, so it
  retraces probes. The relight reads a lane-driven emissive as zero
  (`SdfMeshIrradiance.Shade`); read the lane's value there, then author the
  switch as a lane.
- [medium] A surface no published probe answers is lit by the view's camera
  sample (`sdfMeshAnswers`). Re-confirm the far-east level-2 view at
  (47.6, 9.1, 23.3) and the valley summit on both backends after a `debug.go`
  cut; its reflection still takes the open share and the sample's grey mean.
- [medium] After bake-scale, member normals stay unit length while root
  normals take the inverse scale, so a triangle spanning both interpolates a
  lighting that changes with body scale (`sdf-mesh.hlsli`): the likely cause
  of thin bright lines under a quarter-size Moth's back band plates. Normalize
  both after the scale. Repro: `body.pose 6.16 0.07 30.43 23 0 0` at scale
  0.25 with `view.override camera look 6.40 0.30 31.05 6.16 0.24 30.43`.
  Other candidates are shading lengths in world units that do not follow a
  body's scale.
- [medium] The stairwell's flight B enclosure reads black on its side walls in
  `indirect` while treads and landings are lit: `world.probe 12.3 3 37 1 0 0`
  answers from a valid probe whose cubes read far darker than across the
  divider. More bounces or a finer lattice there.
- [medium] The Vulkan stairwell lit and indirect pair has a sharp dark bow-tie
  patch on a middle tread, absent in its baseline; isolate geometry and the
  moving bake from probe interpolation. The Vulkan sweep also flags the
  cumulative moving-bake cap counter rising, cause unisolated.
- [medium] The coarse probe levels' far field, beside the plan's
  coarse-level visibility step: the level table is engine data
  (`SdfMeshIrradiance.LevelsOf`) a world cannot choose beside
  `render.indirect`'s tier, and coarse surface maps may want higher angular
  resolution if an atrium taller than its probe planes still seams. Re-check
  the far-wall stops (`finding-teal-roof`, `gallery-entrance`, `devices`,
  `desktop`, `rate-range`, `camera-course`, `stage`, `jump`, `flight`,
  `noclip`, the garden return portal).
- [low] At a camera cut the exposure snaps to the first image rendered after
  it, shaded with the previous place's held `airLight`, so walking indoors
  gives a short dim transient. Snap only after a fresh camera sample
  (`SdfViewExposures.Meter`), bounded so a missing sample cannot hold it.
- [low · one reading] Auto-exposure's first indoor stop after the lawn settled
  late on Direct3D 12 and not on Vulkan. Repeat it before treating it as a
  defect of `SdfExposure.Meter`.
- [low] The camera sample's open-sky share (`airLight`) reads about 0.42 on
  the open lawn at noon where `SDF_MESH_IRRADIANCE_OPEN_GROUND_SHARE` means it
  to read one; check the probes-source path's per-axis sky shares.
- [low · unconfirmed] `render.indirect.bounces` is an on/off switch: bounce
  converges toward the multi-bounce limit, so interiors lit mostly by bounce
  may read brighter than intended and a switched-off lamp's bounce fades over
  the sweeps.
- Indoors a cel style tints bounced light as its shadow band, and
  `world.debug-view shadow` paints unqueried pixels white like lit ones.
- The day cycle's hold is `world.row.set state day advance …`, which no
  authored command may run. A rule effect that writes a row's advance rate
  would make it a `commands` row or a binding.

Artefacts still to isolate:

- The owner's depth-visibility poses in `indirect`: at
  `body.pose 66.24 4.57 14.73 329` ceiling hotspots remain and the wall clips
  white; at `body.pose 58.17 4.57 30.87 180` hard diamond and star patterns
  remain around the NOISE and ERODE plinths.
- Settlement is configured sweeps, not history-independent convergence. Open:
  a general refusal of lighting captures before every bake and the irradiance
  have settled; grazing marks at the Moth backdrop and the boardroom jamb feet
  (re-read them after the finest probes' blocker-evidence rule, which closed the
  teal room's wall foot and vertical corner); bright bands in the painting wing and
  camera-bay roof; curved bands on the level-3 stairwell wall; the garden
  portal's dark horizontal strip; the REVOLVED floor's diagonal patches and
  black rectangle; the wing's north-east corner remnants.
- The SPIN exhibit (`gallery.go motion-spin`): a dotted bright seam at the
  level 3 ceiling and wall junction shows in beauty but not in `indirect`,
  `normals` or `shadow`, with bright lines at the wall's foot and the blinds'
  left edges behind it, on both backends. Geometry and direct light are next.
- The black lift and hanging-sign interiors the owner reported do not
  reproduce on settled checks; reproduce them with the original camera and
  lighting state.
- The plinths' box edges read about 45 degrees of shading error in the GPU
  bake while `puck mesh audit --cpu` scores them clean: a cluster of 32-degree
  shading normals along the bottom back edge of `--group "shape-box ("`.
- Decide (the lead's call): the coat layer's F0 semantic, consistent with the
  material model; and whether baked static copies occupy SDF instances at all.
- Depth visibility's bounded depth work and per-tier probe table sizes await a
  home in the mesh-GI reference.

### Vulkan regressions and GPU robustness

Owner: rendering (GPU leg, the lead scheduling the GPU) · medium · Next: one
draw identity, which visibility's V1 and V2 build on; then Vulkan's crossing
frame and the pixel-pass gap at locked clocks.

- [medium, before V1] Draw identity is tracked twice and the occlusion visible
  bit uses neither: a per-mesh identity counter in `SdfMesh` (read in
  `sdf-mesh-selection.hlsli`) beside the residency's per-index identity for
  motion history (`SdfWorldTables.MeshRegion.cs`), while
  `sdf-mesh-occlusion.comp.hlsl` checks none. A renumbering at the meadow (an
  inserted draw, or the landing subset) hands each blade a neighbour's level
  and visible bit: a cost spike and a pop. One per-record generation checked
  for both; V1 swaps it for instance identity.
- [medium] The bake's output differs by backend: from empty stores each
  backend repeats its own sample and triangle counts, and Vulkan's differ
  from Direct3D 12's by a small fraction. Find it with the two backends'
  `[sdf.mesh-bake] slow` and per-group counts; a bake whose geometry depends
  on the backend is a defect.
- [medium] A Vulkan cold bake once lost the device on the first frame a nested
  session view drew a glass draw (a page fault in `glass-shade`), and no
  longer reproduces after the glass skip and the full-allocation clear, so the
  cause is unnamed. A loss now lists the objects bound at the fault address
  (`VulkanDeviceAddressBindings`): fix the object the next report names, and
  close this after a further batch of cold bakes stays clean.
- [medium · unconfirmed] The World descriptor set is one per ring slot,
  rewritten in place when the binding revision moves
  (`SdfWorldTables.WorldSet`), on the argument that no revision changes
  mid-frame; `ScreenContent` bumps the revision in two places not shown to run
  only between frames. Prove it or move the bump to the frame boundary.
- [medium · unconfirmed] Vulkan's GI trace fell behind on a running debug
  World (`[sdf.mesh-gi] behind`, relit far under needed) and printed no `done`
  after minutes. Compare base and tip on Vulkan before calling it a
  regression.
- [medium] On Vulkan a crossing frame costs several times Direct3D 12's: tens
  of milliseconds of produce, the view nodes' first submissions after a
  reconfiguration blocking in `vkQueueSubmit`. Steady Vulkan frames also
  alternate long and short around the mean, the pacer waiting a full period
  after a short produce.
- [medium] Vulkan's pixel passes run about a quarter slower than Direct3D 12's
  on the RTX 2060 at live clocks and its compute passes more, with no clock
  state read; at locked clocks on the RTX 4070 no gap shows. Re-measure the
  2060 at locked clocks or by replay before treating the gap as real.
- Settled parity in the lift shaft: a cold Vulkan shaft is near-black with no
  meter until its static draws and the GI complete, and its meter then reads
  well below Direct3D 12's at the same EV100 while other geometry bakes.
  Repeat with all geometry settled, and verify the whole gallery on both.
- A cold-bake windowed Vulkan boot allocates heavily, with collections
  pausing the frame thread for seconds in all. Re-read it quiet; if it holds,
  find what a cold bake allocates.
- Vulkan's board-integrated boot: the patrol reports dozens of stalled and
  hitched points, worst near a second at (49,25), where the pre-board patrol
  had a handful. Cause unconfirmed.
- Harden the Direct3D 12 binding and bring-up paths around device removal
  (none shown to have fired): descriptor writes
  (`DirectXGpuBindings.WriteBuffer`, `WriteSampler`, `WriteStorageImage`,
  `ViewSlot`) and `AllocateSet` write into a pool's heap addresses without
  checking the heaps are still the device's, so a write between
  `DirectXDeviceContext.Recreate` and an owner's `OnDeviceLost` lands in
  released memory; `DirectXDeviceContext.EnsureCreated` brings a device up
  without a lock; pipeline, pool, set and command-buffer tokens are bare
  `GCHandle` values with no generation (`DirectXImageViews` shows the checked
  form).
- [low] The draw path's lows: the box cull exists in C# (`SdfMeshCull.Keeps`)
  and HLSL with nothing keeping them equal; Vulkan never checks
  `maxDrawIndirectCount` (`VulkanGpuRecorder`), so a world past 65,535 draws
  breaks on a spec-minimum driver; the single-slot Vulkan fallback grows CPU
  recording with candidates and hard-codes the mesh push stages in the generic
  backend; the wave-sum proof assumes 64 lanes; per-geometry facts are copied
  into every draw record and the level count is stored twice; a mesh with 256
  or more levels throws at staging instead of being refused where it is built;
  stale comments in `SdfMeshOcclusion.cs`, `SdfWorldPassRecorder.cs`, `SdfMesh.cs`,
  `sdf-mesh-shadow.vert.hlsl` and `IGpuRecorder.cs`; and, unconfirmed, a
  discarded recording that grew history leaves it uninitialized.
- [low] `ShaderPipelineRenderNode`'s frame recording is several machines with
  no one owner. A `FrameJob` owning the recording, the cadence checkpoint, the
  copy recording, the publication and the error generation fixes these
  together: `LastRecordError` is sticky across a device loss, so a waiting
  frame reads as refused and armed captures are refused; the published layout
  and binding are assigned while recording, so a paused step that fails leaves
  captures reading the ring image in the wrong layout; `AbandonRecording`
  disposes the old command pool before creating its replacement.
- [low] `VulkanImportedWritableImage.BeginWrite` submits the queue family
  acquire through the queue door every frame, splitting the frame batch.
  Defer the acquire into the batch. Vulkan also counts four more host-visible
  uploads on every pass than Direct3D 12 in `puck counters`; check that
  against this acquire first.
- [low] The drain split is attributed to different cells on each backend:
  Direct3D 12's `WaitIdle` splits through the queue-handle read with cause
  `QueueDoor`, Vulkan's with `GpuQueueCause.Drain`. Use one form.
- [low] The Vulkan pipeline-statistics readout reads the logical device before
  checking that naming is on (`VulkanGpuPipelineFactory`); move the guard
  first.
- [low · unconfirmed] `SdfMeshProbeGpuWork.Scene.cs` fills a merged upload gap
  with the new export's view of the words, so words only the old published
  scene owns would be zeroed if that upload were cancelled.
- Full GPU-based validation at the standard stops after the Direct3D 12
  caller-stated layouts and batched submission: the attempted run did not
  complete.

### Author shaders

Owner: rendering · ordinary · Next: [the memo](author-shaders.md#the-remaining-steps)'s
step 2 legs on the RTX 2060 and the Deck, then step 5, the work bound
calibrated and the device-loss quarantine, then step 6, the engine's layers
migrated to standard programs.

- [medium · unconfirmed] One authored program edit compiles two sets:
  `shaders.compiler.runs.dxc` rose by 4 per edit on both backends where views
  and glass make 2, likely a requested set overtaken while the active and
  declared sets move on different frames.
- [low] A Direct3D 12 player without `dxcompiler.dll` refuses a prewarmed
  kernel at the reflection check (`SdfShadePipelines.BuildAsync`), and a frame
  holding a program its document does not declare requests a set the prewarm
  did not compile.
- [low] `SdfWorldPipelines.Acquire` leases `ViewsPlain` and `GlassPlain` up
  front beside the interpreter pair, so every boot builds both pairs while a
  world uses one. Making the pair a demand (`SdfWorldPipelines.AcquiredOnDemand`)
  needs the frame that first lacks or gains a program to wait on its pipeline
  without a stall.
- [low] Extracting `bloomSelected` added 48 alloca stores to `place.comp`'s
  DXIL with identical arithmetic; a GPU timing of `place` shows whether the
  driver removes them.

### Temporal shimmer and cadence flicker

Owner: integration rendering follow-up · medium · Next: re-measure the
off-mode blink with the display on at a quiet cadence, refresh holds active,
on both backends, once the frame time allows the refresh cadence.

- [medium] The off-mode sill burst once showed two paired flashes about 11
  presentations apart, and has not reproduced since the shaft lintels were
  moved off the slab top; no run has qualified a cadence refresh
  (`shadow-far.held=0`). Region 0.36 0.78 0.32 0.1;
  `body.pose 16.15 13.57 37.28 191 0 0`;
  `view.override camera look 15.508 16.895 33.842 16.155 14.57 37.279 55`;
  lift held open at level 3. No far-shadow, GI or screen refresh is proved
  responsible.
- On-mode still changes a fraction of a percent of pixels at the packet's
  poses, not exactly zero.

### Depth of field

Owner: rendering, after a research round · ordinary, the owner's go-ahead ·
Next: the research round, once the frame bar is known, since it adds a
full-screen cost and decides how a post pass works.

- Puck has none. Weigh the separable complex-kernel circular bokeh
  ([Garcia, GDC 2018](https://media.gdcvault.com/gdc2018/presentations/Garcia_Kleber_CircularDepthOf.pdf))
  against gather with tile dilation, with near and far layers handled
  correctly (the simplified complex-kernel form blurs with one radius per
  pixel and lets sharp foreground bleed), and a Puck-native option: defocus
  traced from the field's exact depth where a view renders the field.
- It is a mechanism, not a look: focus distance and aperture are
  camera-program operations, off by default, opted into by the standard
  package or an author.
- Proving cases in the debug world: the avatar studio's photo area (a focus
  pull on the subject), Wonderland (shallow focus as the scale cue), and
  portals and screens (focus marking the frame or the world beyond); the
  walkthrough's studio inspection stop reads it.

### Certified field contact

Owner: geometry and bake lane (sdf-authoring, rendering, physics) · medium ·
Next: box and ellipsoid bodies, then clearance close to a slope. Relief
colliders and the field provider read one certified surface solve
(`FixedFieldColliderContact.Solve`); the terrain pass's step 5 is this packet.

- [medium] Box and ellipsoid bodies solve from their centre
  (`FixedFieldColliderContact.SolveVolume`): the support along the measured
  normal, not the volume's own nearest point. Cover the volume with core
  spheres as the certified sweep does (`FixedFieldContactSolver.WalkCores`).
- [medium] Clearance within about a third of a metre of a slope is unproved:
  the proof's box is axis-aligned, so on a slope its corners protrude past the
  reach sphere, and a capsule 0.3 to 0.5 m above a 13° to 20° slope is
  `Unproved`. The step-up's across move and the carry release refuse such a
  pose. Three axis slabs cover the sphere with less excess, and an oriented
  box in the surface's frame removes it; either costs more box walks per
  unproved query, which are few.
- [medium] A body at a hard subtraction's convex crease is unproved: all of
  the headless sweep's `physics.contact.field-unproved` comes from
  `range-walker-10` on the river channel's bank (`riverChannel`), where
  `max(box, −cut)` reads the box term the cut removed. A proof needs a bound
  that follows the crease: the box split where `max` changes operand, or the
  cut's analytic surface read across it.
- [medium] Relief bounds for non-exact primitives
  (`WorldColliderSet.CopyReach`, `SdfSolidGeometry.DilationGain`): a primitive
  whose own unit field reads under its distance (the cone family) grows only
  by the reach. Take each shape's gain from the evaluator's reach
  certificates.
- [medium] A carved creation's contact narrows to its analytic pieces only for
  hard unions and hard subtractions (`FixedFieldPartition`); any other blend
  leaves every query on the field solve. An operand blended over radius k
  reaches only where its own field reads under k, which the reach certificates
  (`SdfFieldEvaluator.Certificates.cs`) can bound.
- [medium] `world.collision.probe` (`FixedFieldContactSolver.Probe`) answers
  nothing at a third of the points of a 1 m grid around a lawn flat-crown
  tree, can read the floor 6 m below a crown 0.3 m away, and inside a solid
  reports an exit that runs through other solids. Contact does not read it;
  check its projection and bracketing against the Superellipsoid's
  lower-bound field.
- [medium] The Superellipsoid's field (`SdfFieldEvaluator`, `mapCore`) is a
  1-Lipschitz lower bound whose far value is its least radius over its
  greatest times the true distance, so a sphere cast accepts a hit several eye
  radii from a flat crown and the cull certificates (`TryShapeSphere`) cover
  no Superellipsoid. A maximum with the bounding sphere's distance keeps the
  zero set and tightens the far field in both interpreters. Its CPU and GPU
  formulas also disagree at small radii
  ([hazards](../development/hazards.md#two-copies-of-one-computation)).
- A ground snap's support distance against a field collider brackets the
  collider's push by halving the reach (`FixedSupportDistance.FieldProbes`).
  A directed certified descent would replace it; `CertifiedFieldSweep` lives
  in `Puck.SignedDistance`, so the field collider would carry it as an
  `ICertifiedSweepQuery` the World supplies.
- [low] Relieved instances are never culled from `WorldSolidField`'s program:
  `NoiseDisplace` has no reach certificate (`TryCertifyInstance`).
- [low] The Moth creeps 0.04 m in x over 3,000 ticks standing at the rim;
  cause unknown. The terrain pass's 58° face check is not yet run on the
  certified solve.
- Contact cost at the rim: further cuts are contact over the job system's
  parallel partitions; an octave cut is only the drawn relief's own cut, never
  a contact approximation.

### Collision versus drawn mesh

Owner: next geometry and bake lane · ordinary · Next: the groups sharp boxes
left worse, then the valley sectors' buried colliders.

- [medium] Moving solid fields have no spatial culling in
  `WorldColliderSet.MovingField`: each active row evaluates its creation's
  field bounds even far from the body. In the isolated carved-support field
  scenario, the same ten-tick body path with size zero and size one counts 40
  `physics.contact.field-bounds` queries in either case; the active distant
  solid adds 347 `sdf.bounds.evaluations` and 5,552 `sdf.bounds.instructions`
  (zero when absent). A plain outside-box rejection cannot replace the raw
  distances and intervals the union promises. Give the moving set a bounds
  hierarchy and a query contract that distinguishes a positive clearance
  certificate from an exact field value; prove skipped rows cannot change
  contact, raycasts, navigation, gradients or materials. Verify with the same
  pinned paths and counters, with many distant rows and a nearby carved row.
- [medium] Sub-cell features draw badly where boxes touch or a thin layer sits
  inside a union. `puck mesh audit --cpu` reads these worse since boxes became
  sharp: `goTable` (its flush face prisms' seams), `hall-floor-2` and `-3` (a
  sliver and a shading band where a strip meets the slab's ceiling finish),
  `wonderland-room` and `hall-floor-0` (flat tops read turned 180 degrees,
  unexplained), the lift doors, and `device-humble-gaming-deck-television`'s
  open edges. The interior field measures to the seam and the contour's
  normals (`TryNormal`) read it. Patches that rewrite gap samples opened more
  holes than they closed; the fix is a field that is a distance across a
  seam, or crossings and normals that never read an interior sample. Not yet
  run: `puck test worlds/debug --jobs 1 --reproduce` and a lit windowed look
  at the stairs.
- The 178 witnesses the colliders report counts as uncertified all lie on the
  valley sectors' terrain masses: the render cuts each mass's buried part at
  the floor plane (`terrainFloorCut`) while contact collides each mass whole.
  Either contact takes the same cut, or the report counts those witnesses as
  buried.
- A solid screen collides as its world-axis bounds
  (`WorldColliderSet.ScreenBox`); it needs a rotation from its `Right` and
  `Up` frame.
- [low] The presentation query field excludes the solid placement
  `desk-chair`, whose evaluator refuses a `RepeatPolar` fold, so the chase
  camera and limb probes see no chair. Give the fixed-point query twin the
  fold, or bound the folded shape.
- [low] Carving blends collide as their composed field
  (`WorldColliderSet.CollidesAsField`). Check the places that relied on the
  old whole-box contact (the camera course says it does). A cheaper exact join
  for smooth unions waits until the field's cost is the limit.
- [low] A headless World grants one peer slot, so contact cost under many
  bodies cannot be measured through `puck_world_start`; give the headless
  host a way to admit a crowd.
- The bake-side causes are the [rendering plan](rendering.md)'s: a long thin
  member of a static union baking at the union's capped lattice, the
  sharp-edge rounding of about a tenth of a cell, and the Moth's
  interpenetrating meshes.

### Rigid bodies and physics

Owner: physics lane · ordinary · Next: per-surface contacts over a field
collider, then the lifecycle lows as one pass.

- [medium] A rigid body near a field collider generates its contacts by sweep:
  `FixedStaticContactSolver.CollectContacts` declines when a field collider
  lies within the step's reach, and `WorldBody.ProbeRigidSurface` sweeps the
  predicted motion. The game tables carry relief, so a knocked Go stone costs
  dozens of queries a tick. Answer per surface over a field collider; done
  when the Go knock scene counts about one exact query per candidate pair.
- [medium] The SDF field counters still count through the shared atomic
  `WorkCounterSet` on per-query paths: `SdfFieldEvaluator.PointWork`
  (`PointEvaluations` and `PointInstructions` per point walk,
  `SdfFieldEvaluator.PointWalk.cs`), `SdfQueryWork.ProcessWork`
  (`GradientTaps` per tap, `SdfFieldEvaluator.cs`) and
  `SdfBoundsCache.ProcessWork` (`PrepassBounds`,
  `SdfFieldEvaluator.Bounds.cs`). Each is two or three `AddShared` CAS
  updates per evaluation, which `ShardedWorkCounterSet` replaces with a
  plain store per thread. Move them onto it:
  `SdfFieldEvaluator.CountArrayPayload` and `CreateReachScratch` take a
  `WorkCounterSet` and need a counting parameter both types satisfy, and
  `puck bench world` (`WorldBenchmarks`) lists the sets as sources.
- [low] A stack's base under more than about two bodies' load can settle below
  the rest band. Build three-high and four-high stack scenes, read the base's
  penetration and fix the solver's load sharing.
- [low] A non-rigid sleeper still wakes on every `ContactFieldVersion` move
  (`WorldBody.TryDeferSleepingAdvance`); only parked rigid islands take the
  local wake. No live world enables non-rigid sleep yet.
- Lifecycle lows, read from source and not run: a rigid kit with a motion
  cadence is prepared twice (`WorldPopulation.Rigid.cs`, `WorldBody.Step.cs`);
  a stationary carried body's live pair contacts keep free rigid bodies in its
  non-parking island (`WorldPopulation.Sleep.cs`, `CollectRigidIsland` traverses
  carried rigid sides too), so those resting bodies cannot park;
  one refused probe drops a body's surface warm start
  (`ReleaseUntouchedRigidContacts`); a lattice spill into an
  empty slot under an envelope minimum accepts more than it was offered and
  faults the tick (`WorldTick.Fields.cs`, `FieldLattice`).

### The valley's terrain

Owner: geometry and bake lane (sdf-authoring, rendering, physics) · ordinary ·
Next: the engine gaps this packet holds, filled first
([rendering research](rendering-research.md#engine-gaps-filled-first)): a
sector's bound, the relief's caps and a mask for relief inside a basin (the
[terrain pass](terrain-pass.md#order-of-work)'s steps 4 and 3), then its
terrain programs and far shadows (steps 6 and 7). They start together with
step 1's checks; step 3's kernel edit lands in the bake fundamental's batch,
and step 6 waits on the colour conversion and the RTX 4070's shading columns,
not on the other devices'. Step 2 and the sets (step 8) follow. Relief
contact is [certified field contact](#certified-field-contact).

- [medium] Noise bake detail forces whole-surface refinement: `OwnFinal`
  clamps its factor to at least 2, so a small creation declaring a coarse
  spacing still subdivides its lattice (`SdfMeshPathBake`).
- A scoped static creation's instance bound is a sphere about the creation's
  origin (`WorldPlacementStamper`'s scoped branch), 770 m for a sector, so the
  bake's coarse-axis cap, not the relief's declared spacing, sets its cell. A
  bound about the shapes lets the declared detail through; measure the
  triangles it costs first.
- Creation limits sized for props bound terrain: noise amplitude at most 4 m
  (`CreationNoiseDocument.MaxAmplitude`) and smooth blends at most 0.5 m
  (`ShapeDocument.MaxSmooth`).
- At `valley-slope` on Vulkan, with bakes pending, the frame once blew out to
  white around the Moth; re-check the pose under the image meter.
- The CPU evaluator's capsule reads its segment parameter through a Q16
  inverse squared length (`SdfCapsule`), the precision loss the superellipsoid
  had for a large radius.

### The visual bar

Owner: look lanes (sdf-authoring, rendering) with a separate critic, the lead
scheduling · high · Next: the engine gaps behind this packet's misses, filled
first and ahead of any look lane that would sculpt around them
([rendering research](rendering-research.md#engine-gaps-filled-first)): the
valley's walls wait on [the terrain pass](terrain-pass.md#order-of-work)'s
steps 4, 3, 6 and 7 (gaps 1 to 4), the caldera water on
[water](water.md#order)'s gaps 5, 6 and 9 and on
[moving blends](moving-blends.md#engine-gaps-first)' gap 8, and the Moth and
the swallow on its gap 10 and on [bake detail](#bake-detail)'s gap 7. Then
the caldera water's first proof, then the per-milestone review across every
place. The bar and its judging are in
[the art README](../game/art/README.md#the-visual-bar); each place's look
intent is in its guide.

- [high] The caldera water fails its reference
  ([caldera pack](../game/art/caldera-water-pack/README.md), intent in
  `worlds/debug/debug.md#water`). The glacier (`glacierMass`) is rebuilt as
  one convex tongue of arc-bent slabs under snow lenses with layered slots, a
  lobed scalloped toe and a melt channel, and awaits its look critic; the falls are a flat translucent
  slab with visible tile seams and an opaque white impact disc; the pool is
  flat cyan with a hard rectangular edge; the river runs in a concrete trough.
  Rebuild each to its panel, building the rendering features the panels need
  where they are missing ([water](#water)).
- [high] Cel is the debug world's treatment
  ([the ruling](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)):
  every place opts in through its materials (`celMaterial`) and is judged
  under it. The valley's rock, the glacier's ice, the gallery hall, the
  exhibits' plinths, labels and section signs, and the Moth's painted slots are
  in. Owed: the valley walls' shade reading as mass, which the domes'
  smooth macro form limits more than the shading does; each place's guide
  naming its outside references for the treatment; and dawn and night legs.
- [held, the owner's ruling] The Moth and the swallow wait for a pair session
  with the owner, and no lane models either until then. Agent modeling did not
  converge: builders passed views the critic failed with pixel evidence, and
  many critic items were engine limits, which proceed on their own
  ([bake detail](#bake-detail), [modeling authoring](modeling-authoring.md)).
  The work so far is on `lane/moth-suit`, `lane/moth-pilot` and
  `lane/swallow-7`. The session's agenda:
  - The Moth is rebuilt to [her concept pack](../game/art/moth-concept-pack/README.md):
    she "isn't sacred and would be vastly improved", so her current sculpt,
    palette, rig and bake cost carry no weight. The suit, the off-suit pilot
    and the shared head.
  - The suited Moth toward the mech-suit sheets 01, 02, 03 and 10
    ([moth.md](../../worlds/debug/avatars/moth.md#geometry-and-motion)).
  - Her back thrusters to references 07, 08, 09, 12 and 13: proportions from
    the straight rear view; each wing a hollow casing around a dark core,
    leaning in at the top, with domed shoulders; the shell a chain of band
    plates hinged at the top edge, each lower edge overlapping the plate
    below; the opening facing in and down along a steep diagonal with a
    gunmetal trim and each nozzle flush to it; the spine a stack of chamfered
    blocks.
  - Her boots as one form after `01-suited-turnaround.png` and the boot row
    of `02-construction-details.png`, judged at the sheets' framing
    (`body.pose 48 0 -48 0 0 0`,
    `view.override camera orbit 48 0.95 -48 9 <yaw> 0 14`). Today the shell,
    sole, ankle hub and heel each bake alone, so every junction is a hard
    overlap or gap; the flared lilac shin, cuff, hinged knee guard and
    recessed cyan heel light are missing; the heel needs a housing around the
    downward nozzle.
  - The swallow (`worlds/debug/birds.puck`, not imported by `debug.puck`): a
    first draft whose rig tore the throat and faceted the eyes. Its recipe:
    references named first, one shaped body (`flare`, `shear`), inset eyes,
    painted markings, detail two bake cells wide; the bar met static from
    every side; then the idle rig with each moving part in its own group.
    Iterate live with `world.row.set creations swallow document <json>`.
- [high] The valley's rim and walls (`valley-north-west` and its siblings) are
  one olive colour with low-frequency blotches and lumpy surfaces: none of the
  direction's warm layered strata, ledges or vegetation, and a flat
  blue-purple region with a hard edge at the north-west rim.
- [high] Night is a void: a flat black sky with a few uniform dots, no moons
  and no horizon gradient. Its exposure and relight are
  [lighting](#lighting-and-gi)'s night item.
- [high] The debug world's whites take a yellow-green cast at noon from what
  it authors: the caldera's sunlit walls and the lawn (`lawnTurf`, about twice
  a real lawn's reflectance) outshine the clear-day sky (`dayCycle`) many
  times over on a shaded wall, and the devices room's teal walls
  (`wonderWall`) bounce green. A look call for the lead: darker, less
  saturated turf, a brighter day sky, or both.
- [medium] Daylight at spawn washes every colour out at `day` 0.6. Re-check
  it under the image meter; the calibration is the owner's sitting.
- [medium] Beside the Moth: her boot jets are blown-out white discs with
  oversized bloom; under night lighting her purple and cream read as pink and
  mustard; nothing separates her from the background, and a hovering Moth
  casts no readable shadow; a small floating yellow sprite beside her has no
  context.
- [medium] The binding-bar overlay sits over the Moth's legs in third person.
- [medium] The Humble Gaming Brick's atomic-orange glass shell
  (`device-humble-gaming-brick`) reads deep red: the shell is a solid prism,
  so a ray crosses 13 mm of tint where a real hollow shell's walls are about
  4 mm. A `depth` sized to the real walls is the likely fix.
- [medium] Sunlight leaks along the top of Wonderland's west wall under its
  roof (`wonderland-room`), a stepped sunlit band seen from
  `view.override camera look 8.8 1.6 33.5 1.4 0.9 28`. It goes with
  `world.shadows off`, so it is the cascades' test at the junction.
- [high] The lawn's trees read as inflatable toys: a few smooth drooping
  lobes with no branch structure or silhouette breakup, clean tube trunks, one
  flat green and one species. The meadow is the foliage forcing ground: a
  research round on tree structure through the SDF bake, then the trees
  authored in `worlds/standard/foliage.puck`.

### Water

Owner: the water feature lane (rendering, puck-world, sdf-authoring) ·
ordinary · Next: the engine gaps this packet holds, filled first
([rendering research](rendering-research.md#engine-gaps-filled-first)): depth
over the bed driving colour, a flow following the river’s course, and a moving cutter that re-carves. Then [the plan](water.md#the-lanes)'s lanes A3 to A5 (A2 is built, awaiting its look critic), then R3 and
P1 last. The plan owns the feature end to end; these are the defects it does not.

- [high] Add caustics on the submerged stones and read them through the glass pass.
- [high] The bake of the north-west wall's relief shows as hard facets on the trough's floor and the moraine ridges
  from the valley floor (`worlds/debug/glacier-site.puck`): the sector's cell at that distance is coarser than the
  trough's grooves. A near cell for the trough, or a creation of its own for the cradle, would carry the detail
  without baking the whole wall finely.

- [high] A swimmer cannot leave a walled channel: the swim hold (`swimHold`,
  `worlds/standard/kits.puck`) floats the body 1.25 m under the surface and
  the walk steps up only 0.3 m, so only a shelving bank lets her out, and jump
  does nothing in water. Give the hold a way out (a ledge climb or a jump from
  the surface) with its values in the standard package. Seen at
  (-65.6, -0.8, 27.8).
- [medium] Swimming switched the Moth into flight mode with `flightFuel` at
  1000, though `fly-1` needs rise without jump; suspected, jump reads zero for
  a tick across the hold change. `body.hold` also kept answering `hold=swim`
  on the grass afterwards.
- [medium] The carved channel's acceptance gaps: at stage 1 the river is so
  shallow that contact foam covers it white; the river settles well before
  stage 4, so stages 4 and 5 look alike and the stage ticks need revisiting;
  the river groups' vertex count is unshown; a dark strip at the river-pool
  junction is unconfirmed.
- [high] Let the river’s flow follow bends and piers. The outlet region handles localized acceleration. For a whole course, use the [flow field](water.md#w2s-field-source).
- [medium] The debug-world arc scenario (`worlds/debug/water-arc-tests.puck`)
  ran for hours without a verdict; the chain-only twin passes in minutes. Run
  the arc at `water.speed` if the chain's numbers are speed-invariant, or
  profile what the full world ticks that the chain does not.
- [medium] The pool and the falls hitch where the lake does not, with the
  glass passes a small share of the frame. Attribute them per pass.
- [medium] Cold water meshes still miss the one-minute target, and the
  prototype audits flag normal and topology defects in the glacier, lake and
  falls; inspect those witnesses through the audit's image output.
- [high] The glacier wastes back only by scaling about its head (`sizeBy` in
  the standard's `glacierMass`), which the look critic rejected. A cutter that
  moves against the ice keeps the cut it makes at rest, and a creation with such
  a cutter refuses its relief. Both are
  [moving blends' gap 8](moving-blends.md#gap-8-a-cutter-that-moves-against-the-surface-it-cuts),
  whose step G4 rebuilds the glacier's retreat as a cut.
- [high] Small stones in a wall sector bake at the sector's coarse cell, so the
  trough's moraine and grooves read as flat-shaded polygons; the lake's
  stones are creations of their own for that reason, at a preview cost of
  about 650 000 triangles for the cobbles and boulders.
- [medium] The falls ribbon beside the trough (`fallsRibbon`, lane A3) is a flat
  rectangle with a segment seam that rises into the sky at its top and tilts
  clear of the ground when the falls ebb. It needs to be a channel cut into the
  ground with conforming water.
- [low] `world.reload` of the debug world refuses once a lane's edit passes 16 MiB
  as a whole rebuild ("Rebuild payload is 19239407 bytes; cap is 16777216");
  the delta path reloads an edit of a few rows but a wall-sector edit that
  moves many prototypes falls back to the rebuild.
- [low] `water.speed 60` also runs the day clock, so stage captures come out
  at night; the tour's rows must pin the day.
- [low] A stage checkpoint: a verb that saves and restores a live arena with
  its lattices and bodies (what `replay.record` snapshots at its arm,
  `WorldPersistence`), so a stage of a long simulated arc is a file a windowed
  World loads. Build it when a second long-arc feature wants it.
- [low] A pixel shades two glass interfaces; past three front faces only the
  nearest is shaded. A 64-bit atomic k-buffer would lift the count. The glass
  kernel's register count on the RTX 2060 decides whether a third fits.
- [low] Edge shimmer: `sdfGlassLitAt` takes one bilinear refracted sample and
  marks refraction displacement as reactive in a stationary scene, so the
  resolve suppresses history at the silhouette. Give refraction motion,
  validity and footprint filtering that keep history when valid.
- [low] Mixed-density faces do not define a thickness integral
  (`sdf-optical-depth.hlsli` accumulates each face's own density): a water
  surface touching the `reservoirPavilion` pane is the failing case. Open or
  portal-clipped glass loses its paired face (`sdf-mesh-occlusion.comp.hlsl`
  routes glass without the enclosing bit), so its shadow darkens as the floor
  moves away. Define the optical boundary for both.
- [low · unchecked] Whether a state-bound water colour rebuilds the field
  program every frame while it moves (`WorldFieldEmitter.Emit`).

### Transfers, federation and server

Owner: specialist, transfers and server · high · Next: the
[custody protocol](companion-redesign.md#9-build-plan)'s build plan from step
6 (the separation sweep, stranding, shed and retire), step 7 with the
lease-missed recall; then the uncertain crossing, opened by a two-silo
failover witness. [The register](../decisions/worlds-and-federation.md#building-it)
routes the federation work here and names where each check lives.

Federation and transfers:

- [high, before any remote table check] A network peer cannot enter a spawned
  place. A live socket connection addresses only the boot instance
  (`WorldInstanceCommandModule`'s routing comment says composing the
  peer-admission door with `WorldServer.ApplySession` "is not built"), the
  debug world's admission rows are `mode: FederatedAuthority` only, which the
  door refuses for a direct peer join, and `WorldPeerHost`'s convenience
  constructor builds a `WorldAttestedAuthenticator` with no trusted issuers.
  Route a remote peer's session into a spawned instance through
  `WorldPeerHost` and give the debug world an attested-identity admission
  row. It unblocks every check that seats a remote player in the garden or a
  table room.
- [high] Tape fixed neighbour inputs at their local admission tick.
  `WorldPopulation.AdmitAdjacencyResponse` bounds what a neighbour's bodies do
  to a local body, but `WorldAdjacencyFields.Handle.Pin` copies the latest
  presentation snapshot and discards `sourceTick`,
  `WorldAdjacencyContactField.ResolveCore` quantizes floats after delivery,
  and `WorldReplayTickInput` carries no neighbour image. Build a fixed-point
  neighbour record (authority and generation, source tick, document and
  geometry revision, entity addresses, poses, colliders, contact modes) that
  the local tick owner admits once before simulation and tapes at its local
  admission tick, with the stale, missing and late cases chosen and recorded;
  shadow and live replay install it through one simulation-facing source.
  Check reordered, delayed and missing deliveries, mixed rates, a mid-session
  recording and a document change: replaying the admitted records gives
  identical local state and clamp counts.
- [medium] The layer mechanism and its gate. The resolver keys every global
  resolution to one scope key (`WorldSessionResolver.GlobalScopeKey`), a
  persisted destination refuses two running instances of one origin, and the
  transfer door aborts a transfer the leave rule refuses instead of deferring
  it. Build layer ordinals under one destination, a deferral queue in the
  transfer door, the authored unobservability predicate, and the hop cooldown
  kept by the layer set's authority per identity. Both answers to the owner's
  deferred layer-feel call are authored data on it.
- [medium] What a world nobody sees still costs its authority. Every
  instance steps on the frame thread whether or not anything shows it
  (`WorldInstanceHost.StepInstances`), and each session is still delivered
  to. The rendering research measures both
  ([its decision 1](rendering-research.md#1-measure-beside-the-work)) and
  reduces only presentation work. How often an unobserved world delivers is
  this area's question, asked through the observation door's projection
  request and not by the renderer.
- [medium] A contested seam's residual rule: a neighbour correction a body's
  own motion does not ask for still carries a still body toward the plane up
  to its ownership threshold, with no decay rule
  ([decision](../decisions/worlds-and-federation.md#contested-places-and-fights-between-player-hosts)).
- [medium] The settlement's remote identity is the link's first claimed
  `Authority`, so a neighbour that picks its identity before linking picks
  which side of a pair yields. Attested authority identities close it.
- Resolve an uncertain crossing through the authority directory, not a
  colocated row. A silo row whose crossing log answers `Uncertain` cannot
  write the final checkpoint a deactivation needs, and after a failover the
  source's in-doubt transfer stays bound to the stale local row. Route the
  doubt to whichever activation holds the destination's fence, and let a
  blocked row retire without a final checkpoint. Done when a law fails the
  destination over to a second silo while the source lives on, and the source
  settles from the new activation's journal with exactly one traveller.
- [medium] `body.pose spawn:<id>` naming a point only the home world declares
  refuses a seat whose body a federated authority holds. Carry it home through
  its committed route (`WorldInstanceHost.TryEnqueuePoseHome`).
- Re-evaluate federation disclosure tier and counterpart trust on live policy
  edits and active `Observe`, `Reserve` and `Route` sessions in
  `WorldPeerHost`, and handle `WorldDisclosureTier.Frames` in
  `WorldFederationCodec.EncodeDocument` without throwing.
- [medium] Held input does not carry across an authority crossing:
  `WorldTransferActionContinuity` resets holds, edges and partial patterns,
  where the seam ruling names dropped input as a seam and
  [valley and moons](valley-and-moons.md#phases)' phase 6 says held input
  carries exactly once. Carry it through the seat's existing command path,
  never a second input route.
  The seam walk's routes (`worlds/debug/walkthrough-tests.puck`) press the
  stick again every 15 ticks to get across, so the instrument cannot show
  this defect or a regression after it is fixed: when it lands, a hold-once
  leg per crossing replaces the re-press.
- [medium] Sound does not carry across a crossing: `WorldAudioDirector`
  re-enters an emitter from silence when its identity changes, and no plan
  owns continuity; the [audio reference](../reference/audio.md) is the
  mixer's contract.
- [medium] Prediction and rollback for remote play has no design.
  [Readable interaction](readable-interaction.md)'s step 5 depends on it, and
  its step 0 research round covers it.
- Signed remote write-back is
  [remote home adoption](records-and-pools.md#remote-home-adoption); it needs
  the hub plan's write door and award commit and
  [settlement](#settlement-trust-roles-and-revocation)'s identity abstraction.
- [medium · research input] Flecs adds two ideas to weigh when a round next
  touches state or federation: relationships as first-class data, with
  transitive queries, and runtime inheritance with override
  ([Flecs relationships](https://www.flecs.dev/flecs/Relationships.html)).
  Puck models containment, custody and membership as keyed rows and resolves
  overrides at compile time; the question is whether a relationship primitive
  and runtime prototype sharing pay for an MMO's inventories, guilds and
  millions of near-identical instances. Flecs warns that heavy relationship
  use fragments its tables.

Companions, beside the protocol's steps:

- [medium] The silo's deactivation (`WorldSiloHost.DeactivateCoreAsync`)
  writes its final checkpoint and retires without first sending visiting
  companions home, and freezes without the final custody check under the
  authority gate (`WorldServer.TryFreezeForRetirement`). Route it through
  `WorldInstanceHost.Stop` with the silo store as `DurableImages`; the store's
  upload is asynchronous, so the stop needs a completion that waits for a
  durable image across drains.
- [medium] Build the rest of the [companion decision](../decisions/companions.md):
  the destination's role declaration (`accept` the visitor's pinned driver,
  the same pin, a substituted driver, or nothing), with `worlds/standard/`
  declaring `accept`; the player's HUD notice on a refusal; driver grants keyed
  by placement or role group; the faerie's lasting state in registers; `night`
  declaring the companion role with the faerie's pin. `lane/companion-v1`
  holds the prior art step 9 ports onto `WorldCompanionCustody.TryBinding`
  (`TryRoleVerdict`, the `WorldRole` fields, the faerie split); keep the
  branch until then.
- [medium] No law shows the federation correlation of custody steps
  (`WorldRemoteAuthority`'s `HopQuery`, `HopRequest`, `CustodyQuery` and
  `HopSettled`): two concurrent questions about different keys must not share
  an answer. A witness needs two requests in flight on one lane while the peer
  holds its gate.
- [medium] A source gives a reservation up after `TransferRetryCeiling`
  transient refusals, where the model's source only re-sends. Step 8 bounds
  the retries by the lease deadline; the model then needs the give-up move.
- [low] An explicit stop of a World-process row disposes ordinary travellers
  it embodies with the row; only companions are swept home.
- [low] A frozen row answers a remote reservation or commit by throwing inside
  the federation handler, so the source keeps the crossing in doubt. Answer
  with a typed refusal.

Player state, beside [the hub plan](hub-and-saves.md#build-order)'s durable
images (H7), battery saves (H8) and minimal identities:

- [medium] `world.save` refuses on the debug world: it names the
  loop-generated `sheddingRack1` as a parent, which `shedding.puck` does not
  export. It writes the world's source when it succeeds, so it is never run as
  a read against a working tree.
- [medium] `WorldOwnedWorlds.IdentityBase` copies the whole host world into
  every identity.

State and server:

- [medium · unchecked] `world.decisions` reports 0 policies in the debug
  world's boot instance, so the pond's decisions may not run there.
- [low] A census body an install's re-clamp moves into a new slot gets no
  `PeerAdmitted` and so no default peer control grant (`SelectCensus`'s
  `reports` flag in `WorldPopulation.Reconcile.cs`). Decide whether a moved
  census body keeps its grant.
- Findings read from the code and not reproduced; confirm each with a failing
  case before fixing: `WorldServer.WriteStateCell` throws on a stale `CellKey`
  instead of returning `StaleAddress`; `ResolveDevelopmentRequest` and
  `ResolveBodyPoseRequest` drop a query's reply when host fuel is exhausted;
  `WorldStateDocumentValues` formats Bool as `1`/`0` in one path and
  `true`/`false` in another, and `ValidateStateTransaction` validates the
  unmaterialized definition; `RuleExpressions.Run` swallows an absent value
  inside `not`, `and` and `or`; `StateSeedReader` builds a state section
  without lattices, spaces, enums or pools; `ArenaSnapshot` and `ArenaUndo`
  index rows by non-document lane ordinals and keep change-window state across
  `TryRelayout`; `RuleCompiler` checks control audience twice and validates a
  transaction's failure branch before its primary branch's reads;
  `ValidateBodyScale` and `ValidateRowSelector` throw on a null `bodyState`
  entry.

### Settlement, trust roles and revocation

Owner: specialist, transfers and server, with a reviewer from another family
on the door's invariant · high, before any federation beyond loopback and any
table with stakes · Next: a specification of the settlement door in the
custody protocol's form (records, messages, transitions and a model they are
checked against), reviewed, then the build. The
[register](../decisions/worlds-and-federation.md#scarce-value-settles-at-one-door-per-domain)
states the invariant and its one enforcement point, the door's commit; nothing
enforces it today.

- [high] The settlement door: one transactional door per domain at an
  authority the player does not host, holding issuer-kept records; unit
  creation at the door alone and delegated budgets for every other award; the
  active seat as a domain right, consumed at landing and released at
  departure; signed receipts to every counterparty; the one-domain-per-operation
  refusal; the restore contract; the review of frozen value doors after an
  inexact image; the outbox; and the Deadlines abstraction's first user.
  Receipts cannot reuse the identity's delivery rows, which land only on a
  slot or a bounded, evicting row (`WorldOwnedWorlds`), and the silo hosts no
  account catalog, so an offline holder pulls its receipts from the door. It
  unblocks currency, items, stakes, the refund half of the hub plan's H17 and
  the tables.
- [high] Identity, issuance, revocation and roles as abstractions. The signing
  oracle's remote shape (`ISigningOracle` names a `Vouches` claim nothing
  implements); binding ceilings and a replay horizon at the admission door,
  where `WorldAdmissionDoor` builds its `TrustList` with all three unset; the
  refusal snapshot with a not-after instant per entry (`Puck.Attestation` has
  no revocation); the role list a domain vouches (issuer, dealer, referee,
  witness, budget holder); and the provider behind each. Proof of possession
  is the identity abstraction's: an arrival bound to the connection's
  challenge by a signature, where the Entra authenticator echoes a bearer
  token beside the challenge. It unblocks any federation beyond loopback, any
  vouched role and any table with stakes.
- [medium] The duel witness and prototype: a relay vouched as a witness role
  that signs receipt times and silence; lockstep commitments or
  witness-ordered inputs with a held-input horizon; a match-entry reservation
  that forfeits on an attested stall; and the loopback duel in an authored
  arena under the five fault legs. Needs the two items above.

### Security review

Owner: disclosure build lanes · high · Next: D0b1's acquisition wall, then
the single disclosure build table; the rd2-review
findings are answered in decision 3a. Then the researcher brief's priorities
([security review](../development/security-review.md)).

- [high] A viewer can still be shown what it may no longer see. A session's
  access is settled on the viewer's side from its observation
  (`WorldViewerAccess`, `WorldScreenBinder.AccessOf`), and three separate
  guards act on it (`IRenderGraphInstances.Withdraw`,
  `WorldScreenBinder.ContentAccess`, and the window sharing test, now `WorldSceneProof`).
  An adversarial review found a way around each, and
  [the withdrawal chokepoint](disclosure/withdrawal-chokepoint.md) checks its
  nine findings against the code and ranks the eight that hold in whole or in
  part. The worst shows content that
  was never delivered: a replica-tier session in a world with a restricted row
  is drawn from the authority's whole replica. The others keep a picture or a
  product of one after access shrinks: a view that retires skips the
  withdrawal, a replaced session keeps the previous residency, a node's own
  history and several retained copies survive, and a loss inside "disclosed"
  (a card moved to a zone the viewer cannot read) is not seen at all. The memo
  designs one door in their place: the authority decides a cut from what a
  stream was delivered and can no longer read, the cut reaches the viewer in
  order with the content, and content travels with the right to show it as one
  value. A design review found sixteen flaws in the first draft, and the memo
  answers each. It orders eight steps, each with a check, and no step deletes a
  guard before its replacement owns the same paths. Its step 1 is built (the
  tightened whole-replica test, the stream's generation, ordered ingress), all
  but the lease an admission grants after a lost connection. Next: step 2.
  This is decision 14's step 5 (results keyed by what each viewer was
  delivered) with the withdrawn picture of decision 2.

- [high] Authored search must plan over what its actor is disclosed. Today an
  actor-less job reads true state under the audience check (an AI can read a
  hidden hand), and search refuses private draws (an AI can never choose to
  draw). Two attempts failed. The first copied and recompiled the whole world
  per job and broke the debug world's boot. The second (search1, reverted)
  hid values on the live arena; a review found 14 high findings, four of which
  corrupt live state. Build the actor's position from what the disclosure
  path would deliver to that actor (an allow-list), never by hiding what it
  should not see. A seat's AI then plans on exactly what that seat's client
  holds, and hidden values are sampled from the job's own seed. Every job
  compiles only its judge's rules, and authority-only search is explicit
  (`actor: "world"`). Write a design memo with an adversarial design review
  before code, and prove it by booting the debug world and running the game
  tests with `--reproduce`. The reviews hold the cases to close:
  `SearchLawTests`, and the failing cases in sec6's and search1's findings
  (both reverted merges' commit messages cite them).

- [medium] Player and spectator replay needs a recording of the observations delivered to each recipient. The disclosure lane owns this work. Verify it with two seats that receive different hidden hands and replay only what each seat saw.

- [high, D0b1 incomplete] sec1 has introduced declaration-only row descriptors,
  restricted read primitives and an operator-dispatch capability, and moved
  the state-hash command onto the operator door. Cell-set submits its raw tail
  through the addressed link and parses it at compose. The
  [implementation checkpoint](disclosure.md#d0b1-implementation-checkpoint)
  states their guarantees and limits. The sec1 review's acquisition corrections
  are implemented: restricted row handles are typed, declaration bindings are
  admitted, unavailable cell writes compose raw intent, and retained operator
  hash access rechecks its live grant under the authority gate. Replica draw
  secrets are stripped at the shared row-observation boundary. Preparation
  checks admission before routing and consumes its read fault before minting
  an installable result. Mixed firings compose their ordered edits once. Eight
  external compilation cases cover the row handles, declaration lookups, sticky
  fault and capability minting reached so far. Migrating all preparation reads, the compiler and
  evaluator, removal of raw/lazy acquisition, DISC001 and the
  complete public-surface law remain unfinished. The existing admission
  predicates remain in force. Do not treat this checkpoint as completion of
  D0b1 or start D0b2–D0b4 from it.

- [low, D2/F12; before state-fed membership is introduced] Operator-source
  protection currently rejects any dependency writable through a recipient's
  seeded Edit/all, and `Allows` denies Edit/all wholesale when an operator source
  exists (`src/Puck.World.Server/WorldGrants.Authority.cs`,
  `src/Puck.World.Server/WorldGrants.cs`). Today's group members and roles are
  literal, so the reference collector reaches no such source. When membership
  becomes state-fed, protect the concrete source at the write door and admit
  unrelated state edits; prove both cases with an ordinary seeded seat. Do not
  make a live membership source render all authority transitions unusable.
- [low, D2/F18] Operator-group comparison uses record equality over list-bearing
  groups and kinds in `src/Puck.World.Server/WorldGrants.Authority.cs`. A separately
  materialized but equivalent protected group can therefore cause a conservative
  refusal when an ordinary seat edits another group. Replace that comparison with
  the group's canonical structural equality and prove independent materialization
  plus a real membership change. Duplicate kind names and group ids are already
  refused by `WorldDefinitionValidator.ValidateGroups`; the first/last lookup
  discrepancy is unreachable through admitted documents.

- [high] Ordinary recipient declarations still require the closed-scope and
  acquisition-wall work in [decision 3a](disclosure.md#3a-a-declaration-is-judged-inside-a-scope-its-declarer-reads-whole).
  The acquisition wall has one owner (D0b1); binding/relation/input partitioning,
  host preparation and authority maintenance land separately as D0b2–D0b4.
  D4/D5/D7 build closed scopes, D6 opens and migrates private/default-tier input,
  then D0c switches the remaining general routes. Those steps need their specified checks
  and trust-boundary review ([silent invariants](../development/hazards.md#silent-invariants)).
- [medium] The write footprint reads declarations a recipient below `Replica`
  is not delivered, so a `Frames` or `Presentation` session holding Edit
  learns that a withheld declaration sits in a write's footprint (never its
  name). [The structural write footprint](disclosure.md#the-structural-write-footprint)
  chooses declaration entitlement for general writes and closed scopes for
  lower-tier writes; D6 migrates authored input and addon bindings to protected
  scopes before D0c closes their old general-write routes. Nothing enforces it yet.
- [high, before any push to `main`] The write door's footprint is
  enumerated per field, and an Antigravity adversarial review of the read-door
  fix found five high and three medium leaks it missed: state-row references
  in mutation and placement values outside `CollectReferencedRows`; transform
  indirections (`$cell:`, `$zone:`, `$expr:`, `[key]`) with
  `WorldRecipientView.AdmitsTransform` admitting when it cannot resolve the
  rows; `AdmitsDeclaration` skipping knowledge, witness and per-cell readers;
  `Admits`' reverse footprint skipping dependents other than keyed rows; an
  empty `ExpectedStateRows` passing `AdmitsDefinition` while the fingerprint
  hashes state-dependent placements and fields; console write verbs reading
  the read view first; `WorldDocument.IsStateCommand` reading the unfiltered
  definition; `world.parked` and `world.hud.template`.
  [Decision 3a's structural write footprint](disclosure.md#the-structural-write-footprint)
  records the reopened evidence, options, restricted transaction, static
  security closure, supplemental derived dependencies, cost counters and a finding-by-finding harness
  witness. The review's one-versus-two `Placeholder` example is permitted
  disclosure to its named seat; D0a recasts the actual guard-ceiling attack at
  a lower tier and verifies every claimed reproduction. The design is complete,
  not implemented; its one build table incorporates the Claude design review.
- [high] Hidden state is distinguishable from absent state at doors the
  door-by-door fixes did not reach. [The disclosure plan](disclosure.md#the-build-order)
  is the structural fix and owns the queue: D0b1–D0b4 and step 6, step 0's fuzzer,
  step 0b's principals and step 7's red-team extension.
- [medium] The deployed debug world hands its whole document to every
  authenticated authority: `debug.puck`, `garden.puck` and `night.puck` author
  a `FederatedAuthority` admission row with domain `"*"` and
  `disclosure: Replica`, which with `azure.api-users` deployed is every member
  of the configured group, hidden draw secrets included (inference). Decide
  whether a returning traveller's window needs `Replica` or `Presentation`
  serves it, and narrow the row to the debug world's own authorities if
  `Replica` stays.
- [medium] Bound aggregate retained bytes across worlds, guests and compiled
  modules, and give a single native WAT conversion or compilation a CPU
  deadline or a peak-workspace bound
  ([priorities](../development/security-review-reference.md#independent-review-priorities)).
  `WasmModuleLoader` uses only the LRU's entry capacity, and `AddonInstance`
  retains its module after store disposal, so eviction cannot release a
  module's byte charge while a guest holds it.
- Complete the independent review: federation interruption, recovery and
  revocation schedules; authorization and disclosure composition; deployed
  Azure identity, storage and key policy; aggregate hostile-input budgets;
  cryptographic portability beyond the Windows and Linux fixtures.
- The bearer-token admission trust model, asked of
  `WorldAuthenticationProvider` and checked against the Entra authenticator:
  the challenge and session are compared but not signed by the bearer, so
  replay across challenges needs a still-valid token; on a pinned transport
  the destination's server proof is not checked (`WorldRemoteAuthority`'s
  `pinnedTransport` branch), so decide whether the transport pin alone is the
  intended authority proof. No bypass was reproduced; the signature over the
  challenge is [settlement](#settlement-trust-roles-and-revocation)'s identity
  item.
- Initial binding issuance from caller-writable key blobs (`AttestKeys`,
  `DefaultBindingService`): an authenticated user can supply a public key
  under their own prefix and trigger issuance when `binding.cbor` is absent.
  The identity abstraction requires a key the caller proves it holds;
  establish a concrete authority crossing before classifying today's path.
- Verify deployed SAS delegation with an ordinary authenticated caller
  (`GenerateBlobSasUri`, `DefaultBlobSasUriService`): reproduce storage
  acceptance outside that caller's permissions before calling it a
  vulnerability.
- Azure deployment policy: the API-host storage condition omits tag and
  queue-message actions (`abacConditions.bicep`); authenticated Functions
  callers can distinguish missing share recipients (`Shares`) and receive
  health exception messages (`HealthCheck`). This item authorizes no
  infrastructure change.
- [low, latent] `WorldRemoteAuthority.TryRouteCredential` selects the lowest
  body's credential for an unbound, body-agnostic request; no unprivileged
  path to the shared link was established. Restrict gameplay submission to
  traveller-bound capabilities, with a bound-seat positive control and an
  unbound-link refusal case.
- [low, unverified] The release record's power-loss guarantee rests on
  `AtomicFile.WriteAllBytesDurably` and on flushing staged payload files
  before the commit that selects them; that a new payload file's directory
  entry is durable on NTFS once a later write-through move commits is an
  inference. A power-cut rig, or a block-device fault injector on Linux,
  replaying an apply and a stub rollback cut at each write would show it.
- [low] Three consumers turn an absent expression into a number: a search
  job's score reads 0 (`RuleArenaSearchJudge`), a flock affinity reads zero
  and counts a failure (`ReadFlockAffinity`), and a binding whose local is
  absent is dropped at its sweep's end, re-arming an Edge rule
  (`RuleEvaluator`'s `HoldOnFault`). Decide what each absence means.
- [low] `worlds/hosting/granaries.extensions.json` grants its `console` client
  `mutate` over `section:state` to write six feed rows; scope it to those
  rows if the grant vocabulary allows. The `contribution:` subject of
  [decision 3a](disclosure.md#3a-a-declaration-is-judged-inside-a-scope-its-declarer-reads-whole)
  is that vocabulary where the rows can sit in a scope, which no rule may
  read.

### Test runner and replay

Owner: test runner and replay specialist · high · Next:
[the body's write door](body-state-door.md#steps) from step D5 (pose, motion,
clocks, tape and configuration), then D6 to D12 in the memo's order. D6
changes the rigid solver's interface in `Puck.Physics`, and D12 is the physics
lane's.

- [high] The per-tick authority hash is most of what a recording adds to a
  tick: `BodyContinuation` by an order of magnitude, then `RuleLatch`, `Input`
  and `BodyActionState` (`WorldPersistence.AppendStateHashComponent`). The
  door's steps D5 to D9 take the first and D9 to D11 the rest. A replica's
  fixed floor per scenario is unmeasured.
- [medium] `replay.drive` drives one authority, so it refuses a tape whose
  travelers depart to another authority as it refuses one that lands arrivals
  (`WorldReplayTape.TryBeginDrive`): a player who walked into the garden cannot
  watch that walk again live, only `replay.verify` it offline. Driving the set
  live needs the boot tape and the companion tapes its crossings pair with
  driven together on their own rows, each departure landing where its arrival
  was taped (`WorldReplaySetVerdict` already pairs them offline).
- [low] A debug-world scenario fixture that keeps `documentId: "debug"` (hall,
  chess, cardtable, freecell, meadow, suit) still boots the garden, night and
  hearth companions the debug world's `references` name, beside the boot
  authority. Hall's scenarios cross none of them; a fixture that overrides
  `references` and `destinations` to none runs one authority.
- [low] The replay start validates a changed configuration's checkpoint against a
  `WorldPopulation` its definition builds and the machine plan, before anything
  moves (`WorldPersistence.ValidateCheckpointRestore`). The attached-solid
  rows (`MovingSolids.ValidateAttached`) read the contact field an install
  gives the population, which the scratch population lacks (unverified), so
  they are checked only after the install, and a refusal there still leaves the
  reconfiguration installed.
- [low] `WorldPersistence.Checkpoint.cs`'s refusal text says "a checkpoint
  cannot capture pumped addon guests or a stepped machine without durable
  checkpoint support", but `AnyUncapturableStateEverLatched` latches only on a
  stepped machine: the message names a condition that no longer refuses.
- [low] `RuleGroupState.Flatten` emits dictionary insertion order, while
  `RuleGroupState.AppendStateHash` reads compiled group order. The checkpoint
  copies that flattened order in `WorldPersistence.TryCaptureCheckpoint`, so group
  insertion history can change checkpoint bytes without changing the state hash.
  Canonicalize group progress as the latch does, including the representation
  of an absent versus explicitly stored default progress.
- [low] The checkpoint image of an all-default array or input image is written
  whole; a sparse encoding would cut a slot's bytes about tenfold. It changes
  the checkpoint format, so it rides a door step that already does.
- [low] `--cache-dir` redirects only the `puck test` workers: the process
  itself still persists compiles to the per-user directory (`WorldsRoot`).
  Make it cover the compile cache, or narrow its wording.
- [low] Strict test teardown uses its own recursive `Directory.Delete`, where
  best-effort teardown uses `RunDirectory.TryDelete`, and keeps a path-depth
  limitation ([contributing](../development/contributing.md)).
- [low · unconfirmed] A scoped restore of one body in a parked island or carry
  pair may leave a checkpoint that then fails validation
  (`WorldPopulation.Checkpoint.cs`).
- [unreproduced] `replay.record` mid-session re-grants document grants revoked
  at runtime (the host's addon `Finish` after the checkpoint captures grants),
  and refuses `StartNotCheckpointable` after every pumped addon was removed;
  re-check the second now that a pumped guest's continuation is captured.
- [unreproduced] `WorldDocumentPaths.RelocateBetween` writes rooted paths into
  relative-only fields when the test directory and worktree sit on different
  Windows drives.
- A crossing scenario's sibling verdict is "held on some tick", so a negative
  claim about a visit is settled on what she brings home, and a destination
  reaped when it empties leaves nothing to export
  ([testing a world](../authoring/testing-a-world.md#crossing-into-another-document)).

### Language and authoring

Owner: language lane (transpiler) · medium · Next: the inline console command
hang, then the cost corpus, then the unreproduced language findings.

- [medium] A scratch test with `when { seat1: world.state.cell.set marker 0 7 }`
  on one line consumed minutes of CPU in preparation with no diagnostic; the
  command and the closing brace on separate lines compiled in seconds.
  Reproduce it and bound or repair the compile.
- [medium] The cost pairs two documents cannot share (static placements
  against one creation, `wall - opening` against its box tiling, a mirrored
  pair against a `symmetry` fold, union shape order, `x += 3` against
  `x = x + 3`) need a cost corpus over bake groups and the work sheet beside
  the document corpus.
- [medium] A rule `local` costs more than the expression it names:
  `local k = a + 1` then `cards[k]` prices one unit above `cards[a + 1]`,
  because the key program reads the local (`world.budget.rules --why`). Lower
  a local read into the canonical form, and add the pair to the corpus.
  `docs/reference/state/rules.md` also claims an `$expr:` key compiles into an
  implicit local, which the code does not do.
- [medium] A record-like table of fixed fields states its room by counting
  them: `table play capacity(25)` (`worlds/debug/chess.puck`) restates its 25
  fields by hand, since work pricing reads declarations only
  (`RuleCompileContext.RowCapacity`). A spelling that lowers to the authored
  entries' count gives the same document without the count.
- [medium] Template walls: `let`s inside a `prototypes` block are refused
  (PUCK036) and `with` takes only an object literal, so a shared view material
  became a required field and helper functions (`waterViews`, `riverChannel`);
  an `enum` block inside a template is silently dropped, so a row naming it
  fails with PUCK119 (`chaseCamera` works around it); a `for` loop cannot
  produce list elements where `map` does.
- [medium] `exports` does not compose: two `exports { … }` blocks keep only
  the later block's lists, where the `export` statement appends
  (`WorldDocumentEmitter`'s `ExportNode`), and `export` does not parse inside
  a template, so every document expanding `cameraResponse` writes its own
  exports after the expansion. Merge repeated blocks or admit `export` in a
  template body, and decide whether an import's surface carries its own
  imports' exports.
- [medium] No log of author walls exists, though "Ergonomic is optimal"
  (AGENTS.md) asks for one. The walls sit in S11's audit, this packet and the
  sdf-authoring skill's evals: start the one page those link to.
- [medium] The authoring-time creation store `WorldPrototype`'s remarks name
  has no implementation: no code puts or gets a creation document outside a
  projection. Build it when a creation-library step needs it, or delete the
  remark.
- Decide (the language lane's call): whether a rule's effect list merges by
  `key` when a template is re-expanded over a basis. A rule whose effects
  write one key twice (`claim-expire` in `worlds/debug/pond.puck`) refuses
  today, so the garden's crowd fixture re-expands only `pondFlocks`; effects
  are ordered actions, not a keyed set.
- Refuse a `garden { … }` expectation naming a document the world does not
  reference at authoring time (`puck lint`, the language server,
  `puck compile`); today `puck test` refuses it when it stages the world. It
  needs the composed document's references from the composer.
- [low] The work sheet prices a few shapes above what one tick runs:
  `companion-interest-*` is about 170 running-minimum rules a nearest-point
  reduction would replace; a write whose key is not literal counts against
  every pinned cell of its row in `CountWriters`; a transfer between piles a
  live zone table picks prices its disclosure term by the square of the piles.
- [low] A module library's lets and templates are never reported unused
  (`elementFamilyGlyphs`, `continuousStyle`); a directory lint could report a
  library declaration no linted source reads.
- [low] An imported module named `roles` changed how its importer lints
  (PUCK031, the fragment validated as a root). Re-run it on the current head,
  then find why a module's name changes import resolution.
- [low] A `Capsule` scaled `[0.05, 0.18, 0.05]` stopped about 9 cm short of
  where an author expected (`motion-nod`'s first neck); check what a Capsule's
  half-length means under scale and say so in the shapes reference.
- [unreproduced] A string literal on the left of a relational gate is not
  flipped (`WorldDocumentEmitter.Rules`); a header `when` gate makes a body's
  `local` report PUCK012; `not` loses its source span and recursion depth
  (`PuckParser.Expressions`); unary `-` or `~` before `not` parses at the
  wrong precedence and fails the format round trip; `PuckPrinter` drops `!= 0`
  from a grouped comparison gate.

### Maths

Owner: maths lane · ordinary · Next: segment durations as ticks or decimals on
the wire, then the float duration fields' schema migration.

- Eradicate duplicated mathematics (the owner's ruling), never trading away
  ergonomics or performance. What remains after the shared kernels
  ([maths-usage](../../.claude/skills/maths-usage/SKILL.md)):
  - callers adopt them: inline copies still stand in the simulation, SDF,
    presentation and tooling projects (degree conversions, the two 2π values,
    `v / length` clamps, rounded-length range tests, per-call cumulative
    scans, hand-rolled wraps and packers); an adoption that moves a simulation
    value is a correction attributed in its own commit;
  - the float duration fields become decimal in one schema migration
    (`RigidRestHoldSeconds`, observer and flock `UpdateSeconds`,
    `Autonomy.MotionSeconds` and `SteeringSeconds`, motion `LatchSeconds`,
    transfer and topology `HoldSeconds`, timeline seconds,
    `ActionStateSlot.Initial`, every
    `WorldSimulationTickConversion.DurationTicks(float)` caller), and segment
    durations travel as ticks or decimals (`WorldBody.EnqueueRun` converts
    `WorldCommand.EnqueueSegment.Seconds` through `FromDouble`);
  - one per-shape bound radius (`SdfProgram.TryGetLocalBound`, the
    certificate's `TryShapeSphere`, `SdfSolidGeometry`): they differ in
    precision and slack, so merging re-derives the certificate's slack proof;
  - contact programs do not scale a shape's `smooth` radius by the stamp scale
    while render programs do (`CreationStampEmitter.EmitFixed`): decide which
    is correct;
  - the oriented static box in `FixedSurfaceQuery` keeps a quaternion
    `RotateInverse` frame change beside `FixedOrientedBox`'s axis-dot frame;
  - audit which projects still need `Puck.Hosting` now that `EngineTicks` is
    gone, and rename `WorldAuthorityCheckpointCadence.EngineTicks`.
- Classify the public Maths surface with no law or waiver (algebra wrappers,
  coordinates, continued-fraction certificates, research APIs): bind existing
  evidence, give precise category waivers, delete unreachable code.
- Decide (the lead's call): whether a hashed-witness table may decide primes
  below 2^47 in one strong round in production `IsPrime`, capped near 32 KiB
  and judged cold as well as warm
  ([primality](../reference/prime-exploration.md#primality)).
- Re-run the select probe on each new .NET major, and when the JIT gains
  borrow recognition for the compare-and-mask fold (dotnet/runtime pull
  request 134039) or in-loop if-conversion:
  switch `ScaledResidueRing64.Subtract` to a ternary the day it wins and
  retire `SubtractBySelect`.
- Make the external prime comparisons reproducible as `puck bench` workloads
  (machine-prime's `is_prime` per bucket and input class, checked against a
  deterministic Miller-Rabin oracle).

### Addons

Owner: addon runtime · medium · Next: model table and segment state in
checkpoints.

- [medium] `WasmModuleDeclarations.WithCheckpointExports` refuses every table
  and every element and data-count section, and the World's mount gate
  (`WorldAddonRuntime.Lifecycle.cs`) requires a checkpointable guest, so a
  Rust guest that links the standard library loads through `WasmModuleLoader`
  but cannot mount. Model table, reference and segment-drop state in generic
  checkpoints, or prove which module resources are immutable; then mount and
  restore a standard-library guest in the headless debug world and author
  channelwalk's capability-boundary scenarios there. Keep the named refusal
  until that state reproduces
  ([the runtime contract](../../src/Puck.World.Addons/README.md#durable-guest-state)).

### Delivery, launcher and CI

Owner: delivery lane · high · Next: the production deploy's catalog listing,
which a push to `main` needs; then the silo's cold activation, CI's steady
state, the shader cache and the GC-hole retirement.

- [medium] A world whose checkpoint saved a running machine's image cannot
  activate when the new host cannot prepare that machine
  (`CheckpointRestore.Validate` refuses the whole restore by name). That
  keeps a saved image from being dropped silently, but at scale one machine's
  missing content makes the whole world unrecoverable until a later deploy.
  Bring the world up with that machine refused and keep its saved row in the
  next checkpoint, so a later deploy that can prepare it restores the image.
  The opposite direction (a refused row on a machine that now prepares) was
  fixed to boot it from power-on.

- [high] Production deploy fails at "Publish containers and apply application
  configuration": `az acr repository list` answers "authentication required",
  because `ReusePublishedContainerAsync` begins with a catalog listing
  (`AzureContainerRetention.cs`) the publishing identity deliberately cannot
  make. Ask for the known tag (`az acr repository show --image
  <repo>:<commit>`) and treat only a not-found answer as "not published"; do
  not grant the catalog role.
- [medium] Cold debug-world silo activation spends most of its time before
  checkpoint decode, in storage and definition admission (`WorldSiloHost`'s
  per-step timers; `WorldAuthorityBlobStore`). Profile that phase and compare
  cold and warm activations in one process on a quiet machine before choosing
  a publish or cache change.
- [medium] A checkpoint carries the whole world document, twice once the base
  and the live document differ (`WorldServerCheckpoint.DefinitionJson` and
  `BaseDefinitionJson`), re-uploaded at every cadence though the authority
  store keeps the published definition content-addressed. Store a
  checkpoint's documents by content hash beside it and keep only the hashes
  in the server section.
- [low] The official tree (`puck official build --worlds worlds/debug`) ships
  the composed debug world, but its `sources` and `documents` stop at the
  package, so none of `worlds/standard`'s sources reach the authoring
  workspace. Carry the packages a package imports, as `compile --tree worlds`
  does.
- [low] Left outside the launcher's confinement: `ContentAddressedUpdateStager`
  deletes a corrupt cached object by path and `ContentAddressedStore` writes
  objects by path, both under `update.lock`.
- CI's steady state: the producer is one serial job, then four shards.
  Packing, publishing and the determinism record could overlap, and a shard
  could restore only its own assemblies' outputs
  ([CI and releases](../development/ci.md#build-and-validate)).
- Finish the shared shader cache ([freshness](../reference/shaders.md#freshness)):
  no GitHub run has yet shown the restored Windows build, the silo image build
  and a repeated `shader-bytecode` job compiling nothing; a local cache evicts
  only on `puck shaders cache prune`; toolchain identity covers standard
  release locations, not every native-loader override.
- Retire the JIT GC-hole workaround:
  `DirectXCommandListRecorder.RecordBackBuffer` is compiled with
  `NoOptimization` until the pinned SDK carries a fixed JIT
  ([toolchain cautions](../development/contributing.md#hardware-and-toolchain-cautions));
  on each SDK bump, scan a running World's jitted methods for the pattern
  before removing the attribute.

### Provider independence

Owner: delivery lane · medium · Next: player storage identity. Each item is a
provider assumption outside its provider's project (AGENTS.md).

- [medium] Player storage identity is an authored user id (`storage.userId`,
  `ExplicitOverridePlayerStorageIdentityResolver`), today an Entra `oid` used
  as the container name. Resolve it from the configured identity provider, as
  the store is resolved from its provider (`WorldAuthenticationProvider.Client`
  already returns the verified subject), and keep the container mapping a
  store concern.
- [medium] `Puck.Actors` (declared `<PuckProvider>Azure</PuckProvider>`) is the
  Azure deployment's onboarding tier throughout, and nothing references it.
  Name it for its provider (a `Puck.Azure` prefix), retargeting the citations
  `puck docs links` checks. A non-Azure deployment needs a provisioning
  abstraction (identity, storage, keys) before it can onboard users.
- [medium] The dashboard portal and host sign in through MSAL and talk to
  Azure Blob directly (`tokenCredential.ts`, `resolveStorageEndpoint.ts`),
  which the architecture gate does not see. Hand the portal a sign-in and a
  store seam, with the Azure pair as one implementation.
- [low] Remote MCP's OIDC options encode Entra's claim shape: `SubjectClaim`
  accepts only `sub` or `oid`, and `oid` requires `TenantId`
  (`RemoteMcpOptions.Validate`). Make it a subject claim name plus a map of
  required exact claims.

### Edit to running, hot reload and boot

Owner: hot-reload follow-up (boot and build) · ordinary · Next: what a shading
edit still allocates, then a reboot for an edit outside `PuckHotReload`, then
the windowed C# loop and the cold and warm builds timed on a quiet machine.

The edit loop:

- [low] Staging unchanged source bytes invalidates a pending World build:
  `WorldArtifactKey` hashes `CheckoutContent.Change.Entry`, including Git's
  index/worktree status. An easy-b2 headless start was refused after its
  build-lease wait with "sources changed while waiting" when only `git add`
  changed that status. Key the effective source content independently of
  staging, preserving additions, deletions and paths. Check that staging and
  unstaging identical bytes retain the key and a queued build, while changing
  a source byte still invalidates it.

- [high] A fresh landscape World artifact builds for minutes before boot,
  against the 30-second bar. A live compiler stack sat in ILLink's data-flow
  analysis and in `StaleCaptureAnalyzer.CompilationState.Collect`; every
  compilation builds the analyzer's call graph with no fast path when it has
  no replaceable types. Profile per-project, analyzer, restore and copy costs
  before naming a cause; the artifact builder's separate output directory is
  a lead, not proof of lost incremental reuse.
- [high] A whole-world `.puck` compile of the debug world still reads,
  validates and installs the whole world before every reload, every
  `puck compile --validate` and every root `puck lint`, whatever the edit.
  [The authoring loop](iteration/authoring-loop.md#what-was-measured) took the
  profile, and its [build order](iteration/authoring-loop.md#build-order) is
  the work. Step 1 has landed (the name index, the stamp-trusted digests and
  origins composed only for a finding); step 2, one compile session, is next.
- [high] `world.reload` refuses after one live edit to a creation's palette
  (`world.row.set creations <id> document.palette[0].color <json>`),
  answering `world.reload.delta-unrepresentable:` with an empty reason until
  the edit is undone (`world.undo`); setting the colour back does not clear
  it. The cause is not confirmed; the reload diffs the installed
  declarations, which now hold the live edit, against the source
  (`WorldReloadDelta.Create`). The authoring loop's delta between source
  revisions removes the diff; until then the refusal owes at least its reason.
- [medium] `puck decompile` writes a source the compiler refuses when the
  document holds a shading program: a palette's `shade` prints as its table
  key (`shade: "program/<hash>"`), which is PUCK126, and
  [the CLI reference](../reference/cli.md#the-puck-dsl-verbs) says the verb
  never writes such a source. `world.save` prints rows through the same
  `WorldDecompiler`, so a whole-row rewrite of a palette fails its proof.
  Print the program as the phase block the grammar accepts
  ([the authoring loop](iteration/authoring-loop.md#saving-an-edit-to-source)).
- [high] A debug-world `world.reload` prepares off the tick and shares its
  unchanged rows, but its install still costs hundreds of milliseconds on the
  tick that adopts it, and its preparation's garbage pauses the frame thread
  while it runs. A sampled thread-time trace of the adopting tick reads
  `WorldServer.RecompileRules` (`SettleInstalledRows` building a scratch arena
  at the tick's time, then `LoadInPlace` loading every row into the live
  arena), `WorldOutputHub.DeliverDefinition` (`WorldDisclosureImages.Compose`
  serializing the whole disclosed definition for its frame digest) and the
  rigid bodies the install wakes; each of the four ticks after it composes one
  more audience's whole definition again (`FrameIdentity`, about 200 MB each).
  Make each proportional to the change: settle and load only the rows whose
  declarations moved, digest the disclosed frame per shared section, and wake
  only the bodies whose colliders moved. The preparation itself (two whole
  serializations, a diff, a merge and a deserialization of a 95 MB document)
  allocates gigabytes on the pool, about fifty young collections over its
  seconds; the per-declaration compile the `.puck` compile item above names is
  what removes it.
- A shading edit still runs the whole validator over its candidate and
  canonicalizes and digests the edited creation twice; reload's sender
  serializes and diffs whole trees and the server validates the whole
  candidate, off the tick but in proportion to the document, not the edit.
  Capture the edit's allocations, then narrow the validation as
  `TryValidateTouchedStateRows` narrows a state write.
- Every other mutation composes over `MaterializeArena`, which exports every
  document row anew. Export only the rows whose arena version moved, and
  reload a same-catalog arena in place (`StateCatalog.MatchesShape` answers
  whether the catalog survives).
- A state-section edit builds five arenas on the debug world
  (`world.boot.arena-builds`), one not yet attributed, and an undisturbed run
  grows the counter with no edit at all.
- An edit to a project outside `PuckHotReload` is refused and `dotnet watch`
  stops applying changes; it should reboot as a rude edit does
  ([in-world editing](#in-world-editing) extends that reboot with a resume).
  An applied update also leaves the installed SDF pipeline set as it is, since
  the pass-pipeline cache is keyed by bytecode.
- Time the windowed C# loop on an idle machine: a `Puck.World.Client` edit to
  ready under 20 s, boot to ready under 6 s. Measure the cold and warm local
  builds serially: what is left of a cold build is per-node startup and the
  project-reference closure's `GetCopyToOutputDirectoryItems` walk; read
  whether `MSBuildCopyContentTransitively` can be turned off.
- [low] Reload delta follow-ups: the rebuilt document skips the codec's
  grantee check, so `world.load` and `world.reload` accept different
  documents; a merge refusal surfaces the composer's raw message with no
  `world.reload.*` code; a remote sender's base is nameable only while the
  authority still holds it (`WorldDocument.Installed` holds installs weakly),
  so a remote reload into a world that mutates its document most ticks can
  still refuse `stale-base`; the "CAS resolution first" comment in
  `WorldDocument.Apply` is false.

State across an install:

- [medium] The seeded `World` Edit grant is lost across a load or a reload:
  after `world.load` into a same-capacity fixture, or a full `world.reload`,
  authored state-writing rules refuse with `the live edit grant refuses
  state:blink`. The cause is not confirmed.
- [medium · frame allocation] Every delivered snapshot brings its stream's
  definition identity current (`WorldDeliveryCursor.DeliveredRows` calls
  `WorldDisclosureImages.DefinitionIdentity`), so each row whose version moved
  is imaged again for its audience every tick, most of the debug world's
  headless tick allocation, though only a definition install and the
  federation sink read it. Keep the identity eager only for a stream that
  reads it, or pin a lazy one to an arena-wide version epoch; the audience a
  lazy read uses must be the one delivered.
- [medium · determinism · unconfirmed] Latches over pools may fire again after
  an edit: a `forEach` over a pool binds `LatchKey.LeftGeneration` to the pool
  handle's generation, and an edit in a pool-declaring world seeds its
  replacement from the whole export; if that restarts generations or slot
  order, `RuleLatch.RemapKeys` leaves entries on different carriers. Reproduce
  with a latched Edge rule over a pool across a state-section edit.
- A world whose state section declares pools does not narrow a state edit to
  its named rows (`WorldDocument.NameStateEdit`), so the per-cell traits of
  cells a rule removed are lost. Carrying pool storage rows into a
  replacement (`TryRelayout`) lifts it.
- A row named by a value write (`ExportComposeArena`) exports without the
  per-cell traits of its absent cells, so the next state-section install
  compiles the trait away.
- A cell written back under a per-cell `advance` trait after removal
  (`TryApplyCellUpsert`) gets no birth clock and accumulates from the origin,
  where an import settles such a cell to its install time.
- A bound document value read by the in-process client reads the server's
  arena from the presentation thread (`WorldLiveValues`), unsynchronized with
  the tick.
- [low] Rule-group progress (`RuleGroupState`) and decision state survive a
  load, while latches and scoped replays clear on one
  (`WorldServer.ContinueArena`). Clearing them needs a reorder: a checkpoint
  restore sets group state before `RecompileRules(Load)` runs.
- [low · unconfirmed] A `world.reload` that raises `bodies.capacity` validates
  an `inhabit.count` against the raised capacity, then spawns only what the
  boot's entity table holds. Decide whether a reload grows the table or
  refuses the raised capacity by name.

Boot:

- A self-contained composite ReadyToRun publish for the published World and
  the silo (decided by the lead): it brings the framework into the version
  bubble, removing the first crossing's JIT and the `System.Text.Json` generic
  instantiations (`WorldJsonSourceContext`), about a third of a headless
  boot's first-compiled methods; a windowed World compiles about three times
  a headless one's, partly from `WorldCodeWarmer`. Local builds stay IL.
  Measure JIT count, the first crossing's frame, boot time, publish size and
  publish time before and after; the streaming plan's seam walk reads the
  first crossing's JIT, loader and pipeline-state hitch in a fresh process.
- Prepare machine content once per input: every boot compiles the debug
  world's `.puck` cartridges again (`CartridgeSource.Prepare`). Key a cache by
  the inputs the compile read, as `WorldCompileCache` keys a world compile; it
  needs `ModuleResolver.ImportModules` to report the files it read.
- Two boot reads still resolve by reflection: `WorldSubmissionCodec` reaches
  its protocol union records through a `DefaultJsonTypeInfoResolver`, and
  `WorldAssetRowLoader` parses music, table, tune and patch documents through
  `DocumentJsonOptions.Shared`.
- A strict enum converter of Puck's own would replace the family behind
  `StrictEnumConverter<T>`, keeping the schema's `enum` lists,
  `JsonStringEnumMemberName`, flags spellings and the read side's case rules.
- [low] Growing the program region waits for the device to go idle
  ([gpu cost](../../.claude/skills/rendering/references/gpu-cost.md#pucks-open-cases)).
- Recheck `WorldCodeWarmer`'s windowed cold-write cost; a single slow simulate
  frame on a warm walk wants a sampled thread-time capture before it is named.
- [unconfirmed] A `--no-incremental` Release build once failed
  `SHADERPKG_OUTPUT` with a World shader package directory holding binaries
  but no manifest; possibly a packaging race.

### CLI and lease machinery

Owner: tooling lane (specialist, CLI) · ordinary · Next: the `puck integrate`
verb, then the launch and process lifecycles, each as one change that gives
its lifecycle an explicit state.

- [medium] A World start under a held execution lease gets an admission bound
  of zero (`HostAdmission.Wait`, `CliLease.HoldsExecution`), so inside the
  lead's quiet window a seam-walk stop started right after the previous one
  was refused at 15.3 GB free against 15.5 GB, before the exited World's
  memory had come back. A holder that owns the machine (quiet) loses nothing
  by waiting: give it the step's bound, or wait while a just-exited start's
  memory returns, instead of refusing.
- [low] `puck bench frames --window` still names "each physical row's owners
  and residual" absent, "supplied by step 2", though step 2 has landed; wire
  `host.memory`'s owner rows into the window or correct the text.
- [medium] `puck integrate`: the lead's integration steps as one verb under
  one report: the build, each generator's `--check`, `puck docs links`, a
  headless boot of the debug world, windowed Direct3D 12 and Vulkan boots, and
  named laws. Reuse `GatePlan`, `WorldLaunch` and `puck lease`.
- [medium] `puck_world_start` discards a build when any file under the
  checkout changes mid-build, documentation and `worlds/` edits included. Key
  the source state on what the World's build reads.
- [low] A cancelled or expired `puck_world_start` abandons its isolated build
  rather than stopping it (`src/Puck.Cli.Release/Mcp/McpCommand.cs`,
  `StartWorldAsync`: `Task.Run` uses `CancellationToken.None`, and cancellation
  only arranges disposal of a future result). A headless start's deadline
  returned refusal and left no pending start or owned World, while its
  adapter-owned `dotnet build` and compiler children still ran and needed
  manual termination. Give the build an explicit lifetime: cancel and reap it
  when its last requester leaves, while preserving another requester's live
  shared build. Check by cancelling during compilation and inspecting both
  adapter jobs and child processes, including two callers sharing one build.
- [medium] `puck measure` cannot time `bench world` across builds whose
  in-process types differ: the bench loads the frozen build's assemblies
  under the requesting checkout's CLI. Run each side's bench from that side's
  own frozen CLI, as `bench startup` and `bench frames` do. No recorded-tick
  bench row exists either.
- [medium] Measurement and lease defects:
  - a `puck measure request` filed under the caller's own quiet lease queues
    its frozen-tree build behind the build lease that holder owns; a request
    from a process whose ancestor holds the lease nests or refuses at once;
  - `puck lease quiet` holds what it has while it waits for the rest; acquire
    all three in one admission decision or none;
  - a window that starts after the display turned off samples the
    compositor's slow fallback (`Win32NativeWindow.DisplayPower.cs` keeps a
    lit display lit but does not wake one); `puck measure run` checks that
    presents display, wakes the display if it can, and otherwise refuses the
    slice with that cause;
  - a measurement's readiness probe (`world.wait ready 1`, 10 s, `SweepWorld`)
    gives up before a cold first frame on builds that compile shade programs
    at boot; time warm to first bake on a frozen build and size the probe
    from it;
  - a failed after side leaves a half-frozen request directory with no
    `request.json` (`MeasureCommand`); stage and rename, or delete it.
- [medium] `puck locks` re-resolves floating packages throughout an affected
  lock file after a reference change, even without `--update`
  (`src/Puck.Cli.Gate/Locks/LockFiles.cs`, `RecordArguments` and `Record`).
  A local-feed reproduction pins `1.*` at `1.0.0`, publishes `1.1.0`, then adds
  only a project reference: the non-update restore records `1.1.0` as well as
  the new project entry. `--force` differs from `--force-evaluate`, but an
  invalidated graph still re-evaluates its floating dependencies. Preserve
  existing resolutions during graph repair, without retaining a version that
  a changed constraint excludes, and reserve upgrades for `--update`. This
  needs a restore-resolution design, not a switch-only fix: cover direct and
  transitive floating dependencies, multiple target frameworks, changed
  constraints and removed references. Check with an isolated local feed whose
  newer package appears between the initial restore and the reference edit;
  non-update must retain the admissible old version, update must take the new
  one, and a subsequent locked restore must pass. Do not rewrite resolved
  versions in the output lock file without resolving their dependency graph.
- [low] `puck format --check` reports thousands of files drifted, mostly from
  its whitespace phase: the phase or its settings disagree with
  `.editorconfig`. Fix it at the cause and sweep once.
- [low] Linux has no kill-on-close: `ChildProcessJob` is a Windows job object,
  so a killed CLI leaves its Worlds running, and an MCP World has no
  `--exit-after-seconds` backstop. Use the host's primitive (a parent-death
  signal from one long-lived starting thread, or a process group), never a
  polling watcher.
- [low · unconfirmed] A windowed Direct3D 12 World started with
  `--exit-after-seconds 840` and a stdin script ran far past it, through its
  whole cold bake. Find whether the auto-exit counts wall time, ticks the bake
  starved, or nothing once stdin closes.
- [low] An old-protocol CLI that already holds a lease keeps it:
  `HostAdmission.Wait` does not read `CliLease.RecordedProtocol`.
- [low · plausible, Linux only] `FileLock.TryAcquireForDeletion` deletes a
  lock file while it holds it, and the artifact store deletes keyed locks, so
  two builders can lock different inodes at one path
  (`WorldArtifactBuildClaim`). Check the inode after locking, or stop deleting
  keyed locks.

Launch and process lifecycles, each a [low]:

- The started-World and worker lifecycle has no explicit state
  (`OperatorWorlds`, `OperatorMcpFront`): a World that exits without a stop
  keeps its entry and state directory, a reused process id is misreported or
  wrongly stopped, routes are never removed on a stop, and the pending-start
  table evicts in hash order. Give each World an entry state
  (`Booting | Running | Exited | Stopped`) keyed by process id and start
  ticks, whose exit transition deregisters, and evict the oldest start.
- `WorldProcess` has no state between running and drained: `WaitFor` treats
  exit as drained after one rescan, so a final line arriving later reads as no
  answer. Count end-of-stream per stream and end on drained.
- A windowed World nobody asks to wait counts as still growing for its whole
  life, since only the `world.wait`
  forms print the readiness line. Have the World print it once ready.
- Canary's concurrent leg budget (`CanaryCommand.CanaryBudget`) still counts
  external lease queueing, so a later leg can read `BudgetExpired`.

CLI maturity remainders, each a [low]:

- Refusals come in several shapes: `ERROR: ...` lines in `puck test`,
  `landing`, `parity`, `canary` and `bench world` against `CliExit.Refuse`'s
  shared line. A lease refusal and the child's own failure can both exit 2.
- The test batch watchdog writes its cause only to the batch's `.stderr.log`;
  `sweep` regex-scrapes `puck test`'s output for verdict counts.
- `puck counters` has no `--world-artifact`, so it cannot compare counters
  across builds; `puck compile --watch` watches only the source's own
  directory though its help promises the imports; `puck affected` and
  `puck gate` cannot be narrowed to one suite or step.
- `puck world release checkpoint` prints a JSON document and then a plain
  line on the same output.
- Not re-read at the head, so verify before acting: option names that drift
  between siblings; `disclosure` overrides that skip the fixture's validation;
  absolute backslash paths in `lint`, `decompile`, `creation`, `mesh`,
  `font-atlas` and `embed`; no `--json` on `lint`, `compile` and
  `embed --check`; `cartridge-cost`'s text-only output; `docs build --site`
  and `laws prove` refusing late; the mesh and creation verbs' copied
  helpers; `official verify`'s nested records read strictly; `setup-puck`
  repeating the `nuget smoke` logic in bash;
  `bench world`'s unpaced idle-tick row and `bench startup`'s pooled summary;
  `bench state-evidence`'s stale pinned digests.
- Generate `docs/reference/cli.md`'s verbs index and a synopsis per verb from
  `PuckRootCommand`'s command tree, written and `--check`ed by a docs verb as
  `puck vocabulary` is. It replaces hand-typed option fences and defaults
  that have drifted; then shrink the content-search and symbol-analysis
  references' flag lists.

### MCP and the operator attachment

Owner: MCP lane (specialist) · medium · Next: the stale lease holder, then the
private-install activation measurement.

- [medium] A GPU lease is held by an older operator worker while no World is
  advertised and the worker has no child process, so windowed starts expire
  after their acquisition wait. Inspect the owning session and the
  `OperatorWorlds.WatchAsync` and `Stop` lifecycle, and release it through
  that session, never by touching a foreign process or lease file.
- [medium · unconfirmed] Lockstep Worlds drop an attachment mid-call while
  converging: the call answers "Host disconnected"
  (`LocalControlClient.ExecuteAsync`), so `LocalControlServer.ServeAsync`
  closed first: the World's own deadline, a connection-class exception
  (`IsConnectionError` includes `OperationCanceledException`), or an exit.
  Reproduce with a lockstep windowed World and a capture near its `timeoutMs`.
- [medium] The lead session's own `puck` MCP server may run a stale CLI: the
  project root's build is never checked against its HEAD, as the fallback's
  is (`.claude/hooks/puck-mcp.ps1`).
- A connected session keeps a tool's old output schema when the front
  switches to a backend whose schemas changed, until `/mcp` reconnects. Keep
  output schemas additive across a live switch, or answer with the schemas
  the client was given.
- Time the front's swap to a newly activated build from a private install root
  (`puck mcp --prepare-install --install-root <worktree>/.claude/mcp-install`).
- [low] Investigate the reported operator attachment loss on `world.reset`:
  `WorldGrants.EndSessionsForRebuild` ends observation sessions, while
  `WorldControlCommandModule` owns a separate local console endpoint.
- [low] Operator writes no longer toast (`WorldToastPolicy`), but the
  `mutation.applied` audio cue still fires for them. `world.grants` and
  `world.affordances` answer in one long line, so `puck_exec`'s `lines` and
  `match` cannot narrow them.
- Wanted next, each on an existing hook: a batch capture answering with a
  contact sheet (`ConsoleControlSession.CaptureAsync`; `puck motion` owns the
  sheet writer); camera tracks in recordings (`world.view.camera` through
  `WorldSeatCameraCommandModule.Fields`); compact state diffs, with
  `world.state.hash` as the cheap equality check.

### Tests (suspended)

Owner: suites lane · dormant until the owner restores test requirements
(AGENTS.md) · Next: triage the failing Windows shards by cause: stale after a
deliberate change, update or delete; timing-dependent, fix or delete; a real
regression, fix. A red test holds nothing back, and a refactor deletes a test
that stops compiling.

- [medium] The Windows CI test shards fail across more than one shard, which
  may hide real regressions among stale tests
  (`gh run view --log-failed` on the latest `main` run).
- Red on the integration tip, each to update to the behaviour it now meets or
  delete:
  - Server: `TransferAbortKitWideningLawTests` needs a
    nonzero accumulator remainder its fixture's rates no longer leave.
    `GpuFaultsCommandModuleLawTests`, `WorldRuleMixedDocumentTransactionLawTests`,
    `ShapingRowLawTests`, `InhabitCountLawTests`, and one case each of
    `StorageDeadlineLawTests`, `WorldValidatedLoadLawTests` and
    `WorldAuthorityRecoveryLawTests`; fourteen `WorldAuthorityBlobStoreTests`
    cases whose directory target is below the checkpoint bound (size it with
    `WorldAuthorityBlobStore.DirectoryTarget`); `DeliveryReviewLawTests` (four
    cases) and `SessionObservationLawTests.ReadDoors` (four), the disclosure
    area's.
  - Presentation: `FederationTransferLawTests.ReservationWire_RefusesAnUndecodableTravelerIdentityByName`
    builds wire bytes by hand that fell behind the companion member fields.
  - Rendering: four `SdfPassPlanLawTests` laws expect shadow-near before mesh;
    `SdfWorldPassesLawTests.TheViewsRecorderWritesTheLiveFadeCountAsync`
    expects `sdf-world-views` where a programless frame binds the plain
    kernel; three more of its laws wait on elapsed frames for a graph rebuild
    and fail under load (wait on the install signal);
    `SdfPackedContractLawTests`,
    `SdfMeshIrradianceLawTests.RemovingTheLastStaticDrawReplacesItsSceneAndProbeMeasurements`;
    `SdfSkyEnvironmentDeviceLawTests` and `WorldRoutedPresentationLawTests`
    still set `IndirectTier = Off`, which now runs the volume; `GpuRegion`'s
    two-gigabyte staging law; a cascade-sizing law that times out; and
    `SdfWorldPassesLawTests`' cadence and temporal laws, 55 of which fail
    because the view never stands (`TemporalRig.Stood()`) after twelve frames
    (naming only the frame's sky as a direct source did not change it; the
    cause is unread).
  - Tooling: `McpInteropTests` placement counts,
    `RemoteMcpTests.StateVectorWrite_GrantedPrincipalSucceedsAndUngrantedIsRefused`
    (a client timeout), `LanesCommandLawTests`, the `docs links` symbol
    control, `FormatProjectionLawTests` (six `worlds/` sources differ from
    the printer), `ShippedSourceLintLawTests`
    (`worlds/standard/hydrology.puck` trips PUCK_LINT_003), the
    `SweepEvidenceLawTests` index.
  - Unconfirmed whether they predate the change that found them:
    `MeshCommandTests` (`AnOfflineBakeAuditsAGroupWithNoDeviceAndNoStore`,
    `TheIdentityVerbPrintsABuildsIdentityItsPartsAndWhatDiffers`,
    `AStoredDummyADamagedEntryAndAMissingMeshAreReportedAsSuch`).
- The keep-list the owner approves: exact results and determinism
  (`Puck.Maths.Tests`' laws; World.Server `MusicReplayReDerivabilityLawTests`,
  `CrossingReplayLawTests`, `ReplayForkLawTests`,
  `WorldStateHashCompositionLawTests`, `BodyContinuationHashLawTests`;
  World.Games `ContinuationHashLawTests`; State `ArenaHashLawTests`,
  `DivisionAndPowerLawTests`; Physics `OrderingLawTests`,
  `FixedRigidWorldOrderingLawTests`, `FrictionLawTests`; World.Schema
  `MirroredSolidFieldDeterminismLawTests`; AGB.Forge
  `ColourIdleEquivalenceLawTests`), the C# and HLSL sync contracts (SdfVm
  `SdfKernelHeaderReadLawTests`, `SdfLightAndSkyLayoutLawTests`,
  `SdfPassBindingLawTests`; SignedDistance `SdfEncodingProbeLawTests`; Shaders
  `ShaderRegisterBindingLawTests`, `ShaderFrameBlockLawTests`), and the
  classes verbs run by name (`EntraWorldAuthenticatorTests`,
  `WorldSiloDefinitionLawTests`, `WorldSiloLifecycleLawTests`).
- Find what removed live run directories during one concurrent CPU run:
  `Puck.World.Tests` and `Puck.World.Silo.Tests` crashed at exit because their
  `puck-protoref-process-*` directories were gone. Next time, log every
  deletion under the temporary root while the suites run.

### Sweep and visual checks

Owner: sweep follow-up, with the lead scheduling the GPU slot · medium · Next:
the tour's memory peak, then reproduce the companion overlaps, then one paired
windowed tour compared stop by stop, with `--attach` on one World for both
runs.

- [high] The tour's last stop does not survive a memory budget: an attached
  Direct3D 12 tour reaches every still through `garden`, then the session's
  memory reaper stops the run during `night-field`. The crossing back from the
  garden re-plans the `world` residency and loads its meshes while the
  previous set is held, and re-bakes groups the per-user mesh store evicted at
  its cap for older identities' entries. Until the peak falls the tour
  completes only as two attached runs. Find what the crossing holds twice, and
  stop the store's cap from evicting the live identity's meshes
  ([memory](#memory-on-the-deck-tier)).
- [low] A windowed `world.debug-view report colliders --json` on the debug
  world has not been read since the report moved to a deferred worker: a
  headless reading completes with no exception (463664 witnesses tested,
  complete coverage), but the earlier `DivideByZeroException` was seen on a
  windowed World, which adds the rendered-mesh comparisons. Read it once in a
  windowed window; if it still throws, the answer's text carries no stack, so
  catch it in `WorldDiagnostics` with the exception's `ToString()`.
- [medium] The full windowed sweep records companion overlaps between bodies 0
  and 42 after a clean initial reading. Find which walking or pose transitions
  let the companion touch her and fix its separation
  (`worlds/debug/addons/companion.wat`).
- [medium] The windowed `--tour` does not repeat to its 2 % threshold on a
  long-running World: the picture depends on which cells the trace has cached
  (`sdf.mesh-gi.camera-traces` differs with World history), so a cross-build
  comparison cannot separate a change from lighting history. The paired tour
  on one attached World confirms whether the remainder is the probe cache's.
- [medium] A windowed sweep's convergence is unproven under load: the case
  that once starved it (frames held for minutes, the attachment dropping) has
  not been retried beside a build or a test run.
- [medium] The walkthrough tour's route stills clip their exhibits' tops
  ([`walkthrough-tests.puck`](../../worlds/debug/walkthrough-tests.puck)).
  Stand the stops back, or pitch the eye, until exhibit and plaque both fit.
- [low] A sweep's `--baseline` names one report, so a split run marks every
  signal the other part read as new and red. Accept several reports, or add a
  `puck sweep merge`.
- [low] One capture is pending at a time
  (`RenderGraphRuntime.PendingCapturePath`), so a stop orders its captures by
  hand around a burst's read window. A queue of captures removes the rule.
- The paired motion qualification: the tour's holds and pauses land ambient
  bodies, the lift, the companion and the spin exhibit on the same ticks every
  run (`SweepRun`), which paired motion captures have not yet shown. A cold
  Direct3D 12 run once stayed at tick 0 until its deadline, waiting on an
  upload fence; cause unconfirmed.
- Read boot-once's windowed workload through the full sweep, keeping per-file
  timing and boot and work counts.
- The temporal reconstruction's quiet-machine frame and memory qualification:
  rerun the integrated tour, standing and walking frame readings and the
  gallery and lift body patrol; history is `HistorySurfaceWords` words per
  output pixel in two copies and `HistoryWindowWords` in one, so measure the
  view and slot allocation.
- [medium] The sweep compares no per-tick allocation: `SweepSignals.ReadFrames`
  keeps `host.frames`' header and phases and drops its frame thread allocated
  line, and the `memory` signal reads after a collection. Keep that line as
  metrics with a relative compare in `worlds/debug/sweep.json`, so the batch's
  allocation comparison (orchestration, coding lanes and the integration lane)
  is a sweep signal and not a hand reading.

### The benchmark

Owner: one benchmark lane per stage · high · Next: the owner's windowed watch
of `puck benchmark --route debug --loops 2` on both backends, then stage 4's
remainder (a route played by `puck test`, the per-tick accepted-input and state
hash sequence, waypoint coverage, the seat-started bindable run, a recorded
walkthrough reduced to goals) on the goal chapters and seat program built
([the route is places](benchmark.md#the-route-is-places-played-by-an-authored-seat-program-stage-4)),
beside the lead's windowed reading of `puck benchmark` on both backends
([the build order](benchmark.md#the-build-order)'s stage 3).

The owner's words: "it's slow as shit and mostly just static shots that miss
all of the issues we see while on the move". The memo owns the design and
the stages; this packet holds what a stage does not.

- [high] The lift chapter's steps are the route's slowest frames: at 2560 by
  1440 a dozen steps take 0.6 to 0.8 s each on both backends, and the
  headless chapter still has isolated simulate spikes around
  door swaps. The rendering plan's `frame` lane owns the fix
  ([first wave](rendering-research.md#the-first-wave)). A thread trace showed rigid surface probes and full replica frame
  serialization (`WorldSessionSink.DeliverState`, `WorldDeliveryCursor.FrameMoved`).
  The partial navigation bake now runs on a worker, and response swaps keep the
  attached door's sampled pose so its new collider does not wake remote rigid
  bodies. Profile the remaining spikes with `puck benchmark --headless --route
  debug --chapter "the lift"` using its `frameThread` report. The frame digest
  now reuses unchanged placement rows, and analytic contact installs reuse
  compiled variants of the changed doors. Isolate cold rigid contact work
  from the remaining delivery hitches. The replica wire still encodes the
  whole definition when an observable frame changes.
- [low] A route chapter's control-line refusals are counted when the chapter
  ends (`WorldSeatDriver.Refused`), so a line still settling then is not
  counted; count each line's verdict as it arrives.

- [high] The same route runner is built four times: the tour's route stops
  (`SweepRoute`), the window (`FrameBenchmarks`), `puck motion --route` and
  the motion scenarios, each with its own plan format and parsers. The
  memo's stage 10 folds the first three into the benchmark. Their arrival
  judge is already one (`WorldRouteArrival`, which `SweepRoute.Arrival` and
  `WorldBenchmarkCommandModule` call).
- [medium] `host.frames` reads the host loop's interval and never the time a
  frame was displayed, so judder that an even interval hides is not read
  (`HostFrameTimeline`). Stage 8 adds the displayed interval.
- [medium] The overlay pass costs 2.4 to 2.8 ms of GPU time at 2560 by 1440
  (`world.gpu-timing`'s `main$overlay` with the benchmark panel up), more
  than the world's composite pass; attribute it (the panel's
  two 60-bar plots against the rest of the overlay) and cut it at the cause
  ([rendering research decision 15](rendering-research.md#15-present-frames-the-display-has-room-for-and-know-when-they-showed)).
- [high] The route does not arrive against a remote authority, and the
  driver is no longer the reason: it reads only what the seat's client was
  delivered ([the ruling](benchmark.md#the-route-against-a-remote-authority))
  and plays every chapter in process. What stops a remote run is federation.
  Each defect below was read from a pair of headless Worlds, one listening
  and one started with `--connect`, both on one staged document and one
  federation key:
  - A `--connect` traveller's seat input does not move its body on the remote
    authority. With the transfer committed and deliveries arriving, a held
    `player.signal gamepad.leftStick 0 1` leaves `body.where` unchanged for 90
    ticks and neither process names a refusal. The client submits through
    `WorldFederatedServerLink.SubmitIntent`, and the authority admits through
    `WorldTick.PublishFederatedIntent`; where the input is lost between them is
    not known. This blocks every remote route, on any world.
  - `--connect` resolves its remote destination from the document the client
    booted and refuses one that authors no `host.authorityNamespace`
    (`WorldInstanceHost`'s remote destination resolution). The subject a run
    expects at its remote authority is a run input, like the endpoint
    `--connect` already takes, and the debug world authors none.
  - The debug world admits no remote player: `bodies.networkPlayers` is 0 and
    its one admission row names no signing peer. Its row grants
    `Observe` over `all`, which the admission door refuses for an arriving
    traveller ("observe must name a concrete body"), so an arrival there is
    delivered nothing; a row with no subject, which resolves to the arrival's
    own body, is delivered.
  - A replica travels as one message under `WireLimits.MaxDocumentBytes`. The
    debug world's replica is 21.9 MB and the cap was 16 MiB; it is 64 MiB now,
    which moves the limit and does not remove it. A replica's creations should
    travel by content pin, as the presentation projection's prototypes do.
  - A replica that names content by relative path fails the receiver's
    validation, which has no directory to resolve it beside
    (`patches[0].document.source` and a HUD element's `voice` on the debug
    world): `WorldFederationCodec` deserializes a delivered definition with no
    document directory, though the receiver holds its own copy of the document
    the destination was resolved from.
  - A replica dealt to a recipient who may not read a cell still carries the
    rules that read it, and the receiver's validation refuses the document
    (`StateCellUndeclared` for the auction's `bid`): `WorldStateDisclosure.Disclose`
    withholds the cell and the rule is sent whole.
  - At the presentation tier the debug world's authority does not answer
    `Reserve` inside the lane's deadline (`WorldRemoteAuthority.LaneRequestTimeout`),
    on each of the client's retries. What composing its projection costs was
    not measured.
  - An observer that falls eight records behind is detached and reconnects
    (`WorldFederationProjectionSink.PendingDeliveryLimit`), which on a loaded
    machine happened twice in fifteen seconds; each detach is a quarter second
    and a whole document again. A route's report shows it as a delivery gap.
  The pair's staging, until `puck benchmark` does it: a delta over the world
  (`basis` the world's entry by absolute path) authoring
  `host.authorityNamespace`, `bodies.networkPlayers` and two admission rows, a
  wildcard `FederatedAuthority` row granting `Drive` and `Observe` with no
  subject and a `SignsDirectly` row pinning the pair's own key under the same
  namespace; both processes boot it with one `--federation-key-file`, the
  authority with `--listen` and the client with `--connect`.
- [medium] `puck benchmark` cannot start the remote pair. It needs the staging
  above, the companion the canary already starts (`AuthorityCompanion`), and
  a wait for the seat's route to turn remote before it arms the route, since a
  paused boot commits no transfer; `world.benchmark` then needs a read of the
  seat's topology to wait on.
- [medium] A seat driver no longer fails a chapter on a wall it scrapes along,
  only on one it stops against: it reads a stall from the delivered poses,
  where it read the authority's obstruction witness
  (`WorldBody.LastObstructionNormal`), which no delivery carries. Publish the
  witness as a body fact (`ActionFact`, `BodyFacts`) so a client reads the
  authority's own answer; the latch then travels in the body's checkpoint and
  transfer sections, since an action gate can read it.
- [medium] A run started with `--connect` still steps its whole boot world
  locally: the seat's body leaves and nothing else stops, so a remote
  reading's `simulateMs` is a world no one is in. A client of a remote
  authority should simulate nothing it does not present.
- [medium] A fresh run's first chapter opens on the boot's settling, not on
  its pose: on a headless World armed `at 10` after a `--paused` boot, the
  chapter's first two iterations (ticks 10 and 11) still simulate for
  hundreds of milliseconds, and a thread-time trace of the ticks before them
  holds seconds of rigid contact settling
  (`WorldPopulation.SolveRigidContacts`) and session sinks composing their
  disclosed definitions, while a warm attached replay of the same chapter
  opens on an ordinary tick. Arm a fresh run's first chapter after the boot
  settles, or report the settling as its own span, so a chapter's statistics
  read the route.
- [low] A headless benchmark reports `rendered: true` because
  `WorldBenchmarkCommandModule.Finish` tests whether pacing exists, rather than
  whether a renderer exists. The headless Free Cam seat program observed this
  alongside `gpu.state: absent`. Derive the report's rendered flag from the
  presentation host, keeping host-iteration timing available headless.
- [medium] `puck benchmark --uncapped` boots with the immediate present mode and
  does not lift the presentation target: `PresentPacingPolicy.Resolve` caps an
  explicit `world.target` at the display's ceiling (a variable-refresh range's
  maximum, or the signal), and only an unknown signal resolves unbounded. Add a
  way to ask for no target (a `world.target` form, the lever and the policy's
  `Unbounded` basis) and show a windowed uncapped run presents past the
  display's rate.
- [low] The comparer reads the operating system and the logical processor count
  as the machine's identity; it does not read the GPU adapter, the driver or the
  display, and it does not know whether the live overlay was on. Add both when
  stage 2 lands and a platform probe names the adapter.
- [low] A chapter's GPU maximum is the largest of the rolling window means
  sampled every 30 ticks (`SampleTicks`), not the longest single pass: the
  timestamps keep a window mean and no per-submission series.
- [low] The sweep's windowed open items
  ([sweep and visual checks](#sweep-and-visual-checks)) that concern the
  tour's stills, its 2% threshold and its paired tour close when stage 6
  deletes the tour; the memory peak at the garden crossing stays, read by
  the painting chapter.

### Agent tooling and skills

Owner: the lead · ordinary · Next: the analyzer's soundness, then the grader
validation that makes the skill suites believable.

- [high] `StaleCaptureAnalyzer` misses three shapes the hazards page states as
  covered: passing the old arena to a replacing call exempts every later read;
  the STALE003 seed misses a getter that returns its field through a local, an
  auto-property's setter and `??=`; a refresh on the normal path stops the
  walk before the replacing call's catch and finally edges. Medium: `finally`
  is modelled as a bypassable successor; `s = host.ReplaceAndGetScratch()` is
  flagged by source order; local functions use a second, text-order mechanism;
  an async replacement is attributed to the call that starts it; STALE002
  ignores inherited root accessors; `[Replaces(typeof(object))]` and stale
  marks are never checked; `IdentityCounterAnalyzer` matches project paths
  relative to the scan root and drops namespace and arity from type keys.
  Low: mentioning a counter counts as persisting it, and a tolerant
  `Matches` reads as exact. Fix each or list it on the page.
- [high] The skill-eval programme
  ([evaluate an agent skill](../development/documentation.md#evaluate-an-agent-skill)),
  in value order:
  1. Validate graders, tag cases regression or capability and add the
     no-checkout line, for puck-dsl, puck-world, rendering, rom-forge,
     sdf-authoring, verification, review-passes and symbol-analysis; about 300
     llm graders have no PASS/FAIL pair, so their scores are not believed yet.
  2. Re-run the suites whose graders were repaired (boy-scout, content-search,
     dotnet-performance, gaming-bricks, maths-laws, maths-usage), with and
     without the skill; then the eight above.
  3. Shrink boy-scout to what only it adds, once its re-run shows which cases
     its text moves.
  4. Preloaded runs where an agent preloads the skill: content-search and
     symbol-analysis at the scout's tier, review-passes at the reviewer's.
  5. delegate and orchestration with and without the skill on the strongest
     tier; orchestration's measures its description's new triggers.
  6. A review of the results by a reviewer of each family.
  7. The five new area skills' ten capability cases, and `puck-world`'s
     regression cases after its routing rows changed.

  A preloaded skill sits at the ceiling, so only a with-without run separates
  edits. The dispatch workflow is unexercised until it reaches `main`.
- Repair the eval cases whose with and without scores differ by noise, each by
  inspecting prompt, grader and replies first: puck-world
  `w07-failed-build-stale-binary`; orchestration `o05-partner-message`,
  `o07-write-review-into-a-gpu-tree`, `o11-second-round-findings`,
  `o16-brief-prewrites-the-design`; review-passes
  `r06-reviewer-claims-absent-from-the-tree`, `r07-write-pass-cannot-build`,
  `r09-range-given-as-two-dots`, `r21-write-pass-while-a-run-builds`;
  rom-forge `f05-world-rule-refuses-cartridge-forms`; verification
  `c08-two-dot-is-not-ancestry`. Close the graders that fail with the skill
  loaded: puck-world w03 and w04, verification v08 and v12.
- [medium] Capability indexes in the area skills. A duplicate is written when
  an agent asks "what does X" and no index answers. Give each area skill a
  capability table (`maths-usage`'s Choose table is the model), starting where
  copies were found: the world deal and placement code, and collections and
  comparers.
- [low] Landed history the plans consolidation left in place, for a next
  pass: `state-and-language.md`'s S11 descriptions of landed G-items; the
  "(landed)" markers in section 6 of `mmo-foundations/synthesis.md`;
  `hub-and-saves.md`'s M1 narrative; `acceptance-laws.md`'s implemented
  fixtures; `machines-and-cartridges.md`'s "In place:" status;
  `editor.md`'s red-leg law tests; and reference moves owed:
  `records-and-pools.md` sections 1 to 4 to `docs/reference/state`,
  `updater-policy.md`, editor E5 and E9, the demand tiers' invariants, and
  water's "as built" sections.
- [low] The hazard detectors' follow-ups: mark the next replaceable family (a
  GPU device lost and recreated is the likely one); triage `puck scan --only
  clones` below its heaviest thirty clusters; a C# and HLSL formula pair has
  no detector, so an agreement law per pair is the check that holds it.
- [low] `puck lanes` cannot see lanes the lead's launch scripts start, which
  log to the job's temporary folder; a findings-only lane also needs its
  `.task` file. The reviewer's "builds nothing" is prose only: the read-only
  policy cannot tell reviewer from scout.
- [low] Probe once whether a `claude` session started from a worktree loads
  that worktree's `.claude/agents` and unscoped skills. Log one `Monitor`
  PreToolUse payload; if it carries `tool_input.command`, widen the
  agent-policy hook's matcher to `Bash|PowerShell|Monitor`.
- Give Antigravity a fair coding trial before deciding its role (the owner's
  direction): one bounded fix in its own worktree with a mechanical check, the
  Superellipsoid's far field and small-radius disagreement
  ([certified field contact](#certified-field-contact)), shown by running
  both interpreters on the failing inputs. Score it beside comparable lanes:
  does it build and pass what it claims, findings from two reviews, brief
  adherence, report honesty, and whether its runs finish headless. Record the
  verdict in the orchestration skill's routing table.

### Refactoring campaign

Owner: the lead, one implementer lane per group · high · Next: the groups below, largest payoff first; the two snapshot
mirrors wait for the presentation view. The
owner lifted test requirements for this campaign (AGENTS.md). A duplicate is
fixed when found; this packet holds one only between waves. A `.puck` rewrite
proves itself with `puck compile` lowering to the same document.

- Two mirrors of the boot world's snapshot, both hub subscribers copying every
  entry per tick: `WorldClient` and the boot endpoint's `WorldSessionMirror`
  (`WorldAuthorityEndpoint`); and three per-sink redaction scratch buffers
  (`WorldOutputHub`, `WorldSessionSink`, `WorldFederationProjectionSink`).
  They go with the presentation view's last cut
  ([runtime plan](runtime-and-delivery.md#the-presentation-view)).
- Two recipient streams built side by side: `WorldSessionSink` and
  `WorldFederationProjectionSink` each own a `WorldDeliveryCursor`,
  `WorldDeliveryRows`, `WorldSolidScaleDisclosure`, body redaction, input
  filtering, a presentation feed, and now the same cut announcement and
  re-priming (`AnnounceCut`, `PrimeAfterCut`), differing only in where a
  record goes (an `IClientSink` or a wire frame). One stream type writing
  to a record target would hold each rule once; the withdrawal chokepoint's
  step 4 (the mirror per span) is the natural place to make it.
- [low] File-system primitives `AtomicFile` cannot serve yet:
  `ShaderCompiler.Entries.cs` wants a write that refuses to replace;
  `DirectoryObjectBlobStoreBackend` stages through a confined handle; a lock
  file `FileLock` opens can be pending delete on Windows when
  `WorldArtifactStore.PruneLocked` removes it, and the open throws instead of
  waiting; `ConfinedDirectory.TryAcquireLock` keeps its own contention test
  beside `FileLock.IsContention`. Two `AtomicFile.PendingWrite.Commit`
  hazards, unverified per platform: the overwrite fallback (`File.Replace`
  with a backup) may leave no destination after a crash between its renames,
  and `Commit(overwrite: false)` preserves a concurrent winner only where
  `File.Move` without overwrite is create-only. A law that races two stamps
  on each platform settles the second.
- The `.puck` sources still under-use the language:
  `worlds/debug/avatars/moth.puck` holds thousands of decimal literals and
  raw quaternions, with the pod's band groups pasted (a uniform row table
  waits on a template leaving a member out, G13 in
  [state and language](state-and-language.md)); `worlds/standard/cards.puck`
  traces rank glyphs as polyline points though the engine draws text, a look
  change the owner reviews; hundreds of rotations are quaternion literals;
  `worlds/standard/controls.puck` spells the twelve pad sources twice;
  `*-tests.puck` scenarios repeat input lines where `for` and `repeat n`
  serve.
- Code that .NET 11 makes worth revisiting. Each item names what the runtime
  changed; [dotnet-performance](../../.claude/skills/dotnet-performance/SKILL.md)
  cites the post's section and pull request for it. An item is shown by
  disassembly (`puck bench kernels`) and counted work, with a timing only in
  the lead's quiet window, and none moves a simulation value. Several are
  duplicates first and speed second.
  - **Build: runtime async.** [medium] No project sets the feature. Add
    `<Features>$(Features);runtime-async=on</Features>` to
    `Directory.Build.props`, skipping `Puck.Analyzers` (it targets
    `netstandard2.0`, which has no runtime to lower the methods; that it must
    be skipped is an inference to check). Rebuild, then compare allocated
    bytes per request or tick on the World server, silo and networking paths
    (`AllocationWindow`, `host.memory`) before and after, and keep the opt-in
    if the counts hold or improve. It pays where small async methods stack:
    each directly awaited call stops allocating an intermediate task, and an
    exception stops being rethrown at every frame. The post names cases that
    are still slower, so the counts decide.
  - **Maths: unsafe span walks.** [low] `BinaryFieldKernels.AddRegion` walks
    both regions with `LoadUnsafe` and `Unsafe.Add` and sizes every loop from
    the destination alone; only its one caller, `BinaryField.AddRegion`,
    checks that the lengths match. A slicing loop over both spans is free of
    bounds checks on .NET 11 and makes the span types enforce that invariant.
    Its remarks promise regions of two gibibytes or more, so slice by element
    count, never through one byte view whose length would overflow.
    `Pcg32Extended.AdvanceTableByOne` indexes its whole table through
    `Unsafe.Add` where a plain loop over a local copy of the array reference
    is already check-free; its draws do not change. The masked table read in
    the same type stays, because nothing proves a mask held in a field.
  - **Maths: the wide fixed-point product.** [medium]
    `FusedArithmetic.Product` forms every wide `FixedQ4816` product through
    `Int128`, on a remark that says the form measured faster than `Math.BigMul`
    and asks for a re-measurement before it is replaced. .NET 11 lowers the
    signed `Math.BigMul` to one `IMUL` returning both halves. Re-run the A/B on
    the .NET 11 runtime with disassembly beside the timing. If `BigMul` wins or
    ties with smaller code, replace the body, delete the remark, and correct
    the sentence in [the CLI reference](../reference/cli.md#puck-benchthe-puckmaths-microscope)
    that calls the `Int128` multiply deliberate. The exact product is the same
    either way. Then read whether `(Int128)long * long` in the algebra kernels
    (`DoublingAlgebra`, `QuadraticAlgebra`, `MonogenicAlgebra`) lowers to one
    multiply; if it does not, give them one shared widening-product helper
    instead of patching each site.
  - **Maths: `BigIntegerFunctions.ToDouble`.** [low] Its remark says the
    platform's `BigInteger` to `double` conversion truncates toward zero. On
    the .NET 11 runtime the cast rounded to nearest even on every value probed:
    ties and above-half cases at 54, 55, 64, 65, 101 and 201 bits, positive
    and negative. Correct the remark. With a zero exponent the method then
    repeats the platform cast, so route that case through the cast and keep
    the method for what it adds (the binary exponent, the overflow midpoint,
    the subnormal range), or show a width where the cast still differs.
  - **Maths: the signed byte dot product.** [low, only on a profile]
    `SignedByteVectorFunctions.Dot` widens, multiplies, widens again and adds
    per block. A multiply-add of adjacent widened shorts, the shape
    `PrimeKernels` already uses, halves the widening; the 512-bit VNNI form
    takes unsigned by signed bytes, so it needs a bias correction that nobody
    has derived or checked. Do this only if a profile shows the rule
    compiler's vector operands spending time here. The result is an exact
    integer, so order does not matter; check against
    `SignedByteVectorFunctions.DotScalar`.
  - **Machines: the framebuffer repack.** [low]
    `QueuedMachineWorker.RepackFramebuffer` repacks every emulator frame with
    `Vector128.LoadUnsafe` over `MemoryMarshal.GetReference`. Replace it with
    a loop that slices source and target forward, then the scalar tail, and
    read the World build's disassembly (the proof is static, so the World
    gets it). Its comment requires the vector and scalar paths to agree bit
    for bit, in the repacked bytes and the average colour.
  - **SdfVm: one priority order, as a struct comparer.** [medium]
    `SdfMeshPathBake.ByPriority` and `SdfWorldTables.ByPriority` are the same
    `Comparison<(float Priority, int Index)>` written twice. Replace both with
    one public `readonly struct` comparer in a shared home and sort the spans
    both callers already take through `CollectionsMarshal.AsSpan`. That fixes
    the duplicate, and on .NET 11 a struct comparer on a span sort is not boxed
    and its comparison inlines, where the delegate costs a call per
    comparison. Claim speed only with a counted or traced reading of
    `SdfWorldTables.OrderLanding` or the path bake's plan. The other
    `Comparison` lambdas beside them (`SdfMeshIrradiance`'s candidate order,
    the path bake's tiers) move the same way if a profile shows them hot.
  - **SdfVm and Assets: zlib through stream adapters.** [medium]
    `SdfMeshStoreCodec.TryDecode` expands every store entry through a `fixed`
    block, an `UnmanagedMemoryStream`, a `ZLibStream` and an end-of-stream
    probe, though the expanded length is in the header. One
    `ZLibDecoder.TryDecompress` into the buffer it already owns replaces all
    four; refuse the entry when the call fails or the written length differs.
    `SdfMeshStoreCodec.Encode` writes its payload with a `BinaryWriter` over a
    `MemoryStream`, compresses into a second one and copies out with
    `ToArray`: write with `BinaryPrimitives` into an owned buffer and compress
    with `ZLibEncoder.TryCompress`. `PngDecoder.DecodeImageData` and
    `PngEncoder.ZlibCompress` have the same shape, so put one zlib one-shot
    helper in a shared home for all four. Decode runs for every store load, in
    parallel, on the cold-bake and boot path. Show it with allocated bytes per
    entry and a warm-store load reading. Entries are caches, so a change in
    compressed bytes needs no migration.
  - **Commands: one whitespace token rule.** [low] `CommandRegistry.LeadingVerb`
    and `CommandRegistry.Tokenize`, `WorldCommandArguments.EndOfToken` and
    `WorldCommandArguments.LastTokenStart`, and `CommandEcho.TryReadToken`
    each scan `char.IsWhiteSpace` runs by hand, and three comments say they
    must agree. The comment in `WorldCommandArguments` gives the reason for
    scanning as "not a listed set"; .NET 11 ships that exact set as span
    searches (`IndexOfAnyWhiteSpace`, `IndexOfAnyExceptWhiteSpace`). Have the
    three share one token-rule helper built on them and delete the comment's
    reason. Command lines are short, so this unifies a rule and claims no
    speed.
  - **Shared: one hex-hash validator.** [medium] About ten sites validate a
    fixed-length hex string, each in its own way: LINQ `Any` or `All` with a
    per-character predicate (`WorldMutation`, `CorpusManifest`,
    `WorldSpatialReadDependency`, `WorldDefinitionReadDependency`,
    `MachineContentAdmissionPolicy`, `AzureWorldReleaseSecrets`,
    `EntraWorldAuthenticator`), and a span search that passes the digit set as
    a literal on each call (`AssetContentHash`, `ContentPin.TryParseHex`,
    `LocalEndpointCapability`). Write one public validator beside the
    content-hash types, backed by two cached `SearchValues<char>` sets
    (lowercase, and either case), as a length test and one
    `ContainsAnyExcept`. Keep each site's case rule: lowercase only at
    `EntraWorldAuthenticator`, `AssetContentHash` and `ContentPin`.
  - **MCP: two uncancelled timeouts.** [low] `OperatorMcpFront.RunAsync`
    waits twice with `Task.WhenAny` over a `Task.Delay` that nothing cancels
    when the other task wins, the shape analyzer CA2027 names. Replace each
    with `WaitAsync` and a caught `TimeoutException`, using the `TimeProvider`
    overload where the clock is injected. Both are failure paths, so this is
    hygiene. Then consider raising CA2027 to an error in `.editorconfig`: the
    SDK ships it but reports it as a build warning only under a stricter
    `AnalysisMode` than Puck's default.

### Author leaks to the standard

Owner: UI lane, with the play-feel and editor lanes · ordinary, each leak fixed
when it is found (AGENTS.md) · Next: the overlay text advance and the helper
templates; [UI step 1](ui.md) is this packet's remainder. Each item is a
design built into C# or HLSL, or living only in the debug world, where the
standard package (`worlds/standard/`) should author it over a neutral engine
default.

Looks and rules of play held in engine code:

- [medium] An unauthored world renders a baked atmosphere: `WriteAtmosphere`
  falls back to `SdfAtmosphere.Default`, a fog of `SdfSky.DefaultFogDensity`,
  and every authored sky layer, fog and medium without a luminance reads
  `SdfSky.DefaultLuminance` (`WorldEnvironmentResolve.cs`). Make the
  unauthored default neutral, author the atmosphere in each debug-world place
  that leans on it, and look at the places on both backends before and after.
- [medium] Three looks the rendering research found in engine code, each with
  the authored home [its looks table](rendering-research/step-designs.md#where-each-look-is-authored)
  names: the share of its
  own exposure a window's world applies (`SdfPhotometry.WindowAdaptation`);
  and the shares of the display at which a nested view's update rate steps
  down (`SdfUpdateRateTiers.ViewHertz`).
- [medium] The opening ripple's feel and look are engine constants: reach,
  margin and strengths in `WorldOpeningRipples`, the lifetime and clock period
  mirrored in C# and HLSL (`SdfScreenRipple`), and the amplitudes, speeds,
  glow and idle waves as literals in `sdf-environment.hlsli`. Design one
  `ripple` material carrying feel and look, with the standard's values as the
  default; it changes the GPU data layout.
- [medium] The procedural avatar catalog owns a fixed look (`WorldRigCatalog`:
  the humanoid's part table, leaf counts, bound radius and anchors). Design
  how a catalog rig is authored, with this humanoid as the standard's default
  data, before moving any of it.
- [medium] `WorldOverviewCamera.Resolve`, the view of a world with no local
  seat or camera, bakes in its eye offset, target lift and field of view. Move
  them into the standard's camera program over a neutral framing.
- [medium] The clearance op: a blend falls back to whichever endpoint authors
  clearance regardless of weight (`SdfCameraProgram`); bound fade and
  target-migration bands skip the ordering checks literals get
  (`WorldDefinitionValidator.Screens.cs`); bound `eyeRadius` and `surfaceSkin`
  fall back to zero (`WorldCameraRigCompiler`); `closeTargetOffset` is not
  bindable; a selected case's zoom and pitch limits follow the default case.
- [medium] The chase camera's tight-space preference is one world-wide row
  (`cameraClearance`, `worlds/standard/camera.puck`), shared by every seat and
  no longer travelling with a joined identity. A per-seat preference needs a
  camera program's `state.<row>.$body` binding to resolve against the framed
  body (`StateBinding.BodyKey`), which the rig compiler's scalar slots do not.
- [low] The effector solver's ease in and out uses the drivers' default blend
  (`WorldGaitDrivers.WeightBlend`, `WeightSeconds`) with no authored time, and
  `ProbeBackoff` is 5 cm whatever the body's scale (`WorldEffectorSolver`).
- [low] The gait drivers' `moving` gate is a fixed rendered speed
  (`WorldGaitDrivers.MovingSpeed`), and mirrored avatars advance gait phase by
  a fixed cadence and travel clamp (`WorldMirroredAvatarBand.GaitCadence`,
  `MaxGaitTravelPerFrame`). One design pass on creation-driver authoring
  decides whether each is a driver or rig field (`CreationDriverDocument`).
- [low] The tabletop framing clamps an authored engage pitch to
  `MaximumPitchRadians` (`WorldTabletopFraming`). Confirm the validator's
  `WorldTabletopEngage.Pitch` range stops below it, and report a pitch above
  it by name.
- [low] Flight and fuel are authored only in the Moth
  (`worlds/debug/avatars/moth.puck`); the assisted-flight pass is a standard
  ability template, as `dashAbility` is.

The standard's own surface:

- [ordinary] The standard package's families as a library (the owner's answer:
  the library is this repository;
  [decision](../decisions/engine-design.md#the-creation-library-is-this-repository)).
  Each is a step with its check:
  - `puck creation stats` prints a creation's field extent and ground contact.
    Check: `puck creation stats --world worlds/debug/debug.puck --prototype
    lawn-oak-0` prints an extent, and the walker's reads its authored height.
  - Declared controls and presets on the standard's families: a parameter's
    unit, range and meaning, and named presets. Check: the lawn's three oaks
    are presets by name with unchanged pins, and `puck lint` refuses an
    argument outside a declared range by name.
  - The compiler records the template and arguments that produced a prototype,
    in the compiled document beside its pin. Check: two worlds' expansions of
    one call show one recorded expansion and one pin; a hand-written prototype
    shows none.
  - A shared role table in the standard that every family reads by default.
    Check: one override by id recolours every foliage family in a scratch
    world, and the mesh store gains no entry.
  - Creations in the source-attributed cost report (`WorldCostAnalysis`), with
    a bake-independent estimate per item, since shape count is no cost proxy.
    Check: `world.budget` names the `.puck` span that wrote the Moth's largest
    group.
  - A browser of placeable creations: the gallery is hand-built, and nothing
    lists what a package offers to place.
  - Named attachment frames are
    [the modeling plan's S5](modeling-authoring.md#5-implementation-slices).
- [low] The inline mesh path (`WorldPrototypeMesh`) is validated, pinned and
  extended by the transmission plan's `solid`, and no world authors one. It is
  a product capability nothing calls, so it is wired or covered, not deleted
  (AGENTS.md, "Unreached is not unwanted"); the transmission plan is its
  nearest owner.
- Overlay text is spread too wide: each character advances a padded atlas
  cell, not the font's advance. Decide whether overlay mono text renders at
  its nominal size (a 12 px run's em is 10.4 px today).
- The standard's binding bar slot set (`worlds/standard/controls.puck`, the
  `bare` overlay) still lists keys only the Moth binds; they show as unbound
  plates in a world without her controls.
- In `worlds/minimal` at 1280×900 the standard's editor bar (`editor.bar`)
  runs past the window's right edge with no icons, and the resting page draws
  no bar; look at both on the GPU.
- A windowed boot of a world with no `host` section opens a 120×1 window
  (`WorldHostDefaults.Absent`): refuse it by name, or let the standard author
  the host default.
- [low] `screenMaterials` has no `world.row` section
  (`WorldRowCommandModule`), so a material is tuned only through its source
  and `world.reload`. Add it with an upsert and remove mutation keyed by `id`.
- Look at the controller bar on the GPU after the modifier-row and opener
  change, at 3840×2160 and with a PlayStation and a Switch pad; in the garden
  and the night field RB opens no wheel and reads unbound.
- Helpers that belong in the standard, transform maths first: rotation and
  transform maths (turns in degrees, axis-angle, composition, mirroring); a
  mirrored-pair template; a toggle ability beside `dashAbility`; a tabletop
  kit (grid board, cell placement, piece prototypes), which chess, Go and
  Reversi each wrote; a named base-materials palette; scenario-step templates
  (tap, hold, wait until settled); the pad slot list and a mirrored bar-table
  template in `pad-kit`.

### Port the experimental worlds' missing features

Owner: debug-world content lane (puck-world, puck-dsl, sdf-authoring) ·
ordinary, the owner's direction · Next: the authored render passes (lane 1)
and the second seat (lane 5) ahead of parallel view recording, then the quilt
(lane 2) with the streaming plan's areas-and-tiles step, in
[the plan's order](port-experimental.md#lanes-in-order). Each lane opens by
confirming its site in the debug world and its work-budget share.

- [The plan](port-experimental.md) holds the inventory: each feature's
  experimental source, its support (wire, standard or gap), its place and its
  size. Almost every feature is built and tested, so a port is wiring
  documents to what exists; the one gap is a region-scoped pair primitive for
  the combat yard.
- The quilt, the island and the One World are not re-admitted (the owner's
  ruling). A port lands as `.puck` content in `worlds/debug/`, or in
  `worlds/standard/` where it is a reusable design, and is verified by running
  the debug world.

### Card tables in the board game room

Owner: debug-world content lane (puck-world, puck-dsl, sdf-authoring) ·
ordinary, the owner's direction · Next: the windowed look at the Klondike and
FreeCell tables, close and wide, with the script in
`worlds/debug/cardtable.md`; then Spider.

- [medium] The standard's deck departs from the true-size ruling:
  `cards.puck` authors a card 1.76 wide and the table's look scales it
  (`cardScale`), so the card bakes at the resolution of a 1.76 m object and
  draws at 10.6 cm. Author the deck at its real millimetres;
  `puck mesh audit --prototype card-h-k --cpu` shows the triangle count fall.
- Spider as the same table's game switch (`solitaire.puck`): it lays its piles
  on the `kSlots` graph after FreeCell's and answers the seat through its own
  mailbox. 104 cards need 52 more bodies and a 60-cell-wide grid for ten
  columns; a body wears one face for good, so it is the four-suit game; the
  rules are ten columns of same-suit run moves, ten stock deals and a
  completed pile. It takes what the work budget leaves after the chess search.
- Poker: seats, computer players and the hand judge. Its hidden hands read
  right only through per-seat sessions.
- Prior art still quarantined, which
  [the port](#port-the-experimental-worlds-missing-features) inventories:
  `experimental/src/Puck.World/Assets/worlds/games/` holds Spider, poker with
  a hand-strength judge, backgammon, dominoes, mancala, hidden ranks and word
  spy, and `experimental/tests/Puck.World.Games.Tests` holds laws as prior art
  for `test` blocks.
- The [disclosure plan](disclosure.md#the-build-order)'s four fixture games
  stand at their own tables (`debug.go auction`, `shedding`, `deduction`,
  `skirmish`). Owed: the windowed look, the shedding racks' card orientation,
  and physical units on the skirmish board, which needs a body or a `respond`
  placement per cell. The language walls they hit are
  [S11's G17](state-and-language.md#s11--ergonomic-is-optimal).
- [high, a server specialist's step] Per-seat table sessions
  ([the tables](../decisions/worlds-and-federation.md#the-tables)). A screen in
  a player's room observes a nested destination as the room's authority into
  the room's mirror (`WorldServer.TryObserveAsSession`), so a seat's cards
  either pass through the room host's process or never reach the seat. A
  seat's own authority opens a session with the table, the table delivers the
  seat's private view to the seat's client, and the room's screen observes the
  public tier. Done when the room host's mirror holds no cell of any seat's
  hand while each seat's client renders its own. Needs
  [a network peer into a spawned place](#transfers-federation-and-server).
- Tables with stakes: the vouched table role, stakes reserved at the
  settlement door with the dealer named in the reservation, per-participant
  commitments on draw sites (a draw's seed folds only the document seed, the
  instance identity and the site descriptor today), and the transcript
  policy. Needs per-seat sessions and
  [settlement](#settlement-trust-roles-and-revocation). The stake rows serve
  every product row, so the owner's deferred casino call waits on nothing
  here.
- The rules' work-budget cost is the table's price (`world.budget` against
  `RuleCapacity.MaxWorkUnitsPerTick`): a transfer between named piles costs a
  few thousand units where one between piles a live zone table picks costs
  about 140,000, so every deal, gather and move is a rule pinned on its
  literal piles, and the table reads one pile a tick (`kSync.scan`).
- The cards are posed bodies, not rigid ones: the rules' door judges each
  request over the piles and the table follows. A hand lifting a card with
  `body.carry` would be a second input path beside the cursor.

### Debug tools

Owner: debug-world tools lane (specialist) · medium · Next: the windowed
checks and the developer exhibits; the diagnostics (findings, pins, goto,
annotate, the `probes` and `exposure` views) are
[editor E6's](editor.md#open-items).

- [medium, D2 live probe] Loading a saved debug-world snapshot through
  `world.load` exceeds `WorldFrameCodec.MaxPayloadBytes`'s 16 MiB Rebuild cap
  (`src/Puck.World.Protocol/Protocol/WorldFrameCodec.cs:50`): the camera probe's
  canonical payload was 21,832,203 bytes and was refused before submission.
  Make whole-world replacement scale through the existing content-addressed
  document transport; do not raise an arbitrary cap or exempt local callers.
  Direct boot of the same snapshot does not use the rebuild envelope.
- Restore and inspect the remaining developer exhibits: the rate range, the
  traversal course's geometry and the kinematics scope. Clean bot verdicts do
  not show that they look or measure correctly.
- The scene diagnostics' [windowed checks](../reference/commands.md#scene-diagnostics):
  the deliberate mismatch, hovering and grounded-on-air scenarios. Retained
  dynamic supports, field-provider support witnesses and interpolated body and
  render pose pairing remain unknown coverage.
- [low] A pick on a screen's face names `screen=<index>` and no placement, a
  derived face does not name the placement it belongs to
  (`WorldStaticSceneEmit` registers no pick target for either), and a pick on
  segments no instance owns names nothing.
- [low] `view.override camera swallow-portrait` echoed success but left the
  seat's camera on screen. Find why a named world camera row does not replace
  the view.
- `gallery.inspect`'s values (60 lux fills, EV100 7) are first picks tuned on
  one exhibit; a flat ambient for baked meshes would replace the fills.
- The GPU looks owed at the gallery (the warps II row, the hall's light
  switches) and the `puck mesh audit` gaps are the
  [rendering plan](rendering.md)'s.

### Instrument and dance play

Owner: one feature lead lane, which holds every lane of
[the plan](instrument-play.md) to its stage's checks · high (an owner-requested
feature) · Next: the plan's first wave, all at once: the research round, the
concept pack, the engine-gap lanes (pose clips, the timed turn, the beat index
and calibration, delivered cues, haptics) and the standard package's judge.

- The feature is accepted only by the end-to-end capture series of
  [stage 6](instrument-play.md#stages-and-acceptance) against the owner's
  words and the concept pack, never by its lanes' separate evidence.
- Its engine gaps are lanes of its first wave and are named in the packets
  that own their areas: [the Moth's motion](#dual-stick-play-and-the-moths-motion),
  [input channels](#input-channels) and [media and capture](#media-and-capture).
- Its last stage, a crowd of performers at the frame bar, waits on
  [virtual geometry](virtual-geometry.md#d2-bounds-for-skinned-and-multi-slot-draws)'s
  bounds for many-part bodies and on the benchmark's remote route.
- The rig's caps on swings, slides, drivers and effectors are removed
  ([the caps](instrument-play/pose-clips.md#9-the-caps-come-off)), and a
  world's own creations pay a counted cost (`sdf.creations.terms.evaluated`,
  `sdf.creations.effectors.solves`). A look that arrives with a
  visiting character is work another player's client must do, and nothing
  budgets its terms, effectors and transform slots. Admitting a visiting
  look's rig waits on that budget, which the plan that admits visiting looks
  owns.
- It is the first of the [play expressions](play-expressions.md). The other
  two the owner named, play by solving and play by calculating, are open
  explorations: that page holds
  [the ideation round's synthesis](play-expressions.md#the-synthesis), and
  nothing is built for them until the owner answers
  [its four questions](play-expressions.md#the-questions-the-owner-chooses).
  The synthesis asks the pose-clip and delivered-cue designs to be written
  for the whole family
  ([what the bard's first wave should take](play-expressions.md#what-the-bards-first-wave-should-take-from-this)).

### Dual-stick play and the Moth's motion

Owner: play-feel lane, with the owner's feel sittings · ordinary · Next: the
reversal brake, then a sitting.

- [high] Engine gaps [instrument and dance play](instrument-play.md#the-engine-gaps)
  names, each with a lane in that plan's first wave and ahead of further rig
  or move authoring: a creation's poses do not blend and a driver's weight
  cannot be limited to some shapes (`FrameDocument`, `CreationDriverDocument`);
  an effector cannot aim at a point on its own creation
  (`CreationEffectorTargetDocument`); no driver reads the seat's input, so a
  press reaches the rig a tick late; and no motion operation turns a body
  through an angle or moves it along an eased path over a duration
  (`BodyMotionOp`, `WorldEffect.PlanarImpulse`).
  [Pose clips, body parts and reach](instrument-play/pose-clips.md) designs
  the first three, and its steps are the build lane's order; steps 1 to 8 (the
  compiled rig, its reads, the caps, parts as sets, adding clips, masks and
  replacing, reach and influence, and the guess) are built, so step 9, the
  Moth's hand, is next. Step 8's guess is a pose layer the guessing seat's own
  view alone reads; its windowed reading (the press drawn early in that view and
  in no other view, camera screen or session window) waits on the lead's batch.

- A reversal should brake faster than it accelerates: `ShapeWholeVelocityRow`
  converges at the engage rate whenever the stick is deflected, so a full
  reversal at 40 m/s² takes 8 ticks at 5 m/s.
- [medium] Finish the controls visual checks in a windowed batch: the
  extraction comparison beside the zoom and reset, the dash lean and thruster
  flare, both bar layouts and the compact and full stills, the same on
  Vulkan, standing and walking frame time, and a patrol of the stage.
- The Moth's directional motion is authored rig data
  ([how the moth moves](../../.claude/skills/sdf-authoring/references/rig.md#how-the-moth-moves)).
  Open on it: starts and stops blend over the driver weights and the replant
  settle, and a stride that stops mid-swing eases to rest rather than
  finishing the step; a run's stance is shorter than the plant weight's ease,
  so a planted foot is neither fully on its latch nor level and the boot
  lifts late in stance; walking backward, her legs cross behind each other.
  Her hip, trail, lean and stride values are first readings for the owner's
  sitting.
- Keep the run channel held through the motion scenarios' steady-run
  captures: `body.press run 1 4` is capped by the default Drive grant to two
  seconds, so `run-steady` and `pilot-run-steady` sample the return to
  walking. Re-press within the budget or grant the session the duration.
- The camera: tune per-form eye and close-target points; the tight-space
  course's three-up `camera-compare` layout and a live mode toggle on the
  Debug page remain to author (`camera-course.puck`).
- A rate-limited facing op in place of `SnapYawToPlanarIntent`'s instant
  heading is wanted only if a movement-facing or pointer-steer scheme returns.
- [low] A timed impulse integrates only inside
  `IntegratePlanarAndVerticalVelocity` and `IntegrateScratchVelocity`, so on a
  body whose motion program has neither op a `planarImpulse` waits and fires
  when the body next runs an integrating program. Drain or refuse the overlay
  there.
- [low] `dashPose`, `airPose`, `airWings` and `bootBoost` are each one cell
  keyed `"0"`, so only body 0's dash drives the lean and flare. Key them per
  body together.
- [low] A requested boom distance replaces the orbit op's distance outright
  (`SdfCameraProgram`'s `Evaluate`), and `WorldSeatViewState.AuthoredDistance`
  reads the first orbit op's literal, so a scalar-bound orbit distance stops
  responding after the first scroll.

### Input channels

- [medium] Free Cam admits only local seat owners: `WorldInputRuntime.TrySetCameraRoute`
  requires a Seat principal whose index equals its body below LocalSeatCount.
  Federated, routed peer and session input owners cannot enter the camera state.
  Resolve the owner's camera placement and application through its admitted body
  and route, rather than assuming a local seat index; keep the actor's Drive and
  Control checks at the authority.

Owner: input lane · ordinary · Next: the typing and chat exhibits and live
hardware. The [input channel decision](../decisions/input-channels.md) owns
the model.

- [medium] A seat's intent depends on presentation frames when its view is
  `auto`: automatic first person (`WorldCameraViewTransition.Advance`, from the
  chase clearance probe in `WorldSeatViewState.Clear`) advances per presented
  frame, and `FirstPersonLook` decides whether `WorldClient.ComposeSteer`
  writes the body's facing from the camera and whether the follow camera runs
  (`AdvanceSeatView`). A tight space can therefore change the input a windowed
  seat submits against a headless one, and with the frame rate. An explicit
  `player.camera.view first|third` is frame-independent. Advance the view's
  input-relevant decision on the fixed tick, as stick look already is, and
  leave the boom's easing to the frame.
- [medium] Free Cam has no entry handoff: the camera body rests where it was left
  (`worlds/standard/free-camera.puck`'s `rest`, 40 m over the debug stage at first),
  because a rule's `pose` effect takes a literal position and nothing copies the
  seat's eye to the camera body on entry. Add a pose-from-body effect or let the
  camera application start the body at the perceiving pose. The play wheel's four
  rings are full (`debugRings`), so Free Cam is F9 only; a pad control needs a
  ring or a page entry.
- [medium] Cartridge rumble is unwired: `WorldHapticDirector` now drives
  `IGamepadOutput.Rumble` from authored `haptics` rows (with
  `worlds/standard/instrument.puck`'s beat pulse as the first authored consumer;
  [instrument and dance play](instrument-play.md#sound)), while cartridge
  rumble (`IFeedbackMachine.MotorLevel`) still has no consumer outside
  `Puck.Machines`. Wire a machine's motor to the engaged seat's pad.
- The in-world computer runs programs (the owner's direction): the desktop
  share is one and a typing tutor another, switched on the computer itself
  with the screen showing which is live. The tutor is world-native: lessons
  from the home row to sentences, per-character feedback, words per minute,
  per-key accuracy and a run timer, judged from input history so a replay
  reproduces every keystroke. The in-world keyboard follows the real one by
  scan code, keys sinking on press and the next expected key glowing. Order:
  the keyboard's press animation, the tutor, program switching, then lighting
  a real RGB keyboard's keys on the device-output path.
- Chat typed in the world: a HUD prompt that enters a text-focus mode and
  submits through `chat.whisper` and `chat.log` (`ChatCommandModule`).
- Cross-check the Triton's reports against several independent sources (SDL
  is one second opinion, never the specification), then rewrite the input
  reference's Triton section to cite them:
  [iczero/steam-controller-stuff](https://github.com/iczero/steam-controller-stuff),
  [CouchTurtle/sc2-research](https://github.com/CouchTurtle/sc2-research),
  [anderstvoss/virtualgamepad#159](https://github.com/anderstvoss/virtualgamepad/issues/159),
  [mwdmwd/sc26re](https://github.com/mwdmwd/sc26re),
  [ded811/CounterPuck](https://github.com/ded811/CounterPuck),
  [jahrekza/steam-controller-plasma-widget](https://github.com/jahrekza/steam-controller-plasma-widget),
  the Linux `hid-steam` controller patches and SDL. Decide report 0x7b from
  their agreement and a live capture on the owner's receiver, and check
  whether 0x79 (wireless events) reaches the parser.
- Hardware runs owed ([device input](../reference/input.md#limits-and-hardware-evidence)):
  the Triton receiver's live streaming roster; the DualSense and Switch Pro
  over Bluetooth, the DualSense Edge, one DualSense on USB and Bluetooth at
  once, the classic Steam Controller; the Triton's reports `0x45` and `0x47`,
  pairing events, its wired, Bluetooth and Nereid transports, trackpad
  haptics and the firmware watchdog setting; the Switch Pro's HD rumble at
  other carriers and its Home LED patterns; an Xbox pad with a headset.
- The engaged screens on the GPU
  ([engagement](../../.claude/skills/puck-world/references/engagement.md#the-framed-screen--the-camera-frames-the-engaged-screens-picture-as-an-emulator-shows-it)):
  the framed screen at whole pixels through a wide, a tall and a maximized
  window, on both bricks, with the matte cutting at the ease's settle and a
  `views.graphs` post pass not faded by the screen fidelity; the desktop with
  a pad ([desktop](../../worlds/debug/desktop.md#with-a-pad)), where it is
  unproven whether a pad reads while another window is in front. The engine
  has no on-screen keyboard, and the `engagement` context family is published
  only by the windowed simulation.
- [low] The input decision says a tape reports names from its captured
  definition, while the input skill's references say a segment's raw bank is
  never labelled. Reconcile the decision with what the tape can name.

### Machines as authored logic

Owner: machines lane (gaming-bricks, puck-world, the job-system owner for
scheduling) · ordinary · Next: measure a brick fleet inside a running World,
then design step-until-change wiring and its pricing. The owner's question: an
author uses many hosted brick machines as programmable logic, as builders use
redstone; the circuit is the author's, and the engine supplies scheduling,
wiring, dormancy and cost.

- Measure brick machines inside a running debug World on the lead machine and
  the Steam Deck, at full speed and as mostly-dormant circuits, reading the
  World's frame and tick beside the fleet's throughput
  (`MachineFleetStepper`'s bare benchmark assumed the whole CPU).
- Price machine work in `world.budget`: confirm whether stepped machine cycles
  reach it, and attribute a circuit's cycles to what the author placed.
- Step until change: a quiescence-driven step with a bounded cycle budget per
  tick, on the existing snapshot, restore, fork and simulate-on-demand
  dormancy.
- Wiring: one machine's output to another's input as authored data
  (`LinkSession` already connects bricks), deterministic within the tick,
  with a cycle order under which serial and parallel agree.
- Scheduling: independent machines in parallel through the job system, off
  the frame thread.

### Panes: split screens the player drives

Owner: specialist lane (puck-world views, rendering graphs, input) · ordinary ·
Next: a machine-screen source for a pane. The owner's direction: the Moth in
one pane, the desktop in another, the HGD in a third and another camera angle
in a fourth.

- A machine's screen as a pane source: machines render as
  `source$machine$<id>` graph nodes, but a `views.graphs` row can name only
  `source.capture`, `source.passthrough` and `source.qr`. Add the package,
  finding the existing machine-screen producer first.
- Switching layouts as a player: a binding or controller-bar action, beside
  the `view.override layout` verb.
- Pane focus. Decide (the lead's call): focus is simulation state, one pane at
  a time, moved by a binding or a click, so each input stream has one
  destination and the in-world computer's program switching reuses it.
- Arranging panes at runtime beyond the authored layouts: decide once focus
  and switching land.

### Screens on curved and general SDF surfaces

Owner: specialist lane (rendering, sdf-authoring, puck-world) · ordinary ·
Next: one projection end to end, the sphere the owner asked for, then
cylindrical.

- Today a screen is a flat slab (`ScreenSlab`, `WorldScreenMappings.Normalized`).
  Decide (the lead's call): a screen on a shape is an authored projection
  frame evaluated on the renderer's exact hit, with the same projection in
  fixed point for pointer input, so a click on a globe lands on the same
  picture point live and in replay; a UV atlas from the bake only if a shown
  shape defeats projection.
- Projection kinds beside the planar one: spherical, cylindrical and, on the
  owner's call, cube.

### In-world editing

Owner: specialist lane (puck-world, build and hot reload, input) · ordinary ·
Next: after edit to running's invalidation contract, pane focus and the
in-world computer's programs. The owner's direction: edit on an in-world
screen and see the change hot reload.

- Record the code revision in replay tapes and checkpoints
  (`Puck.Abstractions.CodeRevision`); a replay or restore across a revision
  refuses with the reason.
- A rude edit resumes rather than reboots: checkpoint the World, rebuild,
  boot, restore, so the player lands where they were.
- An editor program on the in-world computer, talking to a language server:
  `.puck` first (`puck lsp` exists), then C# through a Roslyn language server,
  showing the outcome on its screen. `.puck` edits are data, replayable and
  need no compiler.
- Development only: editing C# runs arbitrary code, so it needs the SDK and an
  explicit grant to the owner's seat; published builds do not hot reload.
- A seat that edits the world's own document is its operator:
  [decision 3a of the disclosure plan](disclosure.md#3a-a-declaration-is-judged-inside-a-scope-its-declarer-reads-whole)
  designs that as an `Operate` grant the world gives, in place of the console's
  principal kind.

### Media and capture

Owner: media lane · ordinary · Next: let an unread capture feed idle.

- [medium] The desktop capture feed scales frames nobody reads: its thread
  spends about a core in `Win32GraphicsCaptureDevice.ScaleMapped` in a World
  started with `--devices none`, and a visible share of a cold bake's
  processor samples. Find what subscribes it, let an unread feed idle, and
  downscale on the GPU ahead of the readback.
- [medium] `CaptureSink` is constructed nowhere in `src`: an unreached capture
  capability to wire, not delete (AGENTS.md).
- [low] The authority's score and a listener's score can diverge. `WorldTick`
  steps its `MusicDirector` (`StepCore`, beside `MusicClock.Advance`) on every
  authority edge before the rules run, and never on `MusicSenseFamily.RuleCue`,
  while each listener's director (`WorldAudioDirector.PresentDeliveredCues`)
  steps on its disclosed senses and its delivered cues. A rhythm rule judging
  `$clock:` against the authority's segment can therefore judge a segment the
  player is not hearing, and a segment moved by an edge the player cannot see
  reaches input owners through the captured segment id. The fix decides which
  score the authority judges against (a per-owner director, or a score whose
  conditions read only what every listener is told) and steps rule cues on the
  same tick the listener does.
- [low] A delivered cue, beat or bar boundary defeats a projection's
  `UpdateSeconds` throttle: `WorldProjectionSampler.TryProject` delivers any
  snapshot that carries one, so a score's beat forces a full snapshot on a
  throttled presentation stream. Carry one-shot events across throttled ticks
  (cues queued, boundaries folded with their beat count) and deliver them at
  the stream's cadence, or on a lane of their own.

### Debug world content

Owner: debug-world content lanes, each started by the owner's direction ·
ordinary · Next: the agent harness and embeddings as features, then new
content on the owner's call.

- [medium] The agent harness and embeddings are unexercised by the debug
  world. Build them as features that load the systems as real use does: a
  lobby guide run by `Puck.World.AgentHarness` that walks visitors to places;
  a "where is something" question answered by ranking places by embedding
  similarity (descriptions locked with `puck embed`, the visitor's text
  embedded outside the tick); and an agent playtest participant that tours the
  world and reports what it cannot reach or read. The wiring runs on the
  `puck-fixture` generator and a stub chat client.
- [medium] The streaming campaign's scale workloads and walk
  ([the seam walk](streaming-and-memory.md#the-seam-walk)): a hub of large
  worlds and a crowd of distinct looks; the walk's three crossings are the
  debug world's `seams` route.
- [medium] The lift (`worlds/debug/hall.puck`), the owner's example of polish
  as engine systems doing real work, on the owner's go: its displays as one
  emulated machine running an authored cartridge, mirrored to every panel and
  driven by the lift's state cells, after confirming a generic
  world-to-machine channel (the emulated link port is a candidate); its ride
  as an authored motion profile with easing and a settle. Today the panels
  are baked SDF variants swapping material slots.
- Tabletop chairs (`engage.seats`, `WorldTabletopFrame.SeatSide`) leave the
  avatar where it stood: sitting should pose the body at its chair, and a
  chair needs an occupant, which local and remote play at one table, and
  addon seats at the tables, want. The
  granary board deals its pieces in cell order, right to left from its -Z
  chair.
- The faerie: small, abstract and original beside the Moth, its animation
  rich and reactive, dodging a swat through one generic threat input
  (`companion.threat`) a body or the seat's pointer drives today and a
  camera-tracked hand later. The seed-eye build is a first pass.
- Camera as controller (the owner's stretch goal): the faerie flies through a
  portal into a camera world a real camera drives through the probes system.
  A track advanced by the tick would let a recorded track steer state
  deterministically.
- The suit toggle's handheld: only body 0 holds the brick (`pilotHold`
  declares the one key `"0"`), only while standing still, and its glass shell
  reads near white in her hands (a pooled creation's glass may lose its tint).
- A frameless portal, authored and not built
  ([seams](../decisions/worlds-and-federation.md#seams)): a `threshold`
  template in `worlds/standard/`, a box face that draws no frame
  (`WorldFaceApertures`), beside the gallery paintings. No C#.
- An elements-and-combat yard toward MMO action combat
  ([decision](../decisions/engine-design.md#combat-is-world-content-on-generic-queries-fields-and-input)),
  after the job system: spatially scoped pair queries on `WorldInteraction`'s
  `Distance` kind over `FixedSpatialNeighborhood`; action input on the ordered
  events (an eight-slot hotbar, dodge, target lock; audit
  `AddonAbi.MaxChannels` first); the yard itself, `worlds/debug/combat.puck`,
  with a target-and-strike station, an elements station and hazards, the Moth
  casting through her gauntlets; then two clients on one authority with
  speculative local movement replayed on correction. The yard also carries the
  doors station of [portals in play](portals-in-play.md#8-the-debug-world-probe):
  sentries that sight and fire through doors a player places.
- Weather, particles and a climate wing
  ([decision](../decisions/engine-design.md#weather-is-a-regions-composition-of-generic-pieces)),
  after the job system: six sealed glass-walled bays east of the hall (calm,
  rain, snow, sand, ash, embers), each a local region. Named regions are in;
  then, each finished before the next: the annex shell with the calm and rain
  bays (blocker maps, the particle pool, wetness, puddles, runoff); analytic
  particle quads after the opaque resolve with regional media; cover, tracks
  and the snow bay; wind erosion and the sand bay; shared world recipes with
  the ash and embers bays; the combat yard and the hall's window band on
  outdoor weather; all six bays at the frame bar on both backends, then the
  Deck.
- "Puck Beats HGB" (the owner's goal): a generic construct that learns and
  beats arbitrary HGB games on the machine debugger addon. Training runs
  offline, and the learned policy is frozen into an addon that is a pure
  function of its inputs; start with one committed test cartridge.
- The granary board ([granaries.md](../../worlds/debug/granaries.md)): the
  live feed's accounts are all one kind, so every live piece deals one look;
  the pieces are dealt placements, so a seated player cannot pick one up.
- [low] The rate range's lane (`worlds/debug/range.puck`, `laneX`) spawns
  beside the river's centreline and `range-walker-10`'s loop crosses the
  bank; move the lane onto open ground.
- [low] The alchemy recipe book's last lines run behind the brass rail at the
  screen's bottom. Fit the text runs or move the rail.

### Quarantined gates

Owner: the lead, on the owner's call for qualification · dormant · Next: none
until then. While the debug world is the one live world
([decision](../decisions/engine-design.md#one-live-world-while-the-campaign-runs)),
these gates have no live fixture; their fixtures, laws and ledgers sit under
[`experimental/`](../../experimental/README.md), and each becomes a packet
when qualification is called.

- Real-World canaries (`puck canary`): one live canary remains,
  `tests/Puck.World.Canaries/self-update`. When the rest return, the runner's
  listener work finishes: a single companion authority still receives a
  pre-picked UDP port, and the stub-manifest branch still builds
  `Puck.Launcher.Stub` in place.
- Cross-backend parity (`puck parity`), the only automated gate on the shared
  engine contract. The debug world authors no `captures` rows and has no
  contract file. On the call: author its captures and contract, record them
  on the RTX 4070 and point the gate at them, after lighting and body scale
  land; the rendering plan's owed GPU legs wait on it.
- Cross-host determinism (`puck determinism`): every scenario's tick, system
  and document hashes.
- Counted render work (`puck counters`): the floor, Nexus, courtyard and sky
  workloads; the verb's default shadow workload and the seated-avatar workload
  stay live.
- Release qualification's functional canaries and its island and ink-pipeline
  workloads; the live profile runs only the debug world's stability matrix.
- World baselines (`state`, and the tictactoe hashes of `browser-parity`),
  `puck test` verdict worlds, and the laws that booted a quarantined world.
- The dashboard portal's real-engine tests, which return when the live tree
  authors a fragment and a basis.

## Awaiting the owner's review

Ideas discussed with the owner and not yet queued. None starts until the owner
reviews it and says so (the owner's ruling).

- **A portal placed on a moon.** A player puts one portal on the ground and
  its pair on the moon "and totally run with that instead of simply making it
  a set piece". The moon in the sky is the moon one visits
  ([valley and moons](valley-and-moons.md)), so it needs a portal pair opened
  at runtime by a player's action as an authored rule, aim at a surface
  rendered at astronomical distance, and a crossing between worlds with
  different gravity. Whether the moon admits a visitor's portal is the moon's
  own grant. The owner has since ruled that players place doors in play, and
  [portals in play](portals-in-play.md#4-a-door-onto-another-world-opened-in-play)
  designs the mechanism: the moon offers sockets and grants a visitor's offer.
  The moon door itself, as debug-world content, still waits for the owner's
  word.
- Exposure per place and the tone curve left this list. Both are look values,
  which authors set and the owner is not asked to choose. The mechanisms
  they need, a blend of exposure across a doorway or a portal and the tone
  curve as authored data, are engine work in
  [rendering research](rendering-research.md#looks-are-authored). Which
  curves the standard package and the debug world then author is theirs.
- **A maths room** in the gallery: a 2D grid board and a 3D floating lattice
  whose cells `Puck.Maths` computes in the simulation. Audience, animation,
  interactivity and which maths leads are the owner's questions.
- **An optional self-restricting addon**: it detects whether a player is
  automating their gameplay with Puck, identifying the game from a catalog
  and judging through the addon system's observation grants. The catalog is
  external data outside simulation state, and the addon is opt-in.

