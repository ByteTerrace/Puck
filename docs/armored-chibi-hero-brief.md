# Armored chibi hero: implementation brief

Create an original, expressive armored hero whose silhouette, materials, and movement make Puck's main character feel deliberately designed. The character must look excellent in the running game, from the normal camera, using Puck's signed-distance renderer and authored world data.

The user wants chunky anime armor and chibi proportions, with Firefall and Mega Man X as broad directional references. iq's [Selfie Girl](https://www.shadertoy.com/view/WsSBzh) is a reference for the quality of character form, expression, and finish achievable with SDFs. None is a design to reproduce. The earlier [Rainforest](https://www.shadertoy.com/view/4ttSWf) reference establishes the wider visual ambition; terrain and sky implementation are separate work.

This brief specifies a proposed execution direction, not an owner-approved character design. Its concrete defaults let an implementing agent proceed and produce a reviewable result without asking the user to make every art decision. Later user direction supersedes them. Complete the stages below and judge each stage before proceeding; checkpoints are self-review gates, not requests for repeated permission.

The current owner-selected prototype is **Moth**, from the subsequent character
collage: ivory/lilac armor, warm brown skin, an open hood, a side braid, and folding
flight vanes. Virtual On and Power Stone join the broad style references. These
choices supersede the island-ranger palette and helmet defaults below. The
[Moth studio guide](../src/Puck.World/Assets/worlds/moth.md) owns the current authored
model, controls, and live iteration recipe; the quality priorities here still apply.

## 1. What success looks like

At normal gameplay size, the character reads immediately as a compact, capable adventurer in substantial powered armor. The head has personality; the armor has structure; the joints visibly explain how the body moves. From behind, the character is as recognizable as from the front. Walking feels agile, stopping feels controlled, and landing communicates weight.

The improvement must survive neutral lighting, ordinary island lighting, motion, and reduced render resolution. A beauty shot is supplementary evidence. The deliverable is the playable, integrated character.

Order decisions by this priority:

1. Silhouette and proportion.
2. Pose, joint placement, and movement.
3. Large color regions and material separation.
4. Face and a few identifying details.
5. Lighting refinements and small surface details.

If a later choice harms an earlier one, revise the later choice. Extra detail cannot repair a weak silhouette.

## 2. Art direction: the island ranger

Use **island ranger** as a working description, not new game lore or an engine type. The personality is alert, resourceful, and quietly confident. Favor athletic readiness over military aggression or baby-like cuteness.

### Proportions

Use total standing height `H` as the design reference, measured from the sole to the helmet crown. These are starting ratios to tune through rendered comparisons, not schema fields to invent.

| Feature | Starting target | Visual purpose |
|---|---|---|
| Helmet and head | About `0.30H`; roughly 3.3 heads tall overall | Clear chibi identity and room for expression |
| Maximum shoulder span | About `0.48H` | Substantial armor without hiding the head |
| Pelvis span | About `0.24H` | A visibly narrower center between chest and boots |
| Hip joint height | About `0.40H` above the sole | Compact legs with enough joint travel |
| Each boot | About `0.15H` wide and `0.23H` long | Weight, direction, and a stable planted stance |
| Forearm armor | Visibly thicker than the upper arm | Readable powered-armor silhouette |

Fit the authored character to the existing body's scale and foot origin. Document the actual author-frame axes, dimensions, and pivot convention before placing shapes. Read the geometry unit contract: a primitive's scale can encode dimensions differently from another primitive's scale. Do not assume every scale is a radius or a generic multiplier.

### Shape language

Use rounded wedges, broad beveled plates, tapered limbs, and deliberate dark recesses. Keep enough flat-looking area to catch a broad highlight. Curved geometry should still have a clear front, side, and edge.

Three identifying features should carry the design:

- A low, swept helmet brow framing a visible expressive face, with compact ear housings. Avoid recognizable franchise crests or copied helmet markings.
- A short split chest collar around an offset inset badge, echoed by the back's compact two-piece power housing. The motif must remain readable in rear three-quarter view.
- Large forward-shaped boots and compact wedge gauntlets, tied together by the same edge treatment. One shoulder may carry a slightly larger utility plate; keep the underlying body and gait balanced.

Separate armor from the undersuit at the neck, waist, elbows, and knees. Overlap plates where joints need coverage. Preserve a visible gap between arm and torso in the resting pose. Use a few strong shapes for the gloves rather than separately modeled fingers.

