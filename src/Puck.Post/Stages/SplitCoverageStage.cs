using Puck.Capture;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-B stage B6. The split-screen compositor under ANIMATED regions, decoupled from any game: the stage drives a
/// synthetic merged↔split↔merged guillotine easing itself (the same dividers-slide-in layout math the demo's camera
/// director animates) over a fixed set of steps for pane counts 2, 3, and 4, rebuilding the composite rects every
/// step exactly the way <see cref="SdfWorldEngine"/> rebuilds them every frame against full-size per-view sources. For
/// every step it (a) asserts on the CPU that the rects EXACTLY tile [0,1]² — every pixel-center UV is contained by
/// exactly one rect under the compositor kernel's own containment test (no uncovered band, no overlap) and the areas
/// sum to exactly 1 — then (b) renders the world composite through <see cref="Puck.SdfVm.SdfWorldEngine"/> and asserts every
/// active pane's pixel block is non-blank (not all one color, not all near-zero) and no letterbox-colored pixel (the
/// kernel's outside-every-region constant) survives inside the frame. Deterministic: fixed steps, no wall clock.
/// </summary>
internal sealed class SplitCoverageStage : IPostStage {
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint OutputHeight = 128;
    private const uint OutputWidth = 256;
    private const int MinPaneMaxChannel = 32; // a pane must show something brighter than near-black
    // The compositor kernel's letterbox constant float3(0.015, 0.016, 0.02) as stored rgba8 — written ONLY for a pixel
    // outside every viewport region, so its presence inside [0,1]² is an uncovered band. (The scene's darkest legitimate
    // colors — sky ≥ (10,13,18), the cull's empty-tile flat (18,23,34) — cannot collide with it; see the shader.)
    private const byte LetterboxR = 4;
    private const byte LetterboxB = 5;
    private const byte LetterboxG = 4;

    // The eased divider positions to sample: merged (0) → full split (1) → back to merged, on exact binary fractions so
    // the guillotine rect arithmetic below is float-exact.
    private static readonly float[] EaseSteps = [0f, 0.25f, 0.5f, 0.75f, 1f, 0.75f, 0.5f, 0.25f, 0f];
    // The per-pane chase anchors (stand-ins for player positions), fixed and spread around the scene.
    private static readonly Vector3[] PaneAnchors = [
        new Vector3(x: -2f, y: 0.5f, z: 0f),
        new Vector3(x: 2f, y: 0.5f, z: 0f),
        new Vector3(x: 0f, y: 0.5f, z: -2f),
        new Vector3(x: 0.5f, y: 0.5f, z: 2f),
    ];
    private static readonly Vector3 ChaseOffset = new(x: 0f, y: 4f, z: 7f);
    private static readonly Vector3 SharedEye = new(x: 0f, y: 7f, z: 11f);
    private static readonly Vector3 SharedTarget = new(x: 0f, y: 0.5f, z: 0f);
    private static readonly Vector3 TargetLift = new(x: 0f, y: 0.4f, z: 0f);

    /// <inheritdoc/>
    public string Name => "split-coverage";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var program = BuildScene();

