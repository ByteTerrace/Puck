# Handoff: verify Vulkan closed-loop present timing on a `present_wait`-capable machine

This document is **in the repo on purpose** — it travels with the branch so the work can resume on another
machine without the local session memory. Branch: `features/input-refinement`.

## ✅ RESOLVED (2026-06-22, NVIDIA RTX 4070 / driver 596.49 / Vulkan 1.4, `present_wait`-capable)

The Vulkan closed loop is now **verified live, wiring-correct, and validation-clean**, with determinism preserved
(mini-action hash still `0x55C7B177544D426A`, determinism hash `0xA68E049DC156F422`). Verification on this machine
exposed **three real bugs** that the origin machine could never have hit (its driver lacked `present_wait`, so the
path never executed):

1. **Transposed `present_id` sTypes (the blocker).** The two adjacent `present_id` structure types were swapped in
   our constants. The *correct* header values (`C:\VulkanSDK\1.4.350.0\Include\vulkan\vulkan_core.h`) are:
   - `VK_STRUCTURE_TYPE_PRESENT_ID_KHR = 1000294000` (the present-INFO struct → `VulkanNativeFramePresentationApi`)
   - `VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_ID_FEATURES_KHR = 1000294001` (the FEATURE struct → `VulkanLogicalDeviceFactory`)

   We had them reversed. This single transposition produced all of: the feature query returning `presentId=false`
   (we queried `1000294000`, which is not a feature struct, so the driver left the bool zero — while `vulkaninfo`,
   using the correct `1000294001`, reported `true`); a `vkCreateDevice` validation error (feature chain carried
   `1000294000` = a present-info sType); and a `vkQueuePresentKHR` validation error (present chain carried
   `1000294001` = a feature sType). **NOTE: the original "debug checklist" below documented these backwards** — that
   wrong note is what the code matched. Fixed in both files; both sites now carry a "do not transpose" caution.

2. **`BackendSwitcher` did not forward present timing.** The launcher presents through `BackendSwitcher` (an
   `ISurfacePresenter` fronting the active backend), and `m_presenter as IPresentTimingFeedback` was therefore always
   `null` — so even with the closed loop running and `vkWaitForPresentKHR` returning `Success`, the samples never
   reached the pacer. `BackendSwitcher` now implements `IPresentTimingFeedback` and forwards `LastPresentTiming` to the
   active backend. **This also enabled DirectX present timing to reach the pacer for the first time** — it had the same
   `null` gap, so the prior "DX closed loop verified active" claim was only true at the capture layer, never end-to-end.

3. **(Knock-on, now confirmed working.)** With (1) and (2) fixed: Vulkan logs `ENABLED`, emits periodic
   `Closed-loop present timing live: measured interval ~16.9 ms (~59 Hz)` (phase-locked to the 60 Hz present clamp),
   `vkWaitForPresentKHR` returns `Success` every sample, and the Khronos validation layer reports **zero** errors.

### DirectX nuance discovered
DX closed-loop timing (`GetFrameStatistics.SyncQPCTime`) works under **vsync** (verified: `~8.45 ms / 118 Hz`) but is
**unavailable under `--present-mode adaptive`** — tearing presents have no vsync sync-point, so `SyncQPCTime` stays 0
(persistent `DISJOINT`). This is inherent DXGI behaviour, not a bug: on DX, adaptive/tearing and `GetFrameStatistics`
phase-locking are mutually exclusive. Vulkan does *not* share this limitation — `present_wait` confirms present-id
*display*, independent of vsync, so its closed loop works under adaptive.

### Step 6 — CONFIRMED end-to-end on the Samsung Frame (2026-06-22)
Done. A borderless-fullscreen, `--present-mode adaptive`, **uncapped** (`renderRate: 0`) run on the Samsung Frame (Game
Mode VRR) drove a continuously-variable confirmed-present cadence sweeping **~59–101 Hz** within the VRR window, and the
**NVIDIA G-SYNC on-screen indicator showed VRR ACTIVE** for the duration — i.e. the panel's refresh followed the present
cadence within `[Vmin, Vmax−3]` (the brief dip to 59 Hz, just under Vmin=60, is absorbed by the driver's Low Framerate
Compensation). Closed loop live + Vulkan-validation-clean throughout.

Prerequisite added to get here: a `fullscreen` field on the run document's host section (`schema/run.schema.json` +
`HostDocument` + `HostSettings` → `NativeWindowOptions.StartFullscreen` → the existing borderless `EnterFullscreen`). A
normal DWM-composited desktop window cannot enter VRR; borderless-fullscreen takes an independent flip, which is what lets
the panel follow. See `docs/examples/world-vrr-fullscreen.json`. NOTE: our Vulkan adaptive path maps to `FIFO_RELAXED` and
that was sufficient for VRR to engage here — `IMMEDIATE`/mailbox was NOT required.