Avoid uniformly inflated limbs, sphere chains, smooth-blending every connection, oversized weapons as a substitute for identity, and a backpack that hides the silhouette. No weapon or new combat mechanic is required for this milestone.

### Palette and materials

Start with the palette below. Colors are proposed display-space swatches; pass them through the existing color conversion path rather than treating hexadecimal components as linear light.

| Role | Starting swatch | Treatment |
|---|---|---|
| Main armor | Deep petrol `#236B73` | Broad restrained highlight; painted, not mirror-like |
| Secondary plates | Warm ivory `#DDD7C6` | Helmet brow, selected chest/boot surfaces; lighter value mass |
| Undersuit and recesses | Blue charcoal `#202B36` | Matte; visible separation between hard pieces |
| Identity accent | Burnt orange `#D8783B` | A few intentional blocks, not every edge |
| Face | Warm tan `#DDAA87` | Soft response, no armor-like specular |
| Eyes and small light | Pale cyan `#9FE5DF` | Restrained emissive use for equipment only; eyes remain expressive |

Keep approximately 60% main armor, 25% secondary/undersuit, and 15% face/accent on the visible character. Tune the actual balance by view. Avoid tiny alternating stripes and bright outlines around every plate. Emission must not erase the underlying shape or become a substitute for lighting.

A recolor should preserve the dark joints, face readability, and accent hierarchy. Prove one alternate main armor color; a customization UI is outside scope.

### Face

Build an open face within the helmet: two readable eyes, a brow line, a small nose indication, and a restrained mouth. Prioritize eye placement and the shape of the face opening. A useful default expression is focused curiosity, not a permanent grin.

At close inspection the eyes need a directed gaze and a blink or equivalent expression cue. At gameplay distance they may simplify naturally into a readable face region. Do not require a new skin, hair, or subsurface-scattering system. Avoid replacing the face with an opaque glowing visor merely because expression is harder.

## 3. Implementation route

Read [the vision](vision.md), [the campaign](campaign.md), [the agent guide](agent-guide.md), and the applicable repository skills before changing code. Use the live source to resolve any contradiction; historical comments and examples are evidence, not a guarantee of runtime behavior.

These are implementation entry points inspected while preparing this brief, not a maintained capability inventory:

| Decision | Starting point |
|---|---|
| Author a body-worn creation instead of expanding the procedural catalog | [WorldLook](../src/Puck.World.Schema/WorldLook.cs) |
| Shape dimensions, author coordinates, palettes, parents, exported parts | [CreationDocument](../src/Puck.World.Authoring/Authoring/CreationDocument.cs), [CreationGeometry](../src/Puck.World.Authoring/Authoring/CreationGeometry.cs), [CreationFrame](../src/Puck.World.Authoring/Authoring/CreationFrame.cs) |
| Motion signals, gated drivers, swings and slides | [CreationAnimation](../src/Puck.World.Authoring/Authoring/CreationAnimation.cs) |
| Foot and hand constraints | [CreationEffector](../src/Puck.World.Authoring/Authoring/CreationEffector.cs) |
| Dynamic emission, palette handling, body registration, transform packing and bounds | [WorldStampPool](../src/Puck.World.Client/WorldStampPool.cs) |
| Primitive composition and scope behavior | [CreationStampEmitter](../src/Puck.World.Authoring/Authoring/CreationStampEmitter.cs) |
| Existing humanoid content example; inspect and validate before reuse | [adventurer.creation.json](examples/creations/adventurer.creation.json) |
| Existing studio district for staging | [studio.world.json](../src/Puck.World/Assets/worlds/modules/studio.world.json) |
| Production world integration | [puck.world.json](../src/Puck.World/Assets/worlds/puck.world.json) |
| Shared material and lighting implementation | [SdfMaterial](../src/Puck.SignedDistance/SdfMaterial.cs), [sdf-vm.hlsli](../src/Puck.SdfVm/Assets/Shaders/Sdf/sdf-vm.hlsli), [sdf-world.hlsli](../src/Puck.SdfVm/Assets/Shaders/Sdf/sdf-world.hlsli) |

Use the existing creation-look, prototype, and body appearance routes. Resolve which rows actually control the local character and mirrored appearances before changing them. Use compiler-backed references when tracing C# usage. A static statue in the studio does not prove the body-worn route works.

