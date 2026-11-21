# RTX 2060 local test: the prerelease player build at 862d5ee6be

A test hand-back, not meant to merge. It records what the prerelease player build `player-local/862d5ee6be` does on the consumer machine, run as a player runs it.

## Setup

**Machine:** NVIDIA GeForce RTX 2060 (6 GiB, driver 621.24), Intel Core i5-8400 (6 cores), 16 GB RAM, Windows 11 Pro 10.0.26300.

**Build:** `puck-player-862d5ee6be-win-x64.zip`, its digest checked against the release asset (sha256 `17c55534…09ef5`). `player.json` says `"complete": true` and gives commit `862d5ee6be4561b68cd7d3464cc695e0583f853b`.

**Command:** every run used the player folder's own `Puck.World.exe`, as its README gives it:
```
Puck.World.exe --world worlds/debug --cache-dir cache --backend <directx|vulkan> --width 1920 --height 1080 --devices none --exit-after-seconds N --state-dir <run>\state
```
with the script beside this file on stdin.

**Not done on this machine:** nothing was built, no cartridge was compiled, and nothing was baked by hand. The only thing installed was the `dotnet-trace` and `dotnet-counters` global tools, for the trace.

**Run order:**

| Run | Backend | What it covered |
|---|---|---|
| `dx` | Direct3D 12 | First boot of the player cache, then the benchmark route. The 900 s auto-exit cut the script before its closing reads. |
| `vk` | Vulkan | Boot and the route, with every read. |
| `dx2` | Direct3D 12 | Second boot, the route, every read, and a 60 s gc-verbose trace taken during the route. |
| `deck-headless` | none | Step 5, headless. |
| `deck-windowed` | Direct3D 12 | Step 5, windowed, plus a 60-frame presented burst at the range. |

Each folder holds `out.log` (stdout answers), `err.log` (stderr), and for route runs the benchmark's `frames.csv` and `run.json`.

**On timing:** the world steps at 30 Hz. The windowed runs were the only GPU work on the machine, and the machine had no other heavy load.

## Verdicts

All at 1920×1080 windowed, authority in process, profile `rtx2060`. The device table applied: `table=rtx2060`, device cap 4,608 MiB, host cap 6,144 MiB.

| Device, size, backend | Bar | Route mean | p50 | p95 | p99 | Worst | Verdict |
|---|---|---|---|---|---|---|---|
| RTX 2060, 1920×1080, D3D12 (`dx`) | 60 FPS | 112.9 ms (8.9 FPS) | 70.2 ms | 298 ms | 1,030 ms | 1,526 ms | **Fails** |
| RTX 2060, 1920×1080, D3D12 (`dx2`) | 60 FPS | 100.5 ms (10.0 FPS) | 73.3 ms | 270 ms | 883 ms | 1,685 ms | **Fails** |
| RTX 2060, 1920×1080, Vulkan (`vk`) | 60 FPS | 73.0 ms (13.7 FPS) | 56.5 ms | 118 ms | 439 ms | 1,429 ms | **Fails** |
| RTX 2060, host memory, both backends | 6 GiB | private 7.7–12.3 GiB | | | | | **Fails** |

Every route frame was over 16.7 ms: 1,301 of 1,301 in `dx`, and 1,649 of 1,649 in `vk`.

Standing at the stage before the route, the same boots ran at 28–35 FPS. Boot-section means: D3D12 35.4 ms, Vulkan 28.6 ms.

## Findings, ranked

### 1. The prewarmed cache does not stand on the 2060

The player bakes and keeps baking. This is a finding by the brief's own rule.

**First D3D12 boot (`dx`):**
- The boot started from the shipped cache: `groups=7550 baking=0 loading=1237 shared=6313`.
- Its first `[sdf.mesh-bake] done` line reports `dispatches=598 samples=5869584 loaded=447 seconds=89.115`. That is a real bake of new geometry: `miss … geometry new to its set of 2401 entries`.
- 104 later bake starts had `baking>0`.

**Second D3D12 boot (`dx2`), same cache:**
- The first done line reports `dispatches=0 loaded=1257 seconds=1.6`. So the first run's bake was written into `cache/`, and the shipped cache was missing that geometry.
- Later bakes still ran: 34 starts with `baking>0`.

**Vulkan (`vk`):** 32 starts with `baking>0`.

**What triggers the later bakes:**
- New residencies as the route crosses portals: garden, hearth, the session-shared residency. For example, `start residency=routed$6~global6~garden … begun=[-337/union2/bake]`.
- `lost` lines: "the store could not restore its normals and levels (….mesh waits for workspace memory); its groups bake again".

**No run reported `[bakes: settled …]`.** The producer's own record in `player.json` agrees: its `check directx-rtx2060` and `check vulkan-rtx2060` entries say `not settled after 600 seconds`, while the 4070 checks settled at tick 0.

### 2. The host memory account refuses mesh store reads throughout

The refusal reads, for example, `[sdf.mesh-bake] group -6364 store refused: ….mesh The host memory account refused 45456 bytes for mesh.`

| Run | Store refusals | `lost` | `deferred` |
|---|---|---|---|
| `dx` | 920 | 9 | 61 |
| `dx2` | 330 | 2 | 78 |
| `vk` | 538 | 3 | 51 |

**Why the account refuses.** The benchmark profile gives `hostMemoryLimitBytes=1157834665`, about 1.08 GiB for the host account, against `hostMemoryCapBytes=6442450944`. Meanwhile the process itself is far over 6 GiB:

