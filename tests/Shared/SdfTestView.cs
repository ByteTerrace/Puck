using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Xunit;

namespace Puck.Testing;

// One sdf.world view rendered as the World renders its view of the world: a residency, the package's recorders resolving
// the one instance to the residency's first view, and a render graph whose root is that instance. A frame is one produced
// graph frame, and the view's readiness and captures are the World's: the residency's tables built and the root served.
internal sealed class SdfTestView : IDisposable {
    public const string Instance = "world";

    private readonly uint m_extent;

    private bool m_disposed;
    private long m_frame;

    public SdfTestView(SdfWorldResidency residency, SdfWorldPipelineCatalog pipelines, IGpuDeviceContext device, uint extent) {
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        packages.Register(
            factory: new SdfWorldPasses(resolve: _ => new SdfWorldView(
                Residency: residency,
                View: 0
            )),
            package: RenderGraphPackageCatalog.SdfWorld
        );
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: [new RenderGraphInstance(
                ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Name: Instance,
                Passes: SdfWorldPackage.Fragment.Passes.Count,
                Reads: [],
                Refresh: RenderGraphRefresh.EveryFrame
            )],
            refusal: out var setRefusal,
            set: out var set
        ), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(
            deviceContext: device,
            graphs: new RenderGraphRuntimeGraph?[1],
            hostsOnDirectX: false,
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
    public bool IsReady => (Residency.IsReady && (Runtime.UnservedCaptureReason is null));
    public string? NotReadyReason => (Residency.NotReadyReason ?? Runtime.UnservedCaptureReason);
    public SdfWorldResidency Residency { get; }
    public RenderGraphRuntime Runtime { get; }

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        Runtime.Dispose();
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
