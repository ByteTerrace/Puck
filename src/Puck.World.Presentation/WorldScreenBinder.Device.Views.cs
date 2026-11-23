using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // A session screen's residency, rendering the session feed's frame source on its own clock, its destination's screens
    // showing what the feed's level shows.
    private ViewResidency CreateSessionResidency(SessionFeed feed, string name, SdfCompositionFrameSource source, WorldScreenResolution resolution) {
        var residency = new SdfWorldResidency(
            screenSources: new FeedScreenSources(
                feed: feed
            ),
            dynamicTransformCapacity: source.DynamicTransformCapacity,
            frameSource: new WorldSessionFrameSource(
                captureHostFirst: CaptureHostFirst,
                inner: source,
                resolution: resolution,
                resolveResolution: (view, width, height) => ResolveResolution(height: height, name: name, view: view, width: width)
            ),
            height: ((uint)resolution.Height),
            instanceCapacity: source.InstanceCapacity,
            kernels: ViewKernels(),
            name: name,
            meshLibrary: MeshLibrary,
            pipelines: m_viewPipelines!,
            programWordCapacity: source.ProgramWordCapacity,
            width: ((uint)resolution.Width)
        );

        RegisterViewWork(
            lifetime: residency.WorkLifetime,
            name: name,
            transforms: source.MovedTransforms,
            work: residency.Work
        );
        Console.Error.WriteLine(value: $"[world.view: {name} renders a residency of its own of '{feed.InstanceName}': no proof lets it share another stream's scene]");

        return new ViewResidency(
            Residency: residency,
            Source: source
        );
    }
    // The kernels a residency made now starts from: the set a kernel reload installed in the world's own residency, so a
    // scene first shown after the reload draws with the reloaded kernels, else the deployed set.
    private SdfKernelSet ViewKernels() => ((ViewHost is { ShaderBatchesInstalled: > 0 } host)
        ? host.Kernels
        : (m_viewKernels ??= m_viewPipelines!.LoadDeployed(target: ShaderTargets.Of(hostsOnDirectX: m_viewHostsOnDirectX))));
    /// <summary>Requests a kernel reload (<see cref="SdfWorldResidency.RequestShaderReload"/>) on every residency the
    /// presentation renders through: the world's own (<see cref="ViewHost"/>), each view and session residency, each
    /// routed scene's and each infinity layer's, so an edited kernel reaches whichever world a view shows. Each residency
    /// swaps only its own pipeline leases, and a carried source compiles once into the shared shader cache, so the
    /// residencies after the first read it as a hit. A residency made later starts from the reloaded set.</summary>
    /// <param name="compiler">The compiler a carried kernel source compiles with.</param>
    /// <param name="tree">The kernel tree to read, or <see langword="null"/> for the deployed tree.</param>
    /// <returns>The residencies whose request was queued; a residency with a request still pending keeps it.</returns>
    /// <exception cref="ArgumentException">The directory path is invalid.</exception>
    public int RequestShaderReload(ShaderCompiler compiler, string? tree) {
        var queued = 0;

        foreach (var residency in Residencies()) {
            if (residency.RequestShaderReload(compiler: compiler, tree: tree)) { queued++; }
        }
        return queued;
    }
    // Every residency rendering now, the world's own first.
    private IEnumerable<SdfWorldResidency> Residencies() {
        if (ViewHost is { } host) { yield return host; }
        foreach (var view in m_viewResidencies.Values) { yield return view.Residency; }
        foreach (var routed in m_routedResidencies.Values) { yield return routed; }
        foreach (var entry in InfinityEntries()) {
            if (entry.Residency is { } infinity) { yield return infinity; }
        }
    }
    // The view a session the presentation routed renders: a session of the boot world as a view of the World's own
    // residency, filmed after its seats' views, or one joined to its destination's endpoint scene there, from the one
    // residency that scene's seats and windows share; null while that residency is absent.
    private SdfWorldView? SharedSessionView(SessionFeed feed, string name) {
        if (feed.Home) {
            return ((ViewHost is { } world)
                ? new SdfWorldView(
                    Residency: world,
                    View: m_homeViewIndices.GetValueOrDefault(key: name)
                )
                : null);
        }

        return (TryResolveWindowView(
            view: out var view,
            window: feed.Route.Window!
        )
            ? view
            : null);
    }
    // Hands an armed crossing capture the frame the renderer last completed.
    private void PresentCrossing() => CrossingCapture?.Present(previousFrame: (Runtime?.Render ?? FrameRender.Waiting(reason: "the renderer is not built")));
}
