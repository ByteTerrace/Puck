# Cross-backend parity validation

`Puck.Demo` renders one SDF scene from a single HLSL source on **both** a Vulkan backend (SPIR-V) and a
Direct3D 12 backend (DXIL). The parity gate proves the two backends agree — not byte-for-byte (driver-level
floating-point codegen differs across APIs and cannot be forced equal), but **tolerance-aware**, and it
**localizes** any divergence to a stage of the pipeline.

## Two live backends, not a checked-in baseline

Unlike a single-backend determinism gate that compares one backend against a committed golden image, this gate
renders **both backends live in one process** and diffs them against each other. There is nothing to bless and
no baseline to drift; the comparison is always current-hardware vs current-hardware.

Run it with:

```
dotnet run --project src/Puck.Demo -- --validate      # or: dotnet run --file tools/Tools.cs -- parity
```

`--validate` forces a Vulkan host (Vulkan owns the window; the Direct3D 12 producer is headless), renders
Direct3D 12 fully first then Vulkan (so the host queue never races an offscreen submit), reads each back to host
RGBA, diffs, writes `artifacts/parity/`, and exits **0** (pass), **1** (gate-fail), or **2** (infra-fail, e.g.
no Direct3D 12 device — it fails loudly rather than skipping).

## Debug view modes

The shader decodes a view mode from the camera push constant (`forward.w`) and the host sets it via
`SdfProducerNode.DebugMode`. Interactively: `debug.view <mode>` or **F4** to cycle.

| mode | name              | output                                  |
|------|-------------------|-----------------------------------------|
| 0    | `off`             | the final shaded image                  |
| 1    | `depth`           | `traveled / MaxDistance`, grayscale     |
| 2    | `normals`         | surface normal, `n * 0.5 + 0.5`         |
| 3    | `raydir`          | ray direction, `rd * 0.5 + 0.5`         |
| 4    | `material-id`     | a distinct hue per material id          |
| 5    | `iteration-count` | march steps / `MaxSteps`, grayscale     |

`--validate` runs every mode, diffing each independently. Per-mode PNGs (`vulkan-<mode>.png`,
`directx-<mode>.png`, `diff-<mode>.png`) and a combined `report.json` are written to `artifacts/parity/`.

## Metrics and thresholds

Per pixel, `d = max(|ΔR|, |ΔG|, |ΔB|)` (alpha is ignored). The metrics (`ParityMetrics`) are:

- `percentDiffering`, `differingPixels`, `maxChannelDelta`, `meanAbsError`, `deltaHistogram`
- `unitDeltaFraction` — fraction of differing pixels at exactly `d == 1`
- `isolatedFraction` — **the smart signal**: fraction of differing pixels with at most one differing neighbour

Benign cross-backend divergence is a sprinkle of **isolated ±1-LSB quantization flips** wherever the continuous
shading slowly crosses a 1/255 boundary (measured RTX 4070 baseline: ~0.13% of pixels, ~99% isolated, all ±1). A
real bug instead **spreads into contiguous regions** — a shifted silhouette or a recoloured surface — so its
differing pixels clump and `isolatedFraction` collapses. (A luminance-gradient "edge" test was tried and
rejected: the benign flips sit on gentle fog/sky gradients with sub-LSB slope, so most register as flat.)

Thresholds (`ParityThresholds`, GPU-specific, ~4× headroom, overridable) come in two flavours:

- **Continuous** views (`off`, `depth`, `normals`, `raydir`): `maxChannelDelta ≤ 1`, `percentDiffering ≤ 0.5%`,
  `unitDeltaFraction ≥ 0.99`, `isolatedFraction ≥ 0.90`, `meanAbsError ≤ 0.05`.
- **Discrete** views (`material-id`, `iteration-count`): the ±1-specific checks are dropped (a single benign
  step or material-boundary flip is one isolated pixel but a multi-LSB delta), leaning on `percentDiffering`,
  `isolatedFraction`, and `meanAbsError`, which still catch a real bug.

`diff-<mode>.png` is a grayscale max-channel-delta heatmap amplified 64× (`min(255, d*64)`) so divergences glow
without a 1-LSB image looking alarming; the authoritative numbers are in `report.json`.

## Localization tree

Because each stage has its own view, the mode that first fails localizes the bug:

- `off` fails but `normals`/`depth`/`raydir` pass ⇒ **shading** (lighting, albedo, fog, tonemap).
- `depth` or `raydir` fails ⇒ **raymarch / camera** (ray setup, march distance).
- `normals` fails (with `depth` passing) ⇒ **normal estimation**.
- `material-id` fails ⇒ **scene/material assignment** at the hit.
- `iteration-count` fails (with the others passing) ⇒ **march convergence** (step counts diverging widely).
