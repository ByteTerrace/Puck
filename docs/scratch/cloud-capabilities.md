# Cloud session capabilities (measured)

Scratch report for the Graphics Lead (brief C-cap). Every figure below was
measured in this session on `housekeeping/gfx-cloud-2`, based on
`origin/features/gfx-pipeline` at `merge: docs citations read dotted names…`.

## Machine

| Item | Measured |
|---|---|
| OS | Ubuntu 24.04.4 LTS, Linux 6.18 kernel, KVM guest |
| CPU | x86_64, Intel Xeon @ 2.10 GHz (Sapphire Rapids-class: AVX2, AVX-512 incl. FP16/BF16/VBMI2, AMX), **4 cores**, 1 thread per core |
| RAM | 15 GiB total, ~13 GiB available, no swap |
| Disk | 23 GB free at start; **16 GB free** after the build and wasm workload install. The allowance is fixed, so `bin/obj` trees have to be cleaned on long lanes. |
| User | root (uid 0) inside an ephemeral container, reclaimed after inactivity. Anything not pushed is lost. |

## GPU / graphics APIs

**None usable**, as you expected.

- No `/dev/dri`, no `lspci`, no `nvidia-smi`.
- `libvulkan.so.1` (loader 1.3.275) is installed, but `/etc/vulkan/icd.d` is
  empty and `/usr/share/vulkan/icd.d` does not exist. There is no driver, not
  even lavapipe, and `vulkaninfo` is not installed. No Vulkan device can be created.
- Direct3D 12: not available (Linux, no WSL/dxcore).
- Consequence: `puck parity`, the GPU canaries, `Puck.World` windowed runs and
  any frame capture **cannot run here**.

## Toolchain

- .NET SDK **10.0.401** (MSBuild 18.9.11), runtime 10.0.12, RID `linux-x64`;
  `global.json` honoured.
- No workloads were preinstalled. `dotnet workload install wasm-tools`
  succeeded in **25 s**: it was needed for `Puck.World.Browser` and pulls emscripten 3.1.56.
- DXC: `/usr/local/bin/dxc` → `libdxcompiler.so 1.9 (1.9.0.1)`.
- Also present: node 22, python3, `gh` (no direct GitHub API; GitHub goes
  through MCP tools). **Not present:** pwsh, wine, Codex CLI.

## Build: `dotnet build Puck.slnx -c Release`

1. First attempt **failed at restore in 2.5 s**, verbatim:

   ```
   /usr/local/share/dotnet/sdk/10.0.401/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.ImportWorkloads.targets(38,5): error NETSDK1147: To build this project, the following workloads must be installed: wasm-tools [/home/user/Puck/src/Puck.World.Browser/Puck.World.Browser.csproj]
   /usr/local/share/dotnet/sdk/10.0.401/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.ImportWorkloads.targets(38,5): error NETSDK1147: To install these workloads, run the following command: dotnet workload restore [/home/user/Puck/src/Puck.World.Browser/Puck.World.Browser.csproj]
       0 Warning(s)
       1 Error(s)
   ```

2. After installing `wasm-tools`: **Build succeeded, 0 warnings, 0 errors,
   6 min 04 s wall**. Restore was warm: 169 packages were already in `~/.nuget/packages`.
   The world pipelines (`Assets/pipelines/*.hlsl`) were compiled by DXC during
   this build into 26 fresh `.dxil`/`.spv` binaries under `Puck.World/obj`.
3. SdfVm/Overlays kernels were already up to date from container setup,
   so I touched every `.hlsl` under `src/Puck.SdfVm` and `src/Puck.Overlays`
   and rebuilt:
   - `Puck.SdfVm`: **17/17 kernels recompiled to both DXIL and SPIR-V**,
     2 min 32 s, 0 errors.
   - `Puck.Overlays`: 1 DXIL + 1 SPIR-V fresh, 4.7 s, 0 errors.

   So **HLSL → DXIL + SPIR-V via DXC works on this box**.

## Tests (`--no-build`, Release)

