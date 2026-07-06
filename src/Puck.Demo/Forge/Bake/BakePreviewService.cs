using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Demo.Creator;
using Puck.Hosting;

namespace Puck.Demo.Forge.Bake;

/// <summary>
/// The LIVE in-editor bake preview — the arc's headline: while the player sculpts, this service watches
/// <see cref="CreatorScene.Revision"/>, and once the scene sits still for a frame-count debounce it snapshots the
/// document, rasterizes ONE view per produced frame on the render thread (spreading the GPU cost), quantizes on a
/// worker task, and publishes the composed preview through <see cref="IGpuSurfaceUpload"/> so the workbench easel's
/// diegetic screen shows the ACTUAL baked pixels. Failures degrade to the last image — nothing here may ever throw
/// into the render loop.
/// </summary>
internal sealed class BakePreviewService : ICreatorBakePreview, IDisposable {
    // The debounce: the revision must hold still for this many PRODUCED FRAMES before a bake starts (frame-count,
    // never wall-clock — determinism is a feature even for presentation plumbing).
    private const int DebounceFrames = 12;

    private enum BakeState {
        Idle,
        Rasterizing,
        Quantizing,
    }

    private readonly CreatorScene m_scene;
    private readonly IServiceProvider m_services;
    private readonly BakeRasterizer m_rasterizer = new();
    private IGpuSurfaceUpload? m_upload;
    private DemoConsole? m_console;
    private bool m_consoleResolved;
    private BakeState m_state = BakeState.Idle;
    private int m_lastSeenRevision = -1;
    private int m_stableFrames;
    private int m_lastBakedRevision = -1;
    private int m_snapshotRevision;
    private int m_overlayMode;
    private string? m_styleNote;
    private BakePlan? m_plan;
    private List<RasterizedView> m_rasterized = [];
    private int m_viewIndex;
    private Task<BakeResult>? m_cpuTask;
    private string? m_lastErrorMessage;

    /// <summary>Initializes the service over the live creator scene.</summary>
    /// <param name="scene">The authored scene to watch and bake.</param>
    /// <param name="services">The application services (the GPU compute seam + the optional dev console).</param>
    public BakePreviewService(CreatorScene scene, IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(services);

        m_scene = scene;
        m_services = services;
    }

    /// <inheritdoc/>
    public nint CurrentImageViewHandle { get; private set; }

    /// <inheritdoc/>
    public Vector3 PreviewAverageColor { get; private set; } = Vector3.Zero;

