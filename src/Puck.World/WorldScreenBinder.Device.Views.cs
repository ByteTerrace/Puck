using Puck.Hosting;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // A session screen's residency, rendering the session feed's frame source on its own clock, its destination's screens
    // showing what the feed's level shows.
    private ViewResidency CreateSessionResidency(SessionFeed feed, string name, SdfCompositionFrameSource source, WorldScreenResolution resolution) {
        var residency = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            screenSources: new FeedScreenSources(
                feed: feed
            ),
            dynamicTransformCapacity: source.WorstCaseDynamicTransformCapacity,
            frameSource: new WorldSessionFrameSource(
                captureHostFirst: CaptureHostFirst,
                inner: source,
                resolution: resolution,
                resolveResolution: (view, width, height) => ResolveResolution(height: height, name: name, view: view, width: width)
            ),
            height: ((uint)resolution.Height),
            instanceCapacity: source.WorstCaseInstanceCapacity,
            kernels: ViewKernels(),
            name: name,
            meshLibrary: MeshLibrary,
            pipelines: m_viewPipelines!,
            programWordCapacity: source.WorstCaseProgramWordCapacity,
            width: ((uint)resolution.Width)
        );

        RegisterViewWork(
            lifetime: residency.WorkLifetime,
            name: name,
            transforms: source.MovedTransforms,
            work: residency.Work
        );
        Console.Error.WriteLine(value: $"[world.view: {name} renders a residency of its own of '{feed.InstanceName}': its session does not disclose everything that world holds]");

        return new ViewResidency(
            Residency: residency,
            Source: source
        );
    }
    private SdfKernelSet ViewKernels() =>
        (m_viewKernels ??= m_viewPipelines!.LoadDeployed(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: m_viewHostsOnDirectX)));
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
    private void PresentCrossing() => CrossingCapture?.Present(previousFrame: (Runtime?.Render ?? FrameRender.Waiting(reason: "the renderer is not built")));}
