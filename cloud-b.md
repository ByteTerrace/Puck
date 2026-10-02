# Cloud lane: value domains, review fixes

**Your main:** `fleet/cloud-b` (Cloud B) (head `31b59a4cc`). Skills: `puck-world` and `rendering` (camera rigs, render bindings).

The lane gives every bound render or camera scalar one domain per field. Load time refuses an out-of-domain value; at runtime the value is clamped statelessly and a one-shot diagnostic is reported. The lane exists because bound render scalars were unchecked: a softness of 0 or less leaves smoothstep undefined. It branches from batch C, so read its commits ahead of `origin/integration/batch-c` first. A Codex review found five merge blockers:

1. **Finite camera operands bypass their domains.** `src/Puck.World.Client/WorldCameraRigCompiler.cs:558`: only blend sources receive a `Field`, and path fractions resolve without clamping.
   *Input:* fraction keys `-3e38` and `3e38`, sampled halfway. Both keys pass validation, but `WorldKeyResolver.Scalar` overflows its float subtraction to infinity. A closed path then produces NaN camera coordinates, and `CameraSnapshot.LookAt` throws.

2. **Admission ignores the binding's eased value vs. its `$target`.** `src/Puck.World.Schema/WorldDefinitionValidator.Render.cs:523` always reads the stored value.
   *Input:* scrim alpha bound to `state.scrim`, with a valid dynamics trait, stored target `0`, and clock `Y0` representing `0.9` at epoch zero. The presented value starts inside the scrim domain, but load refuses it. With target and follower reversed, load admits a presented value that starts out of domain. Admission must check the value the binding actually presents.

3. **The ordinary seat chase rig never receives the report sink.** `src/Puck.World.Client/WorldSeatViewState.cs:251`.
   *Input:* `views.seatRig` holds a blend bound to a row that starts at `0.5`; a live write changes the row to `2`. The camera clamps the weight but emits no diagnostic, because its cached compiler receives `domains: null`.

4. **Report identities collide across worlds.** `src/Puck.World.Client/WorldValueDomainReports.cs:57`: the shared singleton keys reports by field, document site and unqualified row name.
   *Scenario:* two observed worlds bind `render.sky.layers[0].softness` to their own `state.cloudSoft` rows, and both write zero. The first world reports; the second world's distinct row is silently suppressed. Key by world identity as well, or scope the report set per world.

5. **Retired report entries are retained forever.** `src/Puck.World.Client/WorldValueDomainReports.cs:32`.
   *Scenario:* repeatedly replace a cloud binding with a uniquely named row that starts at `0.5`, write zero, then remove the old row. Every iteration permanently adds a row string and site to the singleton's `HashSet`, although the live document stays bounded. Release entries when their binding or world goes away. Fixing 4 by making report state per-world may fix this too; prove both with laws either way.

Then check every other site that compiles a bound scalar (camera, render, views, post) for findings 1 and 3: a missing domain, or a `null` sink.

**Owning suites:** `tests/Puck.World.Tests` (heavy, about 7 GB peak, so run it alone with `--filter` while iterating and in full once at the end) and `tests/Puck.World.Schema.Tests`.


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
