using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.Compositing;
using Puck.SdfVm;
using Puck.Text;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the GLYPH DECAL tier — dense reading text sampled AT THE HIT on a
/// <see cref="SdfShapeType.ScreenSlab"/> carrier as a MATERIAL flavor (<see cref="SdfWorldEngine.SetScreenDecal"/>),
/// NOT marched as geometry the way <see cref="WorldGlyphStage"/>'s <see cref="SdfShapeType.Glyph"/> op is. One scene —
/// a screen slab facing the camera whose face carries a grid of glyph cells baked from the deterministic in-process SDF
/// fixture (<see cref="GlyphFixture"/>) — renders through the same <see cref="SdfWorldEngine"/> on Vulkan (SPIR-V) and
/// Direct3D 12 (DXIL) and must agree within the calibrated <c>WorldHighContrast</c> thresholds (the family the
/// sampled-texture / glyph neighbours use — a decal glyph's high-contrast letter edges are exactly where a benign
/// ±1-ULP field difference can flip an isolated boundary pixel).
/// <para>The LEGIBILITY TOOTH (single-backend, Vulkan) is what parity cannot give: a wrong-but-SYMMETRIC
/// reconstruction — a flat panel (the cells never reached the shader), an inverted threshold (text and background
/// swapped), or a washed-out AA — renders IDENTICALLY on both backends, so the parity diff passes while the feature is
/// absent. This gate renders the SAME slab twice on the Vulkan host — once with the text decal, once with an all-BLANK
/// grid — and counts the foreground (bright-green) text pixels. The text render must light a healthy BAND of them; the
/// blank control must be ~0 (its whole face is the cell background). A regression to a flat/absent decal collapses the
/// band while parity would still pass.</para>
/// <para>Deterministic: the fixed scene, the fixed camera, a single frame at time 0, the in-process fixture atlas — no
/// wall-clock, no RNG, no installed-font dependency.</para>
/// <para>⚠CALIBRATION (live, by the lead — the authoring session had no GPU): <see cref="MinTextPixels"/> /
/// <see cref="MaxTextPixels"/> and the channel thresholds are REASONED PLACEHOLDERS, not measured. The floor sits well
/// above the blank control (~0) and below the letters' plausible coverage; the ceiling catches an inverted/washed
/// reconstruction that lights the whole face. Re-measure against a live host render and tighten toward the observed
/// band (and calibrate the shader's <c>DecalMinAa</c> at the same time).</para>
/// </summary>
internal sealed class WorldGlyphDecalStage : IPostStage {
    private const float ScreenHalfHeight = 0.9f;
    private const float ScreenHalfWidth = 1.4f;
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    private static readonly Vector3 ScreenOrigin = new(x: 0f, y: 1.2f, z: 0f);

    // The decal grid — three rows of four cells spelling the fixture charset (P/U/C/K only), every cell filled so the
    // text lights a large, unambiguous band of foreground pixels.
    private static readonly string[] DecalRows = ["PUCK", "CPKU", "KUCP"];

    private const int DecalColumns = 4;

    // The phosphor palette: a bright, near-pure green foreground over a near-black background, packed rgba8 as the
    // shader unpacks it (R in the low byte). Deliberately LOW red/blue (unlike a mixed terminal green) so the tooth's
    // "green high while red/blue low" invariant survives the emissive read + distance fog with margin.
    private const uint DecalForeground = 0xFF50F028u; // (r,g,b,a) = (0x28, 0xf0, 0x50, 0xff)
    private const uint DecalBackground = 0xFF080B06u; // (0x06, 0x0b, 0x08, 0xff)

    // A foreground/text pixel lights green at or above this 8-bit level and green must
    // DOMINATE red/blue by GreenDominance — the near-pure-green foreground satisfies both, while a grey surface
    // (ground/bezel, where the channels track together) fails the dominance test even when its green clears the floor.
    private const byte MinTextGreen = 90;
    private const int GreenDominance = 60; // green must exceed red and blue by at least this (the phosphor is pure green)
    // Calibrated on the Vulkan host: the 4x3 P/U/C/K grid lights 17,190 foreground pixels; the blank
    // control lights 0. Half the measurement - codegen rolls move edges by pixels, a flat/absent decal collapses to 0.
    private const int MinTextPixels = 8500;
    // Approximately 5x the measured 17,190 — an inverted threshold or blown-out AA that floods the whole
    // face green (the slab fills a large fraction of the 960×600 frame) trips this ceiling.
    private const int MaxTextPixels = 90000;