Authored parents must precede their children. Parent a glove to its forearm and a forearm to its upper arm; move each armor detail with the plate it belongs to. Verify the composition order of animation, parent transforms, IK, and secondary followers in the live packer. A named part or an available effector record alone does not prove a desired behavior is connected.

Use document-authored dimensions, colors, pivots, animation parameters, and look selection. Implement a missing reusable rendering or animation control only after an in-engine comparison demonstrates the need. No hero-specific shader branch, avatar-id special case, hardcoded faction palette, mesh import route, or separate character renderer.

### Initial geometry budget

Aim for 36–44 authored shapes, keeping headroom beneath the live stamp limit. Confirm the current validator and pool limits before authoring; do not raise them as the first response to a crowded design. Palette and driver limits must also be checked at execution time.

| Region | Initial shape allocation |
|---|---:|
| Helmet, face, eyes and identifying facial forms | 10 |
| Neck, chest, abdomen and pelvis | 5 |
| Both shoulders, upper arms, forearms and gloves | 10 |
| Both thighs, knees, lower legs and boots | 10 |
| Back housing and selected accents | 5 |
| Initial total | 40 |

Move allocations when the silhouette earns it. Count mirrored pieces separately. Prefer simple existing primitives and rigid transforms. Add a new ISA operation only under the owning skill's admission rule, never just to name an armor plate.

Use plain unions for most separate armor pieces and smooth unions only where a continuous surface is intended. A carve, group, eccentric shape, or field modifier can change evaluation and culling costs; inspect the emitted program and profile it. Do not add procedural noise to this character: clean plates are the art direction, and creation-level noise has body-look restrictions in the present authoring contract.

## 4. Animation direction

Armor moves as rigid pieces over articulated joints. Preserve the volume and shape of plates through every pose. Secondary motion belongs to selected accessories and the body attitude; avoid rubber-like stretching of the whole suit.

| State or transition | Intended performance | Check |
|---|---|---|
| Idle | Slight asymmetry, soft breathing, occasional blink | Feet stay planted; no perpetual walk cycle |
| Walk | Alternating travel-driven stride and opposite arm swing | Feet do not visibly skate through the ground |
| Run | Forward intent, stronger knee lift and arm action | Helmet stays readable; shoulders do not swallow the head |
| Turn and stop | Modest bank and a controlled settle | No long lag between physical body and visible character |
| Jump ascent | Compress into a readable takeoff and gather limbs | Pose follows movement state, not a free-running idle timeline |
| Fall | Open the pose enough to distinguish it from ascent | Stable behavior at the apex; no rapid pose flicker |
| Landing | Brief knee compression and recovery | Feet remain on the contact surface; plates do not intersect visibly |

Start locomotion with the existing travel/speed/vertical-speed/turn-rate drivers and body-fact gates. Use IK where the live path supports it and it visibly improves planting. For one-shot transitions, prove how the current cue or driver route receives the event; do not invent undocumented gate tokens or claim a periodic timeline is event-driven.

Keep presentation separate from authoritative movement and collision. Appearance changes must not secretly alter jump height, speed, or the collision body. Check ramps, obstacles, narrow passages, and the foot origin: a larger boot must not make honest collision look broken. Reset followers appropriately on teleport, respawn, or identity change; demonstrate that the character does not stretch back toward an old position.

## 5. Staged execution and exit criteria

### Stage A — establish the baseline and the live route

Record revision, dirty work, GPU, backend, display resolution, internal render scale, quality settings, camera, and body count. Capture the current character in the studio and in the ordinary island view. Measure current frame and pass timings with the same framing planned for the replacement.

Load a minimal authored creation through the real body-look path and verify it moves with the local body. Establish exact working console commands and content loading before investing in the complete design. Preserve other sessions' edits.

**Exit:** saved baseline images and timings, plus a moving body-worn creation. Record any actual missing connection and its smallest general solution.

### Stage B — silhouette and articulation blockout

Build the helmet, torso, pelvis, major armor masses, and boots in neutral materials. Produce front, side, rear, and both three-quarter views. Use the proposed ratios, then adjust based on the rendered result.

Inspect a silhouette-only treatment against a plain background. At 96 and 160 pixels of character height, the helmet, shoulders, forearms, waist, and boots must remain distinguishable. These are diagnostic screen sizes, not a replacement for testing the actual gameplay camera. Obtain them through framing or rendered output, not enlarged concept art.

