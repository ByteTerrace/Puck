# Cloud lane: wallpaper walls, review fixes

**Your main:** `fleet/cloud-c` (Cloud C) (head `6979ad16b`). Skills: `rendering` (the SDF ISA, both interpreters, the C#/HLSL sync contract) and `sdf-authoring` (creation wallpaper groups).

The lane closes a soundness gap in the wallpaper fold's cell walls. Its rule: a fold builds only through a continuous group, the symmetry LOD is gone, and a creation's non-continuous group is refused where it enters. It sits on `bed61871c` (march-lod-2, in batch C). Read the commits in `bed61871c..fleet/cloud-c` first. The review covered `9f536288c`; the head commit `6979ad16b` (authoring refusal) was not in the reviewed range, so self-review it too.

The fold exists in C# (`src/Puck.SignedDistance/SdfWallpaperFold.cs`) and in HLSL (`src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-map.hlsli`, `sdf-map-grad.hlsli`, `sdf-point.hlsli`). Every fix lands in both, per the sync contract in `rendering`. A Codex review found three merge blockers:

1. **Finite hex limits break continuity in accepted groups.** `src/Puck.SignedDistance/SdfWallpaperFold.cs:122` clamps rounded axial coordinates independently, which breaks continuity for P3M1 and P6M. The existing probe uses effectively unlimited limits, so it misses this.
   *Input:* unit cells, limits `(1,1)`, then a sphere centred at `(0.25,0,0.4330127)` with radius `0.05`. The points `(1.75005,0,0.4329261)` and `(1.74995,0,0.4330993)` are about `0.0002` apart, but their folded images are about `0.5` apart. The first reports about `0.45` clearance; the second is inside rendered geometry. Primary stepping and the beam's clearance proof can both skip it.

2. **Fractional limits invalidate the group-only admission check.** `src/Puck.SignedDistance/SdfProgram.Validation.cs:511` accepts PMM whatever its limits; the builder requires only nonnegative limits.
   *Input:* PMM, unit cells, limits `(0.25,0.25)`, then a sphere centred at `(0.3,0,0)` with radius `0.05`. At world X `0.49` the field reads `0.14`. At X `0.5001`, clamping changes the cell index to `0.25`, which folds X to `0.2501`, inside the sphere. A positive-X trace steps straight to `0.63`, past the whole rendered interval after the seam. Either make fractional limits sound or refuse them at admission and in the builder; pick one spelling.

3. **The reciprocal floor disagrees with the cell displacement.** `src/Puck.SignedDistance/SdfWallpaperFold.cs:28` computes reciprocals from a cell of at least `0.0001`, while the fold subtracts the original, possibly smaller cell, so accepted groups jump at cell boundaries.
   *Input:* uniform `Scale(1e6)`, then PMM with cells `(1e-5,1e-5)` and limits `(2,2)`, then a sphere centred at `(4e-5,0,0)` with radius `1e-6`. At world X `49` the field reads `8`; X `50.1` is already inside rendered geometry. A positive-X step lands at `57` and skips it.

Strengthen the continuity probe so it covers finite and fractional limits and tiny cells, so that it would have caught all three findings.

**Owning suites:** `tests/Puck.SignedDistance.Tests` (`SdfWallpaperFoldLawTests`, `SdfEncodingProbeLawTests`), `tests/Puck.SdfVm.Tests` (`SdfLogSphereCrossingLawTests`), `tests/Puck.Shaders.Tests` (HLSL compile), and `tests/Puck.World.Tests` filtered to `CreationWallpaperGroupLawTests`.
**GPU legs (the lead runs these; list them in your report):** `SdfLogSphereMarchDeviceLawTests` on vk and dx, plus the red-leg patch regenerated against your head.


## Rules for every cloud lane

- **Your main is your `fleet/<session>` branch (Cloud A `fleet/cloud-a`, Cloud B `fleet/cloud-b`, Cloud C `fleet/cloud-c`)** on `ByteTerrace/Puck`. Start from its head, commit there, and push plain fast-forwards to it; the owner pre-authorizes those pushes. Never push or merge any other branch, never `main`, never force-push, never delete a branch. The lead integrates your branch into `features/gfx-pipeline`.
- Read `CLAUDE.md`/`AGENTS.md` first. Before editing, load `verification`, `review-passes` and the skill that owns your area (named in your brief).
- Machine: Linux, no GPU. Build with `dotnet build -c Release -m:4 -nodeReuse:false`; a full Release build needs `dotnet workload install wasm-tools` once per container. DXC compiles HLSL on Linux. The cloud container caps background commands at 2 h.
- Every fix carries a law that fails without it. Prove the red leg by withholding the fix, then restore and touch the restored files, or MSBuild keeps the mutated DLL. Report each red leg's actual failure message.
- If a finding is wrong, prove it with a law that passes on the unfixed code and say so. Never weaken a law to get green.
- Run gates with the branch's own built CLI (`dotnet <built Puck.Cli.dll>`), never a global `puck`. Run `puck affected` against the lane's merge base, the owning suites, `puck format` on what you touched, and every `--check` your change reaches.
- Some World.Tests content tests failed on Linux at earlier bases: canary fixtures, a bake pin and a JSON parse error. Before you attribute a failure to your change, compare it against the unmodified branch head.
- Repository rules: no environment variables, LF only, one spelling per thing, no legacy or compat paths, and docs name no dates or SHAs. Commit subjects are lowercase `area: sentence`. Commits carry **no** Co-Authored-By trailer.
- **Finish line:** every finding fixed (each with a red-legged law) or disproved, owning suites green, pushed. **Stop and report** if a fix needs a GPU, the scope grows beyond the findings, or a fix would change a serialized format token. End with **Needs you / Changed / Found**, and list any GPU legs the lead must run.