| Run | Point | Private | Managed heap |
|---|---|---|---|
| `dx` | 163 s | 7,707 MiB | 1,571 MiB |
| `dx` | 403 s | 7,288 MiB | |
| `vk` | 138 s | 8,215 MiB | |
| `vk` | 509 s, end | 10,812 MiB | 2,977 MiB |
| `dx2` | 127 s | 9,768 MiB | |
| `dx2` | 488 s, end | 12,283 MiB | 3,906 MiB |

Vulkan's end-of-run pool read `refusals=7076 forced=386639520 over-limit-grants=10 pressure-trims=289 room=0`.

So the account starves the mesh store while private bytes outside it, managed heap and allocator, run 2–6 GiB past the bar. The account pool's own line says `private-beyond-held=2901749916` at 138 s on Vulkan.

On a 16 GB machine, 12.3 GiB private leaves little for the OS and other programs.

### 3. The frame is CPU-bound on produce and simulate first, and also GPU-heavy

**Route means:**

| Run | produce | simulate | GC pause | presentMs |
|---|---|---|---|---|
| `dx` | 77.6 ms (p95 248) | 31.7 ms | 6.8 ms | 0.4 ms |
| `dx2` | 60.9 ms (p95 154) | 32.1 ms | 3.1 ms | |
| `vk` | 43.7 ms (p95 65) | 24.6 ms | 2.4 ms | 0.3 ms |

**Where produce goes**, from `host.frames` after the route in `dx2` (600 iterations, mean 49.7 ms):
- produce-work 33.1 ms: pump 10.6, capture 11.4, pack 4.6, record 4.0, prepare 1.6.
- simulate 14.7 ms.

**Standing at the range** (`deck-windowed`, 30 iterations, before moving), the mean was 60.2 ms:
- produce 44.8 ms, of which **pack 32.3 ms**, capture 7.4, record 2.5;
- simulate 14.5 ms.

**During the presented burst at the range:** mean 140 ms. produce 103 ms (pump 49.5, pack 32.6, capture 15.8), simulate 34.5 ms, and the frame thread allocated 7.8 MB a frame.

**GPU**, from the route's last 32-frame window (`run.json` `gpu`):
- Sum of pass means: 64.7 ms on `dx`, 72.6 ms on `dx2`, 37.2 ms on `vk`.
- Top passes on D3D12 (`dx`): world resolve 8.1, views 4.7, overlay 3.7, shadow-far 3.5, composite 2.7, session$24 shadow-far 2.4, surface 1.8 ms.
- Vulkan: resolve 8.2, views 4.9, composite 3.1, overlay 3.0, shadow-far 1.9 ms.
- Several session residencies render their own shadow and mesh passes beside the world's: `session$24`, `session$20$2x`.

### 4. Allocation and GC

**Route figures:**

| Run | Allocated per frame | GC pause total | Longest pause | Collections (gen0/gen1/gen2) |
|---|---|---|---|---|
| `dx` | 8.3 MB | 8.8 s in 147 s | 271 ms | 1,180 / 499 / 9 |
| `dx2` | 5.9 MB | 3.9 s | 248 ms | 716 / 273 / 5 |
| `vk` | 4.1 MB | 3.9 s | 293 ms | |

**Boots:** `dx` allocated 8.46 GB in its first 163 s; `dx2` had allocated 27.3 GB by 488 s.

**The trace:** `dx2/gc-verbose.nettrace.zip` holds 60 s taken 10 s into the route. It was not analyzed here, because the machine has no trace viewer and building one is not allowed on it.

### 5. Gaming decks (step 5)

**Headless:**
- All six machines kept up. At tick 300 the decks read `pending` 4, 4, 5 and 8 (range-deck-50 at 8). At tick 900 every machine read `segments=900 pending=0 fault=none`.
- `screen.state 2` read `backpressure=0` both times: `pending=4/8`, then `0/8`.
- `host.frames`: simulate 7.3 ms a tick, frames at 33.3 ms with the pacer waiting 26 ms. There was no growth in simulate time.

**Windowed (D3D12):**
- At tick 300 the decks read `pending=1`. At tick 900, and after the burst at tick 1443, every machine read `segments` equal to the tick with `pending=0 fault=none`.
- `screen.state 2` read `backpressure=0` both times.
- simulate was 14.5 ms standing and 34.5 ms during the burst at the range.

**So no deck failed real time on the 2060, headless or windowed.** The simulate growth while windowed comes from the frame and catching up on steps (2–4 steps a frame), not from the decks. `steps.shed` was 410–458 in the windowed reads.

**The range burst** (`deck-windowed/burst-*.png`; frames 000, 010, 031 and 059 kept):
- 60 presented frames, 0 standing, 0 skipped. Each frame spans 3–4 world ticks, because the window ran at about 7 FPS.
- The near deck's ball moves smoothly: about 5–8 screen pixels a presented frame in frames 0–30, tracked by centroid.
- In frames 31–48, which have 3 ticks a frame, the ball's vertical motion advances only on every other presented frame (y: 308.6, 307.9, 299.9, 299.5, 291.8, 291.2 …). That is a visible stair-step judder; horizontal motion advances every frame.
- **The "2 px per shown frame" check cannot be read at this frame rate.** A shown frame covers 3–4 ticks, not 1, and the screen-to-native pixel scale was not measured.

## What was not done

- No hand-played session, so no eye judgement of pops, holes or swimming shadows in motion. The benchmark ran scripted with `--devices none`.
- `dx` lost its closing `world.gpu-timing` and `host.frames` reads to the auto-exit. `run.json` carries the GPU window and `frames.csv` every frame.
- Time to first frame was not taken separately. Residency `all-landed` was 89.2 s on `dx`, with near groups landed at 43.3 s.
- The GC trace is shipped but not analyzed.