Exercise elbow bends, knee bends, a full stride, and a landing crouch before adding details. Maintain clearance at shoulder/head, forearm/chest, thigh/pelvis, and boot/ground seams.

**Exit:** a readable original silhouette from every view and articulated poses without conspicuous plate collisions. If it looks like a recolored catalog mannequin, revisit proportions and plate shapes here.

### Stage C — materials and character identity

Apply the palette, face, three identifying features, and restrained specular/emissive settings. Compare with the neutral blockout at identical framing. Check grayscale value separation and one alternate armor color.

Use existing material controls first. If the image still needs help, identify the precise defect with an A/B capture: washed-out plate boundaries, unstable highlights, or loss of shape in shade. Add only the reusable control that fixes that defect.

Potential additions include a soft toon ramp, bounded rim contribution, or material-selectable highlight shaping. They are alternatives to evaluate, not a mandatory shopping list. Avoid hard lighting thresholds that crawl during rotation. A rim must not draw through occluders or brighten every dark edge. Full HDR, bloom, global outlines, and a new PBR stack are separate work unless evidence makes a small part essential.

**Exit:** armor, undersuit, face, and equipment lights remain visibly different under neutral, warm, cool, and shadowed conditions. Bright light does not clip away the character's color hierarchy. Any shared shader change has a no-change/default control on existing content.

### Stage D — movement and gameplay integration

Complete the motion table. Integrate the selected hero as the main character through normal authored look selection in the shipped world, while retaining the baseline through the existing look mechanism when practical for comparison. Verify the actual local-seat route, another embodied copy, and a mirror/session view if the change touches those paths.

Use the studio for review, then walk back into the ordinary game. Verify near-camera obstruction, rear readability, a second seat, look switching, recoloring, and save/reload through supported routes. No stale old body, duplicate geometry, missing face, frozen accessory, or transformed piece left behind.

**Exit:** the player actually wears the hero, motion responds to play, and the ordinary camera and interactions remain usable.

### Stage E — cost, parity, and final review

Compare old and new looks in identical scenes and settings. Measure one hero, two local seats, and a bounded group of eight detailed bodies wearing the same creation if admitted by the existing envelope. If the group is refused, report that limit; do not silently substitute coarse capsules or raise the pool to obtain a screenshot.

After warm-up, gather at least 300 produced frames per variant and repeat the comparison three times. Record median and p95 total GPU time, available pass timings, CPU frame work where measurable, word/instance/transform counts, and the emitted program's step scale. Separate shader compilation and first uploads from steady-state results. Keep background scene and camera coverage comparable.

Initial planning targets are at most 25% additional median and p95 total GPU time for the single-hero replacement, and at most 5% on an unchanged scene without the hero for shared shader changes. These are proposed regression budgets, not measured feasibility or an FPS promise. If exceeded, simplify details and expensive evaluation paths before reducing resolution. Report a persistent miss as incomplete against this target with the actual tradeoff; do not relabel it a pass.

Verify both Vulkan and Direct3D 12. Run the current narrow parity check when shared shaders, the presenter, or the render path change. Parity agreement does not establish visual quality, and the existing parity world does not automatically cover this hero. Inspect hero captures from both backends as well.

**Exit:** the acceptance evidence below exists, relevant checks pass, and any performance or runtime limitation is explicitly named.

## 6. Required evidence and acceptance rubric

Keep raw engine captures, exact commands/configuration, both output streams, and a short results note together in the task's artifact directory. Name the paths in the final handoff. Preserve reusable authored content in the appropriate world/module; avoid committing generated captures or a second persistent capability register by default.

Required evidence:

- Matched old/new gameplay views at the same camera, resolution, and lighting.
- Front, side, rear, and three-quarter views, including a close face view.
- Silhouette and grayscale comparisons at small character sizes, plus native gameplay framing.
- A recorded turntable and locomotion sequence, or a fenced frame sequence if video capture is unavailable: idle, walk, run, turn, jump ascent, fall, landing, stop.
- Neutral and ordinary island lighting, with at least one shadowed view and one alternate armor color.
- One/two/eight-body cost results or the actual admission refusal, and the required backend evidence.
- Successful look switch and save/reload, plus any relevant mirrored-view check.

Judge each category as **pass**, **revise**, or **blocked**, citing the capture or result. A beautiful close-up does not compensate for a failed gameplay view.