        using var renderer = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: OutputHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
            options: new SdfWorldEngineOptions(DynamicTransformCapacity: 1, Program: program),
            width: OutputWidth
        );

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        string? artifactPath = null;

        for (var paneCount = 2; (paneCount <= 4); paneCount++) {
            for (var step = 0; (step < EaseSteps.Length); step++) {
                var ease = EaseSteps[step];
                var rects = new NormalizedRect[paneCount];

                for (var index = 0; (index < paneCount); index++) {
                    rects[index] = PaneRect(count: paneCount, index: index, ease: ease);
                }

                // Layout oracle 1: the pane areas sum to exactly 1 (the ease steps are dyadic, so this is float-exact).
                var areaSum = 0f;

                foreach (var rect in rects) {
                    areaSum += (rect.Width * rect.Height);
                }

                if (areaSum != 1f) {
                    return PostStageOutcome.Fail(detail: $"{paneCount} panes at ease {ease}: pane areas sum to {areaSum} != 1 — the guillotine layout leaks or overlaps");
                }

                // Layout oracle 2: under the compositor kernel's own containment test, every pixel-center UV must fall
                // in EXACTLY one rect — 0 is an uncovered band, ≥2 an overlap (zero-area panes contain nothing).
                for (var y = 0u; (y < OutputHeight); y++) {
                    var v = ((y + 0.5f) / OutputHeight);

                    for (var x = 0u; (x < OutputWidth); x++) {
                        var u = ((x + 0.5f) / OutputWidth);
                        var containing = 0;

                        foreach (var rect in rects) {
                            if (Contains(rect: rect, u: u, v: v)) {
                                containing++;
                            }
                        }

                        if (containing != 1) {
                            return PostStageOutcome.Fail(detail: $"{paneCount} panes at ease {ease}: pixel ({x},{y}) is covered by {containing} rects — the layout does not tile [0,1]^2");
                        }
                    }
                }

                var pixels = renderer.RenderFrame(frame: BuildFrame(ease: ease, program: program, rects: rects));

                // Rendered oracle: no letterbox pixel survives (the layout the GPU composited also left no band), and
                // every ACTIVE pane block is non-blank. First-containing-rect-wins, exactly like the kernel.
                var paneDistinctColors = new HashSet<int>[paneCount];
                var paneMaxChannel = new int[paneCount];

                for (var index = 0; (index < paneCount); index++) {
                    paneDistinctColors[index] = [];
                }

                for (var y = 0u; (y < OutputHeight); y++) {
                    var v = ((y + 0.5f) / OutputHeight);

                    for (var x = 0u; (x < OutputWidth); x++) {
                        var u = ((x + 0.5f) / OutputWidth);
                        var offset = (int)(((y * OutputWidth) + x) * 4);
                        var r = pixels[offset];
                        var g = pixels[(offset + 1)];
                        var b = pixels[(offset + 2)];

                        if (
                            (r == LetterboxR) &&
                            (g == LetterboxG) &&
                            (b == LetterboxB)
                        ) {
                            return PostStageOutcome.Fail(detail: $"{paneCount} panes at ease {ease}: letterbox-colored pixel at ({x},{y}) — an uncovered band inside [0,1]^2");
                        }

                        for (var index = 0; (index < paneCount); index++) {
                            if (Contains(rect: rects[index], u: u, v: v)) {
                                _ = paneDistinctColors[index].Add(item: (r << 16) | (g << 8) | b);
                                paneMaxChannel[index] = Math.Max(val1: paneMaxChannel[index], val2: Math.Max(val1: r, val2: Math.Max(val1: (int)g, val2: b)));
                                break;
                            }
                        }
                    }
                }

                for (var index = 0; (index < paneCount); index++) {
                    var rect = rects[index];

                    if ((rect.Width <= 0f) || (rect.Height <= 0f)) {
                        continue; // a merged-away pane holds no pixels — nothing to prove.
                    }

                    if (paneDistinctColors[index].Count < 2) {
                        return PostStageOutcome.Fail(detail: $"{paneCount} panes at ease {ease}: pane {index} is a single flat color — its view rendered blank");
                    }

                    if (paneMaxChannel[index] < MinPaneMaxChannel) {
                        return PostStageOutcome.Fail(detail: $"{paneCount} panes at ease {ease}: pane {index} is all near-zero (max channel {paneMaxChannel[index]}) — its view rendered dark/blank");
                    }
                }

                // One artifact per pane count, captured mid-transition (the layout state a frozen-rect bug blanks).
                if ((step == 2) && (ease == 0.5f)) {
                    artifactPath = Path.Combine(path1: context.ArtifactsDirectory, path2: $"split-coverage-{paneCount}.png");
                    PngEncoder.Write(height: (int)OutputHeight, path: artifactPath, rgba: pixels, width: (int)OutputWidth);
                }
            }
        }

        return PostStageOutcome.Pass(
            artifactPath: artifactPath,
            detail: $"pane counts 2/3/4 x {EaseSteps.Length} merged<->split ease steps at {OutputWidth}x{OutputHeight}: rects tile [0,1]^2 exactly and every active pane rendered non-blank"
        );
    }

    // The compositor kernel's containment test, replicated in the same float arithmetic:
    // uv >= origin && uv < (origin + size). A zero-area rect contains nothing.
    private static bool Contains(NormalizedRect rect, float u, float v) {
        return (
            (u >= rect.X) &&
            (v >= rect.Y) &&
            (u < (rect.X + rect.Width)) &&
            (v < (rect.Y + rect.Height))
        );
    }

    // The synthetic guillotine layout (mimicking the demo camera director's LayoutRect math): at ease 0 pane 0 fills
    // the screen and the rest are zero-area; at ease 1 the panes tile it (2-up, P0-left + stacked-right, or quad). The
    // dividers slide in; for ANY divider position the emitted rects tile [0,1]² exactly (shared divider coordinates).
    private static NormalizedRect PaneRect(int count, int index, float ease) {
        var divider = (1f - (0.5f * ease)); // 1 → 0.5 as the split eases in

        return count switch {
            2 => ((index == 0)
                ? new NormalizedRect(X: 0f, Y: 0f, Width: divider, Height: 1f)
                : new NormalizedRect(X: divider, Y: 0f, Width: (1f - divider), Height: 1f)),
            3 => index switch {
                0 => new NormalizedRect(X: 0f, Y: 0f, Width: divider, Height: 1f),
                1 => new NormalizedRect(X: divider, Y: 0f, Width: (1f - divider), Height: divider),
                _ => new NormalizedRect(X: divider, Y: divider, Width: (1f - divider), Height: (1f - divider)),
            },
            _ => index switch {
                0 => new NormalizedRect(X: 0f, Y: 0f, Width: divider, Height: divider),
                1 => new NormalizedRect(X: divider, Y: 0f, Width: (1f - divider), Height: divider),
                2 => new NormalizedRect(X: 0f, Y: divider, Width: divider, Height: (1f - divider)),
                _ => new NormalizedRect(X: divider, Y: divider, Width: (1f - divider), Height: (1f - divider)),
            },
        };
    }

    // A small showcase scene: a ground plane and three landmarks, so every camera angle has structure to render.
    private static SdfProgram BuildScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.55f, y: 0.55f, z: 0.6f)));
        var red = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.85f, y: 0.25f, z: 0.2f)));
        var blue = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.2f, y: 0.5f, z: 0.85f)));
        var yellow = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.9f, y: 0.8f, z: 0.25f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .Translate(offset: new Vector3(x: -1.5f, y: 1f, z: 0f))
            .Sphere(radius: 1f, material: red)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 1.5f, y: 0.75f, z: 0f))
            .Sphere(radius: 0.75f, material: blue)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 0f, y: 0.5f, z: -1.5f))
            .Box(halfExtents: new Vector3(x: 0.6f, y: 0.5f, z: 0.6f), round: 0.1f, material: yellow)
            .Build();
    }

    // One camera per pane, eased from the shared framing to a per-pane chase — the shape of the animation the camera
    // director performs, rebuilt deterministically from the fixed anchors. Aspect follows the pane's live pixel size.
    private static SdfFrame BuildFrame(float ease, SdfProgram program, NormalizedRect[] rects) {
        var views = new SdfViewSnapshot[rects.Length];

        for (var index = 0; (index < rects.Length); index++) {
            var rect = rects[index];
            var eye = Vector3.Lerp(value1: SharedEye, value2: (PaneAnchors[index] + ChaseOffset), amount: ease);
            var target = Vector3.Lerp(value1: SharedTarget, value2: (PaneAnchors[index] + TargetLift), amount: ease);
            var paneWidth = Math.Max(val1: 1u, val2: (uint)(rect.Width * OutputWidth));
            var paneHeight = Math.Max(val1: 1u, val2: (uint)(rect.Height * OutputHeight));

            views[index] = new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(position: eye, target: target, fieldOfViewRadians: FieldOfViewRadians, viewportWidth: paneWidth, viewportHeight: paneHeight),
                Region: rect
            );
        }

        return new SdfFrame(
            Program: program,
            ProgramChanged: false,
            Views: views,
            Time: 0f,
            WarpAmount: 0f
        );
    }
}