    /// <summary>Advances the preview one produced frame (render thread). Never throws — a failed bake narrates
    /// once and keeps the last image.</summary>
    /// <param name="context">The frame context (its host resolves the live GPU device).</param>
    public void Tick(in FrameContext context) {
        try {
            TickCore(context: in context);
        } catch (Exception exception) {
            // Degrade, keep the last image, and skip this revision so a deterministic failure never hot-loops.
            m_state = BakeState.Idle;
            m_cpuTask = null;
            m_lastBakedRevision = m_snapshotRevision;
            NarrateError(message: exception.Message);
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_rasterizer.Dispose();
        m_upload?.Dispose();
        m_upload = null;
    }

    private void TickCore(in FrameContext context) {
        var revision = m_scene.Revision;

        if (revision != m_lastSeenRevision) {
            m_lastSeenRevision = revision;
            m_stableFrames = 0;
        } else {
            m_stableFrames++;
        }

        switch (m_state) {
            case BakeState.Idle:
                // A new bake starts only while the mode is up, the scene has been still long enough, and there is
                // actually something new to bake.
                if (m_scene.Active && (m_stableFrames >= DebounceFrames) && (revision != m_lastBakedRevision)) {
                    StartBake(revision: revision);
                }

                break;
            case BakeState.Rasterizing:
                StepRasterize(context: in context);

                break;
            case BakeState.Quantizing:
                TryFinish(context: in context);

                break;
            default:
                break;
        }
    }

    private void StartBake(int revision) {
        var document = m_scene.ToDocument();
        var style = BakeStyles.Resolve(diagnostic: out var note, name: document.BakeStyle);
        var target = (string.Equals(a: m_scene.BakeTargetName, b: "dmg", comparisonType: StringComparison.OrdinalIgnoreCase)
            ? BakeTarget.Dmg
            : BakeTarget.Cgb);

        m_plan = CreationBakePlanner.Plan(document: document, style: style, target: target);
        m_styleNote = note;
        m_overlayMode = m_scene.BakeOverlay;
        m_snapshotRevision = revision;
        m_rasterized = new List<RasterizedView>(capacity: m_plan.Views.Count);
        m_viewIndex = 0;
        m_state = BakeState.Rasterizing;
    }

    // One view per produced frame; a mid-bake edit abandons the pass (the debounce restarts it from the new state).
    private void StepRasterize(in FrameContext context) {
        if (m_scene.Revision != m_snapshotRevision) {
            m_state = BakeState.Idle;

            return;
        }

        if (!TryResolveGpu(context: in context, device: out var device, gpu: out var gpu)) {
            return;
        }

        m_rasterized.Add(item: m_rasterizer.Rasterize(device: device, gpu: gpu, plan: m_plan!, viewIndex: m_viewIndex));
        m_viewIndex++;

        if (m_viewIndex >= m_plan!.Views.Count) {
            var plan = m_plan;
            var views = m_rasterized;
            var overlay = m_overlayMode;
            string[]? extra = ((m_styleNote is { } styleNote) ? [styleNote] : null);

            // The CPU half is pure — no GPU, no scene reads — so the worker task races nothing.
            m_cpuTask = Task.Run(function: () => BakePipeline.RunCpu(extraWarnings: extra, overlayMode: overlay, plan: plan, views: views));
            m_state = BakeState.Quantizing;
        }
    }

    private void TryFinish(in FrameContext context) {
        if (m_cpuTask is not { IsCompleted: true } task) {
            return;
        }

        if (task.IsFaulted) {
            m_cpuTask = null;
            m_state = BakeState.Idle;
            m_lastBakedRevision = m_snapshotRevision;
            NarrateError(message: (task.Exception.InnerException?.Message ?? task.Exception.Message));

            return;
        }

        if (!TryResolveGpu(context: in context, device: out var device, gpu: out var gpu)) {
            return; // The device comes back next frame; the completed result just waits.
        }

        m_cpuTask = null;
        m_state = BakeState.Idle;
        m_lastBakedRevision = m_snapshotRevision;
        Publish(device: device, gpu: gpu, result: task.Result);
    }

    private void Publish(IGpuDeviceContext device, IGpuComputeServices gpu, BakeResult result) {
        m_upload ??= gpu.SurfaceTransferFactory.CreateUpload(deviceContext: device);
        // The returned handle is only valid until the NEXT Upload on this object — re-stored on every publish, and
        // the easel's provider re-reads it every frame, exactly the contract IGpuSurfaceUpload documents.
        CurrentImageViewHandle = m_upload.Upload(
            deviceContext: device,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: (uint)result.PreviewHeight,
            pixels: result.PreviewRgba,
            width: (uint)result.PreviewWidth
        );
        PreviewAverageColor = result.AverageColor;
        Narrate(line: result.Diagnostics.Summarize(target: m_plan!.Target));
    }

    private bool TryResolveGpu(in FrameContext context, out IGpuDeviceContext device, out IGpuComputeServices gpu) {
        gpu = ((m_services.GetService(serviceType: typeof(IGpuComputeServices)) as IGpuComputeServices)!);

        return (context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out device) && (gpu is not null));
    }

    // ONE diagnostics line per publish: the on-screen dev console when it is reachable (it echoes to the terminal
    // itself), else plain stderr.
    private void Narrate(string line) {
        if (!m_consoleResolved) {
            m_console = (m_services.GetService(serviceType: typeof(DemoConsole)) as DemoConsole);
            m_consoleResolved = true;
        }

        if (m_console is { } console) {
            console.WriteLine(message: line);
        } else {
            Console.Error.WriteLine(value: line);
        }
    }

    // A failure narrates ONCE per distinct message — a broken scene must not spam every debounce window.
    private void NarrateError(string message) {
        if (string.Equals(a: message, b: m_lastErrorMessage, comparisonType: StringComparison.Ordinal)) {
            return;
        }

        m_lastErrorMessage = message;
        Narrate(line: $"[bake-preview] bake failed (keeping the last image): {message}");
    }
}