| Category | Pass condition |
|---|---|
| Original identity | The design has its own helmet, chest/back motif, and silhouette; no recognizable copied character markings or geometry |
| Proportion | Reads as a compact armored adventurer, with a large expressive head and substantial extremities |
| Readability | Head, torso, limbs, and travel direction remain clear from the gameplay camera and behind |
| Construction | Plates have intentional edges, joints have clearance, and attachments move with their parent |
| Materials | Painted armor, matte recesses, face, and lights remain distinct without display artifacts |
| Expression | Gaze and at least one expression cue are visible at close range; the face region reads at play distance |
| Motion | State-responsive locomotion and jump/landing behavior; no conspicuous skating, interpenetration, or follower trails |
| Integration | The real player wears the look through normal world data; switching and persistence work |
| Cost | Measured under fixed conditions; planning budgets met or the shortfall remains explicitly unresolved |
| Backend behavior | Both backends render the intended character without missing pieces or material divergence |

## 7. Verification mechanics and boundaries

Use the running `Puck.World`; a build proves compilation, not the character. Consult the current [world skill](../.agents/skills/puck-world/SKILL.md) for console grammar, capture behavior, body/seat indexing, and run recipes, and the [SDF skill](../.agents/skills/sdf-world/SKILL.md) for shader contract pairs and bytecode regeneration. Do not run the quarantined Post battery.

The baseline launch shape is:

```powershell
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 0 --state-dir <isolated-state-directory>
```

Replace the placeholder with a real task-owned directory. Feed script input using the supported process/console route; do not paste POSIX input redirection into PowerShell. Use UTF-8 without a BOM and capture stdout and stderr separately. Consult current command help before adding backend overrides or look-selection verbs.

`world.screenshot <path.png>` arms a capture. Fence with `world.wait <ticks>` and verify the capture completion message and file before inspecting it. Do not arm the next image while the previous request is pending. Hide any opened console overlay for visual judgment. For deterministic multi-view sequences, prefer the existing authored capture/camera machinery when suitable.

Run builds and focused checks appropriate to actual changes. Schema or animation changes need meaningful failure/control cases, such as a missing parent/driver refusing while a valid definition works; pure color or shape edits do not need implementation-mirroring unit tests. Inspect images directly. Do not substitute average pixel error, image dimensions, or a successful file write for visual review.

Keep implementation within this milestone: one hero, one alternate palette, essential movement and rendering support, production integration, and evidence. Leave terrain/sky upgrades, combat, equipment inventories, full avatar customization, broad renderer rewrites, and crowd-system redesign for separate work.

Before completing, audit every documentation surface affected by a changed contract using [boy-scout](../.agents/skills/boy-scout/SKILL.md) and [documentation](../.agents/skills/documentation/SKILL.md). Record unrelated defects without expanding the change. Do not stage, commit, or alter another session's work.

## 8. Handoff between implementing agents

Work sequentially unless the user authorizes delegation. A later agent receives a completed stage and its evidence, not a vague instruction to make things look better. If delegation is authorized, assign disjoint file ownership; retain one integration owner for shared schemas, stamp packing, and shaders.

Each stage handoff must contain:

1. Stage completed and the exact exit criteria met or still failing.
2. Changed files, content identifiers, and the current working-tree status.
3. Exact launch, input, camera, capture, and replay instructions that were actually used.
4. Links to evidence and measured results, distinguishing observations from proposed targets.
5. The next bounded task and unresolved risks. Explain what not to redo.

Do not call the milestone complete while a required category is marked revise or blocked. If a required feature cannot be finished, deliver the useful work and a precise remaining task rather than hiding the gap behind aesthetic language.

### Copyable execution prompt

> Implement `docs/armored-chibi-hero-brief.md` in the current repository. Start with the live body-look route and baseline captures, then complete stages A–E. Preserve the original chunky anime/chibi direction and the proposed island-ranger identity while refining it through actual rendered comparisons. Prefer authored creation data and existing parent/driver/effector mechanisms. Add only general engine support justified by a visible deficiency. Integrate the hero as the main character, verify movement and gameplay views on both GPU backends, measure cost, and deliver the evidence required by the rubric. Read current repository instructions, preserve unrelated dirty work, and do not stop at a concept image, static studio statue, successful build, or plan. Use autonomous self-review checkpoints rather than repeated permission requests; report any real blocker precisely.
