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
/// Tier-C stage. Cross-backend parity for the <see cref="SdfShapeType.Glyph"/> primitive — text as REAL world
/// geometry, sampled from a font atlas as a DISTANCE-level field (not the material-level sampling of
/// <see cref="WorldScreenStage"/>'s ScreenSlab). One scene, baked from a deterministic in-process SDF fixture
/// (<see cref="GlyphFixture"/>, uploaded via <see cref="SdfWorldEngine.SetGlyphAtlas"/>), exercises every axis of the
/// op: RAISED text (Union), ENGRAVED text (Subtraction carving a slab — the showcase), a SMOOTH-blended glyph
/// (SmoothUnion fused with a sphere), and text at TWO scales. The identical scene renders through the same
/// <see cref="SdfWorldEngine"/> on Vulkan (SPIR-V) and Direct3D 12 (DXIL) and must agree within the calibrated
/// <c>WorldHighContrast</c> thresholds — the family the sampled-texture / material-seam neighbours use, because a
/// glyph's high-contrast letter edges are exactly where a benign ±1-ULP field difference can flip an isolated
/// boundary pixel's winning surface. A no-op guard (the SAME scene rendered with NO atlas bound, so every glyph
/// collapses to its conservative cell box) must differ substantially, proving the atlas actually reaches the shader.
/// </summary>
internal sealed class WorldGlyphStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-glyph";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // The THREE canonical label compositions on one backing slab plus a smooth-blended glyph — a flat program (no
    // instances; the parity gate needs the whole field, not the cull):
    //   EMBOSS  — "PUCK" Union'd onto the slab's +Z face, PROUD by a positive relief. The glyph plane sits AT the face
    //             but the extrude straddles it (back buried inside the slab, front protruding), so the two solids' zero
    //             sets NEVER coincide — coplanar text (zero offset) produces the coincident-surface speckle instead.
    //   ENGRAVE — "PUCK" Subtracted into the same face lower down (the showcase); subtraction is a LOCAL accumulator op
    //             that carves only where the letter solids penetrate the slab.
    //   FLOAT   — "PUCK" Union'd free-standing above the slab at a LARGER scale (the two-scales axis).
    // Then a single 'P' SmoothUnion'd onto a sphere (the glyph's Data1.x smooth radius rides the same lane every shape
    // uses — the packed-UV layout kept it free).
    internal static SdfProgram BuildGlyphScene(FontAtlas atlas) {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.30f, 0.33f, 0.38f)));
        var slabMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.52f, 0.48f, 0.42f), Specular: 0.3f, Shininess: 24f));
        var embossMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.90f, 0.55f, 0.20f), Emissive: 0.25f));
        var floatMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.92f, 0.72f, 0.18f), Emissive: 0.35f));
        var smoothMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.28f, 0.66f, 0.90f), Emissive: 0.20f));
        var slabFrontZ = 0.30f;

        _ = builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // The backing slab: front face at z = slabFrontZ.
            .Translate(offset: new Vector3(0f, 1.35f, 0f))
            .Box(halfExtents: new Vector3(2.2f, 1.15f, slabFrontZ), round: 0.03f, material: slabMaterial)
            .ResetPoint();

        // EMBOSS: the glyph plane AT the slab face; extrudeHalfDepth 0.10 straddles it (back to z=0.20 inside, front to
        // z=0.40 proud) — a positive relief that is NEVER coplanar with the slab surface.
        _ = builder.Text(atlas: atlas, text: "PUCK", origin: new Vector3(-1.40f, 1.72f, slabFrontZ), right: Vector3.UnitX, up: Vector3.UnitY, worldEmHeight: 0.58f, material: embossMaterial, blend: SdfBlendOp.Union, extrudeHalfDepth: 0.10f);

        // ENGRAVE: carved into the same face lower down; depth 0.20 cuts a clean channel below the surface.
        _ = builder.Text(atlas: atlas, text: "PUCK", origin: new Vector3(-1.40f, 0.72f, slabFrontZ), right: Vector3.UnitX, up: Vector3.UnitY, worldEmHeight: 0.58f, material: slabMaterial, blend: SdfBlendOp.Subtraction, extrudeHalfDepth: 0.20f);

        // FLOAT: free-standing, a LARGER scale, above the slab.
        _ = builder.Text(atlas: atlas, text: "PUCK", origin: new Vector3(-2.35f, 2.85f, 0f), right: Vector3.UnitX, up: Vector3.UnitY, worldEmHeight: 1.05f, material: floatMaterial, blend: SdfBlendOp.Union, extrudeHalfDepth: 0.15f);

        // SMOOTH: a sphere, then a 'P' SmoothUnion'd onto it — lifted clear of the ground so the smooth halo never
        // reaches the floor plane (a smooth blend against a coincident/near surface would fillet into the ground).
        _ = builder
            .Translate(offset: new Vector3(-2.85f, 1.25f, 1.40f))
            .Sphere(radius: 0.55f, material: smoothMaterial)
            .ResetPoint()
            .Text(atlas: atlas, text: "P", origin: new Vector3(-3.25f, 0.92f, 1.65f), right: Vector3.UnitX, up: Vector3.UnitY, worldEmHeight: 0.85f, material: smoothMaterial, blend: SdfBlendOp.SmoothUnion, extrudeHalfDepth: 0.30f, smooth: 0.28f);

        return builder.Build();
    }

    internal static SdfFrame BuildGlyphFrame(SdfProgram program, uint width, uint height) {
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(0.4f, 2.15f, 7.6f),
            target: new Vector3(-0.4f, 1.35f, 0f),
            fieldOfViewRadians: (55f * (MathF.PI / 180f)),
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

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        var atlas = GlyphFixture.Build();
        var atlasPixels = (atlas.ImageData ?? throw new InvalidOperationException(message: "The glyph fixture produced no image data.")).RgbaPixels;
        var atlasWidth = (uint)atlas.Width;
        var atlasHeight = (uint)atlas.Height;
        var program = BuildGlyphScene(atlas: atlas);
        var frame = BuildGlyphFrame(program: program, width: WorldWidth, height: WorldHeight);

        // Vulkan reference (SPIR-V) with the atlas bound, plus a Vulkan-only NO-ATLAS control render of the same scene.
        var vulkanGpu = context.Resolve<IGpuComputeServices>();
        var vulkanDevice = context.RequireGpuDevice();
        var vulkanPixels = RenderGlyphFrame(device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv", program: program, frame: frame, atlasPixels: atlasPixels, atlasWidth: atlasWidth, atlasHeight: atlasHeight);
        var vulkanNoAtlasPixels = RenderGlyphFrame(device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv", program: program, frame: frame, atlasPixels: ReadOnlyMemory<byte>.Empty, atlasWidth: 0, atlasHeight: 0);

        // Direct3D 12 comparand (DXIL) with the same atlas bound.
        var directX = context.RequireDirectXDevice();
        var directXGpu = directX.Services.GetRequiredService<IGpuComputeServices>();
        var directXPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => RenderGlyphFrame(device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil", program: program, frame: frame, atlasPixels: atlasPixels, atlasWidth: atlasWidth, atlasHeight: atlasHeight));

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var diffPath = Path.Combine(context.ArtifactsDirectory, "world-glyph-diff.png");

        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-glyph-vulkan.png"), rgba: vulkanPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-glyph-directx.png"), rgba: directXPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-glyph-noatlas.png"), rgba: vulkanNoAtlasPixels, width: (int)WorldWidth);
        ParityCheck.WriteDiffImage(comparand: directXPixels, height: (int)WorldHeight, path: diffPath, reference: vulkanPixels, width: (int)WorldWidth);

        var metrics = ParityMetrics.Compute(reference: vulkanPixels, comparand: directXPixels, width: (int)WorldWidth, height: (int)WorldHeight);
        var failures = ParityThresholds.WorldHighContrast.Evaluate(metrics: metrics).ToList();

        // The no-op guard: the atlas MUST change the picture. With no atlas bound every glyph is its conservative cell
        // box — the engraved word carves a solid rectangle instead of letters, the raised word is a solid slab — so a
        // large fraction of pixels must differ from the atlas-bound render, proving the atlas reached the shader.
        var changedPercent = ChangedPercent(a: vulkanPixels, b: vulkanNoAtlasPixels, width: (int)WorldWidth, height: (int)WorldHeight);

        if (changedPercent < 1.0) {
            failures.Add(item: $"binding the glyph atlas changed only {changedPercent:0.00}% of pixels vs the no-atlas control — the atlas did not reach the shader (glyphs stayed their fallback boxes)");
        }

        if (failures.Count != 0) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"{ParityCheck.Describe(metrics: metrics)} | atlas-vs-none {changedPercent:0.0}% — {string.Join(separator: "; ", values: failures)}");
        }

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{WorldWidth}x{WorldHeight} glyph text (raised + engraved + smooth + two scales; {atlasWidth}x{atlasHeight} SDF fixture) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds | atlas changed {changedPercent:0.0}% vs no-atlas control | {ParityCheck.Describe(metrics: metrics)}");
    }

    // One render of the glyph scene with the atlas bound (empty atlasPixels = the no-atlas control: every glyph falls
    // back to its cell box).
    private static byte[] RenderGlyphFrame(IGpuDeviceContext device, IGpuComputeServices gpu, string bytecodeExtension, SdfProgram program, SdfFrame frame, ReadOnlyMemory<byte> atlasPixels, uint atlasWidth, uint atlasHeight) {
        using var renderer = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: bytecodeExtension),
            options: new SdfWorldEngineOptions(Program: program),
            width: WorldWidth
        );

        renderer.SetGlyphAtlas(rgbaPixels: atlasPixels, width: atlasWidth, height: atlasHeight);

        return renderer.RenderFrame(frame: frame);
    }

    // The percentage of pixels whose max channel delta exceeds a meaningful threshold — the no-op guard's measure.
    private static double ChangedPercent(byte[] a, byte[] b, int width, int height) {
        var changed = 0;
        var total = (width * height);

        for (var index = 0; (index < total); index++) {
            var offset = (index * 4);
            var delta = Math.Max(Math.Abs(a[offset] - b[offset]), Math.Max(Math.Abs(a[(offset + 1)] - b[(offset + 1)]), Math.Abs(a[(offset + 2)] - b[(offset + 2)])));

            if (delta > 24) {
                changed++;
            }
        }

        return ((100.0 * changed) / total);
    }
}