### Still open
- **Fullscreen-EXCLUSIVE** present path (DXGI `SetFullscreenState` / Vulkan exclusive-fullscreen). Borderless-fullscreen is
  now wired and VRR-confirmed, which covers the modern path; true exclusive remains optional/unimplemented.
- **Multi-monitor**: the refresh range is queried once at startup — re-query on `WM_DISPLAYCHANGE` when the window changes
  monitors.

The original handoff content below is preserved for context; its "sType constants" checklist line was the one that was
**wrong** (see bug 1; that line is now corrected in place).

---

## Why this handoff exists

The full VRR + planet-scale + closed-loop present-timing stack is **implemented and builds green**, and
**determinism is preserved throughout** (the mini-action state hash is unchanged: `0x55C7B177544D426A`). The one
thing that could not be verified on the origin machine: the **Vulkan** closed-loop present-timing path *activating*,
because that machine's driver/environment does **not** expose `VK_KHR_present_wait`. It logs:

```
[present-timing] VK_KHR_present_wait/present_id unavailable (open-loop pacing)
```

…and correctly falls back to the (working) open-loop adaptive pacer — no crash, no validation errors. The **DirectX**
closed loop (`GetFrameStatistics`) **is** verified active and stable.

**Goal on the new machine:** confirm Vulkan `present_wait`/`present_id` is exposed, then confirm the closed loop
actually engages and is wiring-correct (struct layout, present-id lifecycle, the wait), with Vulkan validation on.

## First steps

```sh
git switch features/input-refinement      # pull it on the new machine first
dotnet build Puck.slnx -c Debug
dotnet run --project src/Puck.Demo -- --validate-mini-action      # determinism + cell-invariance gate (CPU)
dotnet run --project src/Puck.Demo -- --validate-determinism      # fixed-point + WorldCoord3 self-tests (CPU)
```

Both gates are GPU-independent and must pass on any machine.

## Verification protocol (Vulkan is the default backend)

1. **Is `present_wait` now exposed?** Run and read the startup line:
   ```sh
   dotnet run --project src/Puck.Demo -- --mini-action --present-mode adaptive --exit-after-seconds 5
   ```
   Success = `[present-timing] VK_KHR_present_wait/present_id ENABLED (closed-loop pacing)`.
   If it still says `unavailable`, the device/driver doesn't expose it — try a newer driver, or check whether the
   environment (RDP/VM/headless) hides it. (`vulkaninfo | grep present_wait` to confirm independently.)

2. **Is the loop actually live?** Opt in to the interval diagnostic:
   ```sh
   PUCK_PRESENT_TIMING=1 dotnet run --project src/Puck.Demo -- --mini-action --present-mode adaptive --exit-after-seconds 8
   ```
   Success = periodic `Closed-loop present timing live: measured interval X.XX ms (Y.Y Hz)` lines, where the Hz tracks
   the display (e.g. ~141 Hz clamped on a 144 Hz panel). No such line ⇒ the wait never returns `Success` (see step 4).

3. **Validation-clean?** Run with the Vulkan validation layer enabled and watch for any `VkPresentIdKHR` /
   `vkWaitForPresentKHR` complaints:
   ```sh
   VK_INSTANCE_LAYERS=VK_LAYER_KHRONOS_validation dotnet run --project src/Puck.Demo -- --mini-action --exit-after-seconds 5
   ```
   Success = no validation errors mentioning present-id/present-wait.

4. **Watch for the self-disable log.** If `present_wait` is ENABLED but the wiring is subtly wrong, the presenter
   disables itself once and prints the offending result:
   ```
   [present-timing] vkWaitForPresentKHR returned <VkResult>; disabling closed-loop present timing ...
   ```
   That `<VkResult>` points straight at the bug — debug per the checklist below.

