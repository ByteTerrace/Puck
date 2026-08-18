# Cross-backend parity pattern corpus

`puck parity` boots every world in this directory, plus the shipped default
world, once per graphics backend, screenshots the same fenced simulation
moment in each run, and compares each backend pair under the relaxed parity
envelope (mean absolute channel delta ≤ 0.35 LSB, differing pixels ≤ 20%).
There are no stored baselines: both frames of a pair come from the same build,
so content changes cannot fail the check — only a cross-backend divergence
can. Two different patterns from the same backend must fail the same envelope
on every run, so the comparator proves it can refuse before it may report
green.

Each pattern targets one contract slice, so a divergence names the slice that
moved:

| World | Stresses |
|---|---|
| `parity-gradient.world.json` | Smooth-blend seams, curved normals, broad specular falloff — where benign ±1-LSB codegen noise clusters. |
| `parity-edges.world.json` | Hard high-contrast edges: checker boxes, a yawed silhouette, a thin distant sliver, an emissive bar. |
| `parity-modifiers.world.json` | Shape modifiers beyond identity transforms: twist, bend, onion, dilate, mirror. |
| `parity-glyphs.world.json` | Both text tiers off one uploaded font atlas: marched `Glyph` geometry (an embossed centered run with wrap/tracking/line-spacing, an engraved run) and the per-cell glyph decal on a `text`-source screen. |
| `parity-film-grain.world.json` | The `sdf-film-grain` post-render extension authored over the gradient pattern — proves `sdfPcg3d` (pixel/tick/seed) produces the same noise field on SPIR-V and DXIL. |

The shipped default world rides along as the one integration entry — the
composed game frame with a live avatar body.

## Editing a pattern

The worlds are generated; edit `ParityCorpusGenerator.cs`
(`src/Puck.Cli/Parity/`), never the JSON.

1. Change the pattern in `ParityCorpusGenerator.cs` and run
   `puck parity --generate`. New or changed creations are stamped with a
   zero hash.
2. Boot the world once; the validator refuses and names the canonical sha256:

   ```bash
   dotnet run --project src/Puck.World -c Release -- --world tests/Puck.Parity/parity-gradient.world.json --headless true --exit-after-seconds 5 --state-dir "$TMP/parity-state"
   ```

3. Re-run `puck parity --generate --hashes parity-gradient=<hex64>,...` with
   every id the refusals named, and confirm each world boots to a
   `world.status` echo. Pass EVERY pattern's hash, not only the changed one —
   the generator writes all the worlds each run, and an id it has no hash for
   is stamped back to zero (recover an unchanged pattern's hash from its
   committed world file).
4. Run `puck parity` and look at the frames it leaves in its artifact
   directory — a pattern that stopped framing its subject measures nothing.

Solid placements compile into the contact field, which accepts anisotropy only
for primitives with an exact spelling: keep `Torus` and `RoundCone` scales
uniform or boot refuses.
