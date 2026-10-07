using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Xunit;

namespace Puck.Testing;

// One sdf.world view rendered as the World renders its view of the world: a residency, the package's recorders resolving
// the view instance to the residency's first view, and its shared environment producer ahead of that root. A frame is one produced
// graph frame, and the view's readiness and captures are the World's: the residency's tables built and the root served.
// The host backend also selects the display-encode shaders when a float output is captured.
internal sealed class SdfTestView : IDisposable {
    public const string EnvironmentInstance = "world.environment";
    public const string Instance = "world";

    private readonly uint m_extent;

    private bool m_disposed;
    private long m_frame;

    /// <summary>Creates the native world view and its capture path on the supplied host backend.</summary>
    /// <param name="residency">The world residency whose first view the graph renders.</param>
    /// <param name="pipelines">The residency's shared pipeline catalog.</param>
    /// <param name="device">The device that renders the view and encodes its captures.</param>
    /// <param name="extent">The width and height of the square output.</param>
    /// <param name="hostsOnDirectX">Whether the graph's display encode reads DXIL rather than SPIR-V. This must
    /// match the bytecode backend supplied to the residency and pipeline catalog.</param>
    public SdfTestView(SdfWorldResidency residency, SdfWorldPipelineCatalog pipelines, IGpuDeviceContext device, uint extent, bool hostsOnDirectX) {
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        Passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        Environment = new SdfSkyEnvironmentPasses(views: Passes);
        Environment.Register(name: EnvironmentInstance, residency: residency, view: 0);
        packages.Register(factory: Passes, package: RenderGraphPackageCatalog.SdfWorld);
        packages.Register(factory: Environment, package: RenderGraphPackageCatalog.SkyEnvironment);
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: [new RenderGraphInstance(
                ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Name: Instance,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: [new(Producer: EnvironmentInstance, Kind: ShaderPipelineResourceKind.Buffer)],
                Refresh: RenderGraphRefresh.EveryFrame
            ), new RenderGraphInstance(Name: EnvironmentInstance, ExternalPackage: RenderGraphPackageCatalog.SkyEnvironment,
                Passes: SdfSkyEnvironmentGraph.Fragment.Passes.Count, Reads: [], Output: ShaderPipelineResourceKind.Buffer, Refresh: RenderGraphRefresh.EveryFrame)],
            refusal: out var setRefusal,
            set: out var set
        ), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(
            deviceContext: device,
            graphs: new RenderGraphRuntimeGraph?[2],
            hostsOnDirectX: hostsOnDirectX,
            packages: packages,
            pipelines: pipelines.Pipelines,
            refusal: out var refusal,
            root: Instance,
            runtime: out var runtime,
            set: set
        ), userMessage: refusal?.Message);

        m_extent = extent;
        Residency = residency;
        Runtime = runtime;
    }

    public ICaptureRequestTarget CaptureTarget => Runtime.CaptureTarget(instance: Instance);
    public SdfSkyEnvironmentPasses Environment { get; }
    public bool IsReady => (Residency.IsReady && (Runtime.UnservedCaptureReason is null));
    public string? NotReadyReason => (Residency.NotReadyReason ?? Runtime.UnservedCaptureReason);
    public SdfWorldPasses Passes { get; }
    public SdfWorldResidency Residency { get; }
    public RenderGraphRuntime Runtime { get; }

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        Runtime.Dispose();
        Environment.Dispose();
        Residency.Dispose();
    }
    // Produces one graph frame, returning whether the view presented an image.
    public bool Produce(in FrameContext context) {
        var frame = new RenderGraphFrame(
            DisplayHeight: ((int)m_extent),
            DisplayHertz: 60,
            DisplayWidth: ((int)m_extent),
            Footprints: [],
            Index: m_frame,
            Roots: [new RenderGraphRoot(Height: 1.0, Instance: Instance, Width: 1.0)],
            Tick: m_frame
        );

        m_frame++;

        return !Runtime.ProduceFrame(
            context: in context,
            frame: in frame
        ).IsEmpty;
    }
}