5. **DirectX regression (should still be active):**
   ```sh
   PUCK_PRESENT_TIMING=1 dotnet run --project src/Puck.Demo -- --world --backend directx --present-mode adaptive --exit-after-seconds 8
   ```
   Expect the interval log too (DX uses `GetFrameStatistics.SyncQPCTime`; may show `unavailable` transiently right
   after resize — that's the expected `DISJOINT` fallback).

6. **On a real VRR display:** with `--present-mode adaptive` and the framerate uncapped (set `renderRate: 0` in the run
   document, or leave the default), confirm the monitor's refresh follows the present cadence within `[Vmin, Vmax−3]`.

## If `present_wait` is available but it misbehaves — debug checklist

The Vulkan path mirrors the codebase's existing hand-written P/Invoke patterns, but was never exercised live. Check, in
order of likelihood:

- **sType constants** — CORRECTED (these were the transposed bug; see the RESOLVED block at the top). The right values,
  matching `vulkan_core.h` 1.4.350: `VkPresentIdKHR` (the present-INFO struct) = `1000294000` in
  `VulkanNativeFramePresentationApi`; feature sTypes `present_id` (`VkPhysicalDevicePresentIdFeaturesKHR`) = `1000294001`,
  `present_wait` (`VkPhysicalDevicePresentWaitFeaturesKHR`) = `1000248000` in `VulkanLogicalDeviceFactory`. The two
  `present_id` values are trivially transposed; a wrong sType makes the driver misread the struct — validation will flag it.
- **`VkPresentIdKHR` layout** (`src/Puck.Vulkan/Bindings/VkPresentIdKhr.cs`): `{ uint sType; nint pNext; uint
  swapchainCount; nint pPresentIds; }`, `Sequential`, natural alignment (matches the existing `VkPresentInfoKhr`). The
  present-id is a single `uint64` in its own unmanaged block, pointed to by `pPresentIds`.
- **Present-id lifecycle** (`VulkanFramePresenter`): monotonic from 1, `0` is the "no id" sentinel, **reset per
  swapchain** (it resets when `swapchain.Handle` changes). The wait targets the **prior** frame's id; the first present
  after a (re)create has no prior id and is skipped.
- **The wait** (`WaitForPresent`): bounded to **50 ms** so it can never hang the pump thread; a `Timeout` is benign
  (sample skipped), a hard error self-disables.
- **Feature enablement**: the generic 256-byte feature chain in `VulkanNativeLogicalDeviceApi.CreateLogicalDevice`
  enables each requested struct's **first** `VkBool32` — which is exactly `presentId` / `presentWait`. No per-struct
  code needed; just the two sTypes added in the factory.

## File map (the closed-loop change set)

- Neutral seam: `src/Puck.Abstractions/IPresentTimingFeedback.cs` (`IPresentTimingFeedback`, `PresentTimingSample`).
- Pacer phase-lock + diagnostics: `src/Puck.Launcher/LauncherWindowHostedService.cs`.
- DirectX: `src/Puck.DirectX.Presentation/DirectXSurfaceCompositor.cs` (`CapturePresentTiming`/`TryGetPresentTiming`),
  `DirectXSurfacePresenter.cs` (implements the seam).
- Vulkan: `src/Puck.Vulkan/Bindings/VkPresentIdKhr.cs`, `Messages/VulkanPresentRequest.cs` (+`PresentId`),
  `Interfaces/IVulkanFramePresentationApi.cs` + `Apis/VulkanNativeFramePresentationApi.cs` (chain + `WaitForPresent` +
  `SupportsPresentWait`), `Interfaces/IVulkanFramePresenter.cs` + `VulkanFramePresenter.cs` (id lifecycle + wait +
  timing), `Factories/VulkanLogicalDeviceFactory.cs` (gate + enable), `Puck.Vulkan.Presentation/VulkanRenderer.cs` +
  `VulkanSurfacePresenter.cs` (forward + implement the seam).

## Broader session context (so this resumes cold)

This branch also shipped, all verified, all determinism-safe:

- **Floating-origin render seam + alpha interpolation** — the GPU only ever sees camera-relative float32 (rebased in
  fixed point before the cast); the renderer interpolates `lerp(prevTick, currTick, alpha)` at that seam. (Puck.Demo
  MiniAction.)
- **`WorldCoord3` cell+offset coordinate** (`src/Puck.Maths`) — planet-scale range (astronomical↔microscopic);
  `--validate-mini-action` proves cell-0 ≡ cell-1e9 local-trajectory bit-identical, and a far-cell GPU render at ~1e15
  units is pixel-perfect (`PUCK_MINIACTION_CELL=<n>`).
- **Adaptive VRR core** — refresh-range query (`IDisplayRefreshInfo`), adaptive pacer clamp `[Vmin, Vmax−3]` +
  high-resolution waiter (`IPrecisionWaiter`), Vulkan `FIFO_RELAXED` + neutral `PresentMode.Adaptive` matching DX
  tearing. See `docs/feature-parity-table.md` (Presentation/Swapchain rows).

## Still open after present-timing is verified

- Fullscreen-exclusive present path.
- Multi-monitor: the refresh range is queried once at startup — re-query on `WM_DISPLAYCHANGE` when the window moves
  monitors.
- Validate the actual cadence on a VRR display (step 6).

## Moving machines (git)

The work is in the working tree on `features/input-refinement`. To move it: commit the tree and push the branch here,
then `git pull` on the new machine. (Git is owned by the repo author — no auto-commit was made.)
