using System.Numerics;
using Puck.Abstractions.Gpu;
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
    // The worst-case capacity envelope: a synthetic full-capacity creation run through the SAME planner emission
    // the live bakes use, so the floor tracks the emission by construction.
    private static readonly Lazy<(int Words, int Instances)> WorstCase = new(valueFactory: MeasureWorstCase);

    private SdfWorldEngine? m_engine;
    private uint m_width;
    private uint m_height;

    /// <summary>Rasterizes one view of a plan, reusing (or lazily building) the persistent engine.</summary>
    /// <param name="device">The live GPU device.</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="viewIndex">Which view to rasterize.</param>
    /// <returns>The rasterized view (supersampled extent).</returns>
    public RasterizedView Rasterize(IGpuDeviceContext device, IGpuComputeServices gpu, BakePlan plan, int viewIndex) {
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

        return new RasterizedView(Height: (int)height, Name: view.Name, Rgba: m_engine.RenderFrame(frame: frame), Width: (int)width);
    }

    /// <inheritdoc/>
    public void Dispose() {
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
            kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv", directory: DemoShaders.SdfDirectory),
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
                Position: new Vector3((index * 0.05f), 1f, 0f),
                Rotation: rotation,
                Scale: new Vector3(CreatorScene.MaxScale),
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
