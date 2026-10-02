# Cloud lane: certified physics, review fixes

**Your main:** `fleet/cloud-a` (Cloud A) (head `35e5ca8ae`). Skills: `maths-usage`, `maths-laws` (any `tests/Puck.Maths.Tests` edit), `rendering` (fixed-point SDF query evaluator).

The lane adds certified SDF bounds over a region, built on an outward-rounded interval type in `Puck.Maths`. On top of those bounds sit a certified sweep, line of sight, and a physics step proved clear before the contact solve. Read the ten commits ahead of `origin/features/gfx-pipeline` before starting. A Codex review found three merge blockers:

1. **A Q16 fraction tie lets a later core cross the wall.** `src/Puck.Physics/FixedFieldContactSolver.cs:705`: `Fraction` discards the sweep's Q32 precision while `Reached` keeps it, so the `>= least` comparison ignores a shorter sweep when both fractions round alike.
   *Failing input:* thin box wall at `z=-100`, half-thickness `0.01`. Two radius-`0.5` sphere volumes centred at `(0,0,0)` and `(0,0,-9)`, in that order. Identity orientation. Previous body position `(0,0,-90)`, displacement `(0,0,-1000000)`, skin `0.02`, budget `64`. Both fractions round to zero. The first core proves about `9.74` units of travel and the second about `0.74`. The solver keeps the first displacement, which carries the second sphere through the wall.
   *Fix:* carry and compare the full Q32 sweep fraction. Add a compound-body law for equal Q16 fractions.

2. **Rotation bounds hide an intermediate overflow.** `src/Puck.SignedDistance/Queries/SdfFieldEvaluator.Bounds.cs:415`: `Intersect` turns an unbounded stepwise result into a bounded linear one. The four-raw error allowance assumes rounding without overflow, so it doesn't enclose wrapped quaternion intermediates, and `FindFrame` admits them.
   *Failing input:* translate by `(-2^45,-2^45,0)`, rotate by quaternion `(0,0,√½,√½)`, then a plane with normal `UnitY` and offset 0. Query the point box at `(2^46,2^46,0)`. `t.X` wraps. The point field returns `FarDistance` (1,000,000,000), while the certified interval lies wholly below zero (upper raw ≈ `-6917529027641081852`).
   *Fix:* prove the fused intermediates representable before using the linear enclosure; otherwise propagate refusal or unboundedness. Add this overflow inclusion law.

3. **A lattice index overflow certifies occupied space as clear.** `src/Puck.Physics/Fields/FieldLatticeSolid.cs:135`: subtracting `Reach` from an `int` column index can wrap before clamping, and the bounds walk then skips every column.
   *Failing input:* a one-cell lattice at the origin, cell size 1, column height 1. Query bounds from `(-2147483648,0.5,0.5)` to `(0.5,0.5,0.5)`. `int.MinValue - 2` wraps to `2147483646`, the loop visits nothing, and the walk returns `[2,2]`. The enclosed point `(0.5,0.5,0.5)` answers `-0.5`.
   *Fix:* compute column indices and neighbour limits in a wide type, clamp before narrowing, and refuse overflowing coordinate arithmetic. Add the bounds-versus-point law.

Then hunt the same three classes across the rest of the lane: precision dropped before a comparison, linear enclosures that assume no wrap, and narrowing before a clamp. Fix any further instance the same way.

**Owning suites:** `tests/Puck.Physics.Tests`, `tests/Puck.SignedDistance.Tests`, `tests/Puck.Maths.Tests` (law suite; follow `maths-laws`).


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