    /// <inheritdoc/>
    public string Name => "world-glyph-decal";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // A ground plane plus a screen slab centred at ScreenOrigin, front face toward +Z (the camera side, no rotation) —
    // the same trivially-agreeing frame WorldScreenStage uses, so worldRight/worldUp (+X/+Y) are the slab's actual local
    // axes and the decal UV maps straight onto its face. Screen index 0.
    internal static SdfProgram BuildDecalScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.30f, y: 0.33f, z: 0.38f)));
        var bezel = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.08f, y: 0.08f, z: 0.10f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // A bezel box behind the slab so the terminal reads as a panel, not a bare rectangle — a plain material box.
            .Translate(offset: ScreenOrigin)
            .Box(halfExtents: new Vector3(x: (ScreenHalfWidth + 0.08f), y: (ScreenHalfHeight + 0.08f), z: 0.08f), round: 0.02f, material: bezel)
            .ResetPoint()
            .Translate(offset: ScreenOrigin)
            .ScreenSlab(
                halfExtents: new Vector3(x: ScreenHalfWidth, y: ScreenHalfHeight, z: 0.1f),
                round: 0f,
                worldOrigin: ScreenOrigin,
                worldRight: Vector3.UnitX,
                worldUp: Vector3.UnitY,
                screenIndex: 0
            )
            .Build();
    }
    internal static SdfFrame BuildDecalFrame(SdfProgram program, uint width, uint height) {
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(x: 0f, y: 1.4f, z: 5.2f),
            target: ScreenOrigin,
            fieldOfViewRadians: (50f * (MathF.PI / 180f)),
            viewportWidth: width,
            viewportHeight: height
        );

        return new SdfFrame(
            Program: program,
            ProgramChanged: false,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            Time: 0f,
            WarpAmount: 0f
        );
    }

    // Bakes the decal cell grid (row-major, DecalWordsPerCell = 4 uints each): each cell packs its glyph's atlas UV rect
    // (unorm2x16 corners, matching sdfGlyphUnpackUv) + the fg/bg colours; a blank cell (a char absent from the atlas)
    // packs equal UV corners so the shader shows only the background. `blank` forces every cell blank (the control).
    internal static uint[] BakeDecalCells(FontAtlas atlas, bool blank) {
        var rows = DecalRows.Length;
        var cells = new uint[((rows * DecalColumns) * 4)];
        var atlasWidth = (float)atlas.Width;
        var atlasHeight = (float)atlas.Height;

        for (var row = 0; (row < rows); row++) {
            var line = DecalRows[row];

            for (var column = 0; (column < DecalColumns); column++) {
                var index = (((row * DecalColumns) + column) * 4);
                var character = ((column < line.Length) ? line[column] : ' ');

                cells[(index + 2)] = DecalForeground;
                cells[(index + 3)] = DecalBackground;

                if (blank || !atlas.TryGetGlyph(unicode: character, glyph: out var glyph) || (glyph.AtlasBounds is not { } atlasBounds)) {
                    cells[(index + 0)] = 0u; // uvTopLeft == uvBottomRight => a blank cell (background only)
                    cells[(index + 1)] = 0u;

                    continue;
                }

                cells[(index + 0)] = PackUv(u: (atlasBounds.Left / atlasWidth), v: (atlasBounds.Top / atlasHeight));
                cells[(index + 1)] = PackUv(u: (atlasBounds.Right / atlasWidth), v: (atlasBounds.Bottom / atlasHeight));
            }
        }

        return cells;
    }

    private static uint PackUv(float u, float v) {
        var packedU = (uint)Math.Clamp(value: (int)MathF.Round(x: (u * 65535f)), max: 65535, min: 0);
        var packedV = (uint)Math.Clamp(value: (int)MathF.Round(x: (v * 65535f)), max: 65535, min: 0);

        return packedU | (packedV << 16);
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The DECAL-PROOF fixture: RGB carry the true field, ALPHA is zeroed. The decal medians RGB, so this renders
        // identically to the clean fixture — but an alpha-reading regression sees empty cells, lights ~zero text
        // pixels, and fails the legibility floor. (WorldGlyphStage keeps the clean fixture: GEOMETRY marches alpha.)
        var atlas = GlyphFixture.BuildDecalProof();
        var atlasImage = (atlas.ImageData ?? throw new InvalidOperationException(message: "The glyph fixture produced no image data."));
        var atlasPixels = atlasImage.RgbaPixels;
        var atlasWidth = (uint)atlas.Width;
        var atlasHeight = (uint)atlas.Height;
        var distanceRange = atlas.DistanceRange;
        var textCells = BakeDecalCells(atlas: atlas, blank: false);
        var blankCells = BakeDecalCells(atlas: atlas, blank: true);
        var program = BuildDecalScene();
        var frame = BuildDecalFrame(program: program, width: WorldWidth, height: WorldHeight);

        // Vulkan reference (SPIR-V) with the text decal, plus a Vulkan-only ALL-BLANK control render of the same scene.
        var vulkanGpu = context.Resolve<IGpuComputeServices>();
        var vulkanDevice = context.RequireGpuDevice();
        var vulkanPixels = RenderDecalFrame(device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv", program: program, frame: frame, atlasPixels: atlasPixels, atlasWidth: atlasWidth, atlasHeight: atlasHeight, distanceRange: distanceRange, cells: textCells);
        var vulkanBlankPixels = RenderDecalFrame(device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv", program: program, frame: frame, atlasPixels: atlasPixels, atlasWidth: atlasWidth, atlasHeight: atlasHeight, distanceRange: distanceRange, cells: blankCells);

        // Direct3D 12 comparand (DXIL) with the same text decal bound.
        var directX = context.RequireDirectXDevice();
        var directXGpu = directX.Services.GetRequiredService<IGpuComputeServices>();
        var directXPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => RenderDecalFrame(device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil", program: program, frame: frame, atlasPixels: atlasPixels, atlasWidth: atlasWidth, atlasHeight: atlasHeight, distanceRange: distanceRange, cells: textCells));

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var diffPath = Path.Combine(path1: context.ArtifactsDirectory, path2: "world-glyph-decal-diff.png");

        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-glyph-decal-vulkan.png"), rgba: vulkanPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-glyph-decal-directx.png"), rgba: directXPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-glyph-decal-blank.png"), rgba: vulkanBlankPixels, width: (int)WorldWidth);
        ParityCheck.WriteDiffImage(comparand: directXPixels, height: (int)WorldHeight, path: diffPath, reference: vulkanPixels, width: (int)WorldWidth);

        var metrics = ParityMetrics.Compute(reference: vulkanPixels, comparand: directXPixels, width: (int)WorldWidth, height: (int)WorldHeight);
        var failures = ParityThresholds.WorldHighContrast.Evaluate(metrics: metrics).ToList();

        // The legibility tooth: the decal must light a healthy band of foreground text pixels the all-blank control does
        // not — parity cannot catch a flat/inverted/washed reconstruction (both backends would agree on it).
        var textPixels = CountTextPixels(pixels: vulkanPixels);
        var blankPixels = CountTextPixels(pixels: vulkanBlankPixels);

        if (textPixels < MinTextPixels) {
            failures.Add(item: $"the decal lit only {textPixels} foreground text pixels (< {MinTextPixels}) — the glyph cells did not reconstruct legible text (a flat/absent decal, which parity would still pass)");
        }

        if (textPixels > MaxTextPixels) {
            failures.Add(item: $"the decal lit {textPixels} foreground pixels (> {MaxTextPixels}) — the whole face is text-coloured, an inverted threshold or blown-out AA");
        }

        if (blankPixels > (MinTextPixels / 4)) {
            failures.Add(item: $"the all-BLANK control lit {blankPixels} foreground pixels (should be ~0) — the cells are not what produce the text (the tooth's baseline is broken)");
        }

        if (failures.Count != 0) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"{ParityCheck.Describe(metrics: metrics)} | text {textPixels}px / blank {blankPixels}px — {string.Join(separator: "; ", values: failures)}");
        }

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{WorldWidth}x{WorldHeight} glyph decal ({DecalColumns}×{DecalRows.Length} cell grid, {atlasWidth}x{atlasHeight} SDF fixture) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds | text {textPixels}px vs blank {blankPixels}px control | {ParityCheck.Describe(metrics: metrics)}");
    }

    // One render of the decal scene: uploads the fixture atlas + binds the decal cell grid to screen slot 0, then
    // renders. Passing the all-blank grid renders the control (every cell background).
    private static byte[] RenderDecalFrame(IGpuDeviceContext device, IGpuComputeServices gpu, string bytecodeExtension, SdfProgram program, SdfFrame frame, ReadOnlyMemory<byte> atlasPixels, uint atlasWidth, uint atlasHeight, float distanceRange, uint[] cells) {
        using var renderer = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: bytecodeExtension),
            options: new SdfWorldEngineOptions(Program: program),
            width: WorldWidth
        );

        renderer.SetGlyphAtlas(rgbaPixels: atlasPixels, width: atlasWidth, height: atlasHeight);
        renderer.SetScreenDecal(screenIndex: 0, columns: DecalColumns, rows: DecalRows.Length, distanceRange: distanceRange, cellWords: cells);

        return renderer.RenderFrame(frame: frame);
    }

    // Counts foreground (near-pure-green phosphor) pixels: the green channel clears MinTextGreen AND dominates red and
    // blue by GreenDominance — the invariant the green-on-dark decal palette makes unambiguous (the dark cell fill, the
    // grey ground/bezel, and the bluish sky/fog all fail the dominance test, whatever their brightness).
    private static int CountTextPixels(byte[] pixels) {
        var count = 0;

        for (var i = 0; (i < ((int)WorldWidth * (int)WorldHeight)); i++) {
            var red = pixels[(i * 4)];
            var green = pixels[((i * 4) + 1)];
            var blue = pixels[((i * 4) + 2)];

            if ((green >= MinTextGreen) && (green > (red + GreenDominance)) && (green > (blue + GreenDominance))) {
                count++;
            }
        }

        return count;
    }
}