| Project | Result | Duration |
|---|---|---|
| `tests/Puck.Maths.Tests` | **Passed 695 / 695**, 0 skipped | 27 s (30.7 s wall) |
| `tests/Puck.Shaders.Tests` | **Failed 2**, passed 640, skipped 3, total 645 | 5 s (8.0 s wall) |

Shaders.Tests detail:

- **2 failures, a real Linux path defect, not an environment gap.** Both
  `ShaderRegisterBindingLawTests.Every_shipped_register_equals_its_binding`
  and `…Every_shader_item_type_reaches_a_source_and_the_package_library_reaches_its_resample_kernel`
  throw:

  ```
  System.IO.FileNotFoundException : Could not find file '/home/user/Puck/src/Puck.World/..\..\build\WorldAssets.targets'.
  ```

  `src/Puck.World/Puck.World.csproj:77` has
  `<Import Project="..\..\build\WorldAssets.targets" />`. It is the only
  backslash `Import` under `src`/`tests`/`build`; MSBuild normalises it on Linux.
  The test's `ProjectAndImports` (`tests/Puck.Shaders.Tests/ShaderRegisterBindingLawTests.cs:120-135`)
  passes the raw attribute to `Path.Combine`. The fix is to make the import
  forward-slash (repo path convention), and probably also to make the test
  loader normalise. I have not fixed it, because the brief was measure-only.
- 3 skips: `ShaderInterfaceSpikeTests.The_dxil_reader_finds_every_binding…`
  (`typed-buffers`, `film-grain`, `pixelate`), which are skipped by design. The test says
  "The DXIL reader calls dxcompiler through its Windows COM layout." Its
  SPIR-V counterparts ran and passed.

## Network and git

- nuget.org: `https://api.nuget.org/v3/index.json` → HTTP 200 in 0.5 s, and
  restore works (through an agent proxy).
- `git fetch origin` works. `git push --dry-run` to a new branch on origin is
  accepted, and this report itself is the real push.
- GitHub API only via the session's MCP tools, scoped to `byteterrace/puck`.
  I cannot open PRs unless the owner asks.

## Agent runtime

- `claude --version`: **2.1.287 (Claude Code)**.
- Model: the session is configured for and currently served by `claude-opus-5-5`
  (per the session's own metadata). The account's seven-day rate limit reports
  `allowed_warning`.
- Tools: Bash, Read/Write/Edit, Grep/Glob, subagents (Agent), background
  processes, Monitor, WebFetch/WebSearch, GitHub MCP, the `puck` MCP server
  (it needs a running World, which can't start here without a GPU), and
  scheduled self check-ins. **No Codex.**
- Cross-session messaging works both ways. I message the Lead with the Claude Code Remote MCP `send_message` tool, `session_id` `session_01MEP1Y2gSYRwmR7mfeeDzww`; plain `SendMessage` refuses in this session, but that is only that one tool. Git trailers stay the durable record and the message is the notification.

## What a lane should not expect from me

- **Anything needing a GPU**: `puck parity`, GPU canaries, running
  `Puck.World` (windowed or offscreen), captures, Vulkan/D3D12 validation
  layers, GPU timing.
- **Windows-only paths**: D3D12, the DXIL reader (COM layout), anything that
  assumes `\` paths. Linux exposed the defect above, which makes this box useful as a
  path-portability check.
- **Long-running work**: a background command lasts up to 2 h and a monitor
  up to 30 min before re-arming. The container is reclaimed on inactivity, and with only
  4 cores a full solution build is about 6 min, so batch the verification.
- **Wall-clock benchmarks**: shared KVM host, not an idle machine. `puck bench`
  numbers from here would not be credible.

## What I can do well

CPU-side work with full verification: C# across all projects, HLSL editing
with DXC compile to DXIL and SPIR-V, Maths/Shaders/other unit tests, the
emulator Post batteries (CPU-only, so presumably runnable; not measured),
`puck` CLI verbs that don't need a GPU (format, lengths, comment-smells,
architecture, references/declarations/search), docs work, and pushing branches.
