using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Authoring;
using Puck.Compositing;
using Puck.Demo.Creator;
using Puck.SdfVm;

namespace Puck.Demo.Forge.Bake;

/// <summary>
/// The live preview's persistent rasterizer: ONE <see cref="SdfWorldEngine"/> created lazily and REUSED across
/// bakes (hot-swapping programs through <see cref="SdfWorldEngine.UploadProgram"/>), recreated only when the raster
/// extent changes (a style's supersample factor or an intent's native size). Its capacity floors come from a
/// worst-case synthetic creation — a full 64-shape pool with the reserved modifier envelope — measured ONCE, so any
/// live creation's program fits the constructed buffers by construction.
/// </summary>
internal sealed class BakeRasterizer : IDisposable {
    // A bounded busy-wait cap for DrainPending: the copy is already submitted, so this only ever waits out the GPU
    // (microseconds); the bound exists purely so a torn-down/lost device — a fence that never signals — can never hang
    // the render thread.
    private const int DrainSpinLimit = 1_000_000;

    // The worst-case capacity envelope: a synthetic full-capacity creation run through the SAME planner emission
    // the live bakes use, so the floor tracks the emission by construction.
    private static readonly Lazy<(int Words, int Instances)> WorstCase = new(valueFactory: MeasureWorstCase);
    private SdfWorldEngine? m_engine;
    private bool m_pending;
    private uint m_width;
    private uint m_height;

    /// <summary>Begins rasterizing one view of a plan — (re)builds/reuses the persistent engine, uploads the view's
    /// program, and submits the render plus a NON-BLOCKING fenced readback. Poll <see cref="TryCompleteRasterize"/> on
    /// a LATER produced frame to collect the pixels; at most one rasterize may be pending at a time.</summary>
    /// <param name="device">The live GPU device.</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="viewIndex">Which view to rasterize.</param>
    public void BeginRasterize(IGpuDeviceContext device, IGpuComputeServices gpu, BakePlan plan, int viewIndex) {
        ArgumentNullException.ThrowIfNull(plan);

        var view = plan.Views[viewIndex];
        var width = (uint)(plan.NativeWidth * plan.Style.SupersampleFactor);
        var height = (uint)(plan.NativeHeight * plan.Style.SupersampleFactor);

        EnsureEngine(device: device, gpu: gpu, height: height, program: view.Program, width: width);
        m_engine!.UploadProgram(program: view.Program);

        var frame = new SdfFrame(
            Program: view.Program,
            ProgramChanged: false,
            Views: [new SdfViewSnapshot(Camera: view.Camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            Time: 0f,
            WarpAmount: 0f
        );

        m_engine.SubmitFramePipelined(frame: frame);
        m_pending = true;
    }

    /// <summary>Tries to complete the pending <see cref="BeginRasterize"/> — polls the fenced readback and, once it
    /// has landed, copies out the supersampled view. Returns <see langword="false"/> (and a default view) while the
    /// GPU copy is still in flight, so the caller spreads the wait across produced frames.</summary>
    /// <param name="plan">The plan whose view is being rasterized (its name + extent stamp the result).</param>
    /// <param name="viewIndex">Which view is being completed.</param>
    /// <param name="view">When this returns <see langword="true"/>, the rasterized view (supersampled extent).</param>
    /// <returns>Whether the pixels were ready and <paramref name="view"/> was produced.</returns>
    public bool TryCompleteRasterize(BakePlan plan, int viewIndex, out RasterizedView view) {
        ArgumentNullException.ThrowIfNull(plan);

        if ((m_engine is null) || !m_engine.IsFramePixelsReady()) {
            view = default!;

            return false;
        }

        // Copy OUT of the reusable staging view before the next SubmitRead reuses the same buffer.
        var pixels = m_engine.AcquireFramePixels().ToArray();
        var source = plan.Views[viewIndex];
        var width = (uint)(plan.NativeWidth * plan.Style.SupersampleFactor);
        var height = (uint)(plan.NativeHeight * plan.Style.SupersampleFactor);

        m_pending = false;
        view = new RasterizedView(Height: (int)height, Name: source.Name, Rgba: pixels, Width: (int)width);

        return true;
    }

    /// <summary>Drains any pending pipelined rasterize — bounded-busy-waits the fenced readback to completion and
    /// discards the pixels — so the shared engine's single-in-flight guard is clear before the engine is reused (a
    /// mid-bake edit) or disposed. A no-op when nothing is pending. If the device never signals (torn down/lost), the
    /// engine is dropped so its guard resets on the next build rather than the render thread hanging.</summary>
    public void DrainPending() {
        if ((m_engine is null) || !m_pending) {
            return;
        }

        for (var spin = 0; ((spin < DrainSpinLimit) && !m_engine.IsFramePixelsReady()); spin++) {
            // Spin: the copy is already submitted; this only waits out the GPU, never the sim clock.
        }

        if (m_engine.IsFramePixelsReady()) {
            _ = m_engine.AcquireFramePixels();
        } else {
            m_engine.Dispose();
            m_engine = null;
        }

        m_pending = false;
    }

    /// <inheritdoc/>
    public void Dispose() {
        DrainPending();
        m_engine?.Dispose();
        m_engine = null;
    }

    private void EnsureEngine(IGpuDeviceContext device, IGpuComputeServices gpu, uint height, SdfProgram program, uint width) {
        if ((m_engine is not null) && (m_width == width) && (m_height == height)) {
            return;
        }

        m_engine?.Dispose();
        m_engine = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: height,
            kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
            options: new SdfWorldEngineOptions(
                InstanceCapacity: WorstCase.Value.Instances,
                Program: program,
                ProgramWordCapacity: WorstCase.Value.Words,
                ViewportCapacity: 1
            ),
            width: width
        );
        m_width = width;
        m_height = height;
    }

    // The synthetic worst case: the pool capacity's worth of the wordiest arrangement — every shape rotated,
    // scaled, smooth-blended — planned through CreationBakePlanner so the measured program IS the live emission.
    private static (int Words, int Instances) MeasureWorstCase() {
        var shapes = new List<ShapeDocument>(capacity: CreatorScene.Capacity);
        var rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0.7f);

        for (var index = 0; (index < CreatorScene.Capacity); index++) {
            shapes.Add(item: new ShapeDocument(
                Blend: SdfBlendOp.SmoothSubtraction,
                Group: 1,
                Id: index,
                Material: (index % CreatorScene.PaletteSize),
                Name: null,
                Position: new Vector3(x: (index * 0.05f), y: 1f, z: 0f),
                Rotation: rotation,
                Scale: new Vector3(value: CreatorScene.MaxScale),
                Smooth: CreatorScene.MaxSmooth,
                Type: AvatarPrimitive.Capsule
            ));
        }

        var document = new CreationDocument(
            BakeStyle: null,
            Frames: null,
            Intent: CreatorIntent.Object,
            Name: "probe",
            Palette: null,
            Schema: CreationDocument.CurrentSchema,
            Shapes: shapes
        );
        var plan = CreationBakePlanner.Plan(document: document, style: BakeStyles.Classic, target: BakeTarget.Cgb);
        var program = plan.Views[0].Program;

        return (program.Words.Length, program.Instances.Count);
    }
}
