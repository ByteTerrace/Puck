# Author shaders research round: the question and Puck's mandates

## The owner's questions

Should Puck let authors bring their own shaders, and how? Would it perform well?
Does it truly allow better expression? Games show "compiling shaders" steps with
thousands of shaders: is that accurate, how do they use thousands, and how do
they work together? How does all of it fit into Puck?

## Puck's mandates (from AGENTS.md; every finding is weighed against them)

- **Authors rule.** Looks and shading treatments are the author's; the engine
  keeps mechanisms with neutral defaults. Authors bringing their own shaders is
  in scope (the owner's ruling).
- **Performance bar.** 60 FPS on the RTX 2060 and RTX 4070, 45 on the Steam Deck,
  at the highest quality, met by a path every bar device runs. A technique only
  stronger hardware has is an enhancement with an automatic fallback, never the
  baseline.
- **Quality.** The bar is met at the highest quality setting; no quality
  reduction is accepted as a performance fix.
- **Ergonomic is optimal.** A spelling an author prefers costs no more than a
  clumsier equivalent; all spellings lower to one canonical form, and a spelling
  that costs more is a compiler defect. Cost is attributed to what the author
  wrote in `world.budget`.
- **Puck code over engine code.** Behaviour is authored Puck programs the engine
  compiles, runs and costs; the engine's own layers should become the first
  authored programs, never a second path.
- **Derived products are caches.** Compiled artifacts are identified from their
  inputs, derived again on a miss and counted, never committed; a player build may
  ship them prewarmed.
- **Edit to running under 30 seconds** from changing a shader to seeing it.
- **Both backends.** Direct3D 12 (DXIL) is primary; Vulkan (SPIR-V) never falls
  far behind. Linux and the Steam Deck matter.
- **Determinism** pins simulation only; presentation float work is outside it, but
  an authored shader must never read or write simulation state.
- **Plays nice with its host.** No seizing cores or busy-waiting; a runtime compile
  must not stall the frame.
- **Independent work.** Consult several independent sources (vendor docs, engine
  docs and talks, papers, open implementations), cite each with a URL you opened,
  and note where they disagree. No single engine is the specification.

## Puck today (verify against the code before relying on it)

Puck renders signed-distance-field worlds: shapes bake to meshes (the mesh bake),
which a raster mesh pass draws; materials are data rows (`SdfAppearance`,
`SdfMaterial`, paints, weathering, flow, glass) read by a few shading kernels
(`src/Puck.SdfVm/Assets/Shaders/Sdf/`, e.g. `passes/sdf-surface-stage.hlsli`,
`shade/sdf-material.hlsli`). That is an ubershader-and-material-table design: no
per-material shader permutations, no compile stutter. The `views` shading pass
(about 4.3 ms at the worst stops on an RTX 4070) is one of the largest frame
costs, with high register allocation reported earlier by PIX. Shaders compile
with DXC to DXIL and SPIR-V at build time; there is a shader cache and shader hot
reload (`world.shaders.reload`).

## Output

Write your findings to the file your brief names: sections per question, each
claim with its citation (URL opened, title, date where given), numbers stated
with their source, disagreements noted, and a closing "What this means for Puck"
section that weighs the borrowed techniques beside Puck-native combinations
(SDF source, derived products identified by inputs, authored programs costed in
`world.budget`, change-proportional work). Mark anything you could not verify.
