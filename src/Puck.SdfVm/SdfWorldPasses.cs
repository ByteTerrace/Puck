using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The residency and the view of its frame one <c>sdf.world</c> instance renders.</summary>
/// <param name="Residency">The residency whose tables the instance's passes read.</param>
/// <param name="View">The view's index in the residency's frames (<see cref="SdfFrame.Views"/>).</param>
public readonly record struct SdfWorldView(SdfWorldResidency Residency, int View);
/// <summary>
/// The <c>sdf.world</c> package's recorders: each instance of the package is a view of a residency's frame, run as the
/// package's fragment (<see cref="SdfWorldPackage.Fragment"/>) — the sky, the instance masks, the beam, the cull
/// arguments, the mesh pass, primary traversal, surface and ambient resolution and shading into the view's color — every
/// pass recorded into its instance's submission with the barriers the planner planned, over scratch the instance
/// allocates once, counted by the residency (<see cref="SdfWorldResidency.CountsAt"/>). The host names the residency and
/// view each instance renders (<c>resolve</c>), and the package keeps every residency it has resolved or a recorder
/// holds: it starts their frames, asks them whether a view is unchanged, and tells them of a device loss.
/// <para>
/// A pass installs only once its residency's tables exist (its build waits for them), and the frame's first pass to
/// record submits the residency's upload ahead of the instance's submission. The screens a view shows are its
/// instance's reads its graph binds to no version (<see cref="SamplesReads"/>): each pass binds the image of every
/// screen, the first taking its lease into the frame's lease list.
/// </para>
/// </summary>
public sealed class SdfWorldPasses : IRenderGraphPackageFactory {
    private readonly Func<string, SdfWorldView?> m_resolve;

    // Each instance the runtime asked about: the view it renders, resolved on the frame thread at most once a frame, and
    // its counter, whose revision moves when its passes cannot follow the resolved view. An entry stays while the
    // package lives, keeping its revision's count of switches even while the runtime does not ask about the instance.
    private readonly Dictionary<string, Entry> m_entries = new(comparer: StringComparer.Ordinal);
    // Guards the entries, which a pass's build reads on the thread pool.
    private readonly Lock m_gate = new();
    // Every residency an entry resolves or a recorder holds, with its holds.
    private readonly Dictionary<SdfWorldResidency, int> m_residencies = new(comparer: ReferenceEqualityComparer.Instance);
    // Each graph's viewport-row region, by the frame block its passes share: the sky part's, which every later part of the
    // same graph binds.
    // The frame the package started last, which each residency's frame is started for once.
    private long m_frame = 1;

    // The context the package's frame was started with, which a residency an instance first resolves this frame is
    // prepared with before the instance decides whether its passes follow it in place.
    private FrameContext m_context;

    /// <summary>Initializes a new instance of the <see cref="SdfWorldPasses"/> class.</summary>
    /// <param name="resolve">Returns the view an instance renders, or <see langword="null"/> when the host renders no view
    /// of that name. It runs on the frame thread.</param>
    /// <param name="host">The residency whose frame the host's other residencies film, or <see langword="null"/>: the
    /// package holds it for its lifetime, so its frame is started and prepared every frame whether or not any instance
    /// renders a view of it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolve"/> is <see langword="null"/>.</exception>
    public SdfWorldPasses(Func<string, SdfWorldView?> resolve, SdfWorldResidency? host = null) {
        ArgumentNullException.ThrowIfNull(argument: resolve);

        m_resolve = resolve;

        if (host is not null) {
            Hold(residency: host);
        }
    }

    /// <inheritdoc/>
    /// <remarks>A view's screens sample them.</remarks>
    public bool SamplesReads => true;

    /// <inheritdoc/>
    /// <remarks>Waits, on the thread pool, until the instance's residency has built its tables, and holds the residency
    /// for the recorder.</remarks>
    public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: context);

        Entry? entry;

        lock (m_gate) {
            _ = m_entries.TryGetValue(
                key: context.Instance,
                value: out entry
            );
        }

        if (entry?.View is not { } view) {
            throw new InvalidOperationException(message: $"Instance '{context.Instance}' renders no SDF view its host resolves.");
        }

        view.Residency.Retain();

        try {
            view.Residency.WaitReady(cancellationToken: cancellationToken);
        } catch {
            view.Residency.Release();

            throw;
        }

        return new Built(view: view);
    }
    /// <inheritdoc/>
    public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: groups);

        var view = ((built as Built) ?? throw new ArgumentException(message: "An sdf.world pass is created from its own build.", paramName: nameof(built))).Take();

        Hold(residency: view.Residency);

        try {
            return new SdfWorldPassRecorder(
                context: context,
                groups: groups,
                owner: this,
                view: view
            );
        } catch {
            Unhold(residency: view.Residency);
            view.Residency.Release();

            throw;
        }
    }
    /// <inheritdoc/>
    public IShaderPipelineStorageCounter? CounterOf(string instance) => Refresh(instance: instance);
    /// <inheritdoc/>
    public bool IsUnchanged(string instance, in FrameContext context) {
        var entry = Refresh(instance: instance);

        return (
            (entry.View is { } view) &&
            view.Residency.IsUnchanged(
                context: in context,
                view: view.View
            ) &&
            (entry.RenderedBindings == entry.Bindings)
        );
    }

    // A residency's signature may belong to another instance. This instance can stand only after its own passes
    // render the binding it currently resolves, including a different view index within the same residency.
    internal void MarkRendered(string instance, in SdfWorldView view) {
        if (
            m_entries.TryGetValue(
                key: instance,
                value: out var entry
            ) &&
            (entry.View == view)
        ) {
            entry.RenderedBindings = entry.Bindings;
        }
    }

    /// <summary>Returns whether an instance's passes have rendered the view it resolves: false from the frame it resolves
    /// another until its passes record that one, whether they follow it in place or rebuild against it.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns><see langword="true"/> when the instance's latest render is of the view it resolves.</returns>
    public bool HasRenderedResolvedView(string instance) {
        lock (m_gate) {
            return (
                m_entries.TryGetValue(
                    key: instance,
                    value: out var entry
                ) &&
                (entry.RenderedBindings == entry.Bindings)
            );
        }
    }

    // The view an instance resolved this frame, which its passes follow in place when they can
    // (SdfWorldPassRecorder.Follow).
    internal SdfWorldView? ViewOf(string instance) {
        lock (m_gate) {
            return (m_entries.TryGetValue(
                key: instance,
                value: out var entry
            )
                ? entry.View
                : null
            );
        }
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        foreach (var residency in m_residencies.Keys) {
            residency.OnDeviceLost();
        }
    }
    /// <inheritdoc/>
    /// <remarks>Gives back the holds of instances whose residency its host has released, and starts and prepares the frame
    /// of every residency the package still holds, so a residency builds its tables before any pass of its views is
    /// installed, and while a capture keeps the runtime from asking whether its view is unchanged.</remarks>
    public void BeginFrame(in FrameContext context) {
        m_context = context;

        foreach (var entry in m_entries.Values) {
            if (entry.Residency is { IsReleased: true } released) {
                Unhold(residency: released);
                entry.Residency = null;
            }
        }

        m_frame++;

        foreach (var residency in m_residencies.Keys) {
            if (!residency.IsReleased) {
                Begin(residency: residency);
            }
        }
        // A camera can capture its host while preparing, so every residency must start before any is prepared.
        foreach (var residency in m_residencies.Keys) {
            if (!residency.IsReleased) {
                _ = residency.Prepare(context: in context);
            }
        }

        // A capture bypasses cadence, and an installed graph need not ask for its counter. Resolve its view before
        // recording anyway, after the host's capture has latched the routes this frame presents.
        foreach (var instance in m_entries.Keys) {
            _ = Refresh(instance: instance);
        }
    }

    // Starts a residency's frame the first time the package meets it in this frame.
    internal void Begin(SdfWorldResidency residency) {
        if (residency.PackageFrame != m_frame) {
            residency.PackageFrame = m_frame;
            residency.BeginFrame();
        }
    }
    // Holds a residency the package tells of a device loss.
    internal void Hold(SdfWorldResidency residency) =>
        m_residencies[residency] = (m_residencies.GetValueOrDefault(key: residency) + 1);
    // Gives back a hold; the package forgets a residency no one holds.
    internal void Unhold(SdfWorldResidency residency) {
        if (!m_residencies.TryGetValue(
            key: residency,
            value: out var holds
        )) {
            return;
        }
        if (holds <= 1) {
            _ = m_residencies.Remove(key: residency);
        } else {
            m_residencies[residency] = (holds - 1);
        }
    }

    // Whether passes that record a view of one residency can record another in place, with no rebuild and so no frame
    // held: the same residency (another view index), or tables that exist and share every layout the passes were built
    // against, with the instance count their counted scratch is sized by.
    private static bool CanFollow(SdfWorldView from, SdfWorldView to) =>
        (
            !to.Residency.IsReleased &&
            (
                ReferenceEquals(
                    objA: from.Residency,
                    objB: to.Residency
                ) ||
                (
                    !from.Residency.IsReleased &&
                    (from.Residency.Tables is { } fromTables) &&
                    (to.Residency.Tables is { } toTables) &&
                    (from.Residency.CapacityRevision == to.Residency.CapacityRevision) &&
                    fromTables.SharesLayoutsWith(other: toTables)
                )
            )
        );
    // Resolves the view an instance renders this frame, on the frame thread, once a frame. A residency the instance meets
    // for the first time is prepared at once, so its tables exist when the instance decides whether its passes follow it
    // in place or rebuild against it.
    private Entry Refresh(string instance) {
        Entry? entry;

        lock (m_gate) {
            if (!m_entries.TryGetValue(
                key: instance,
                value: out entry
            )) {
                entry = new Entry();
                m_entries.Add(
                    key: instance,
                    value: entry
                );
            }
        }

        if (entry.Frame == m_frame) {
            return entry;
        }

        entry.Frame = m_frame;

        var view = m_resolve(arg: instance);

        if (view is { Residency.IsReleased: true }) {
            view = null;
        }

        if (!ReferenceEquals(
            objA: view?.Residency,
            objB: entry.Residency
        )) {
            if (entry.Residency is { } previous) {
                Unhold(residency: previous);
            }
            if (view is { } resolved) {
                Hold(residency: resolved.Residency);
            }

            entry.Residency = view?.Residency;
        }
        if (entry.View != view) {
            entry.Bindings++;
        }
        if (view is { } current) {
            Begin(residency: current.Residency);

            if (
                (entry.Followed is { } followed) &&
                !ReferenceEquals(
                    objA: followed.Residency,
                    objB: current.Residency
                )
            ) {
                _ = current.Residency.Prepare(context: in m_context);
            }
        }
        if (entry.Followed != view) {
            if (
                (entry.Followed is not { } from) ||
                (view is not { } to) ||
                !CanFollow(
                    from: from,
                    to: to
                )
            ) {
                entry.Switches++;
            }

            entry.Followed = view;
        }

        entry.View = view;

        return entry;
    }

    // What a pass's build hands its recorder: the view, whose residency the build holds until the recorder takes it.
    private sealed class Built(SdfWorldView view) : IDisposable {
        private bool m_taken;

        public void Dispose() {
            if (!m_taken) {
                m_taken = true;
                view.Residency.Release();
            }
        }
        public SdfWorldView Take() {
            m_taken = true;

            return view;
        }
    }
    // One instance: the view it renders this frame, and the counter its passes' scratch is sized by. The view is written
    // on the frame thread and read by a pass's build on the thread pool.
    private sealed class Entry : IShaderPipelineStorageCounter {
        private readonly Lock m_gate = new();

        private SdfWorldView? m_view;

        // The frame the entry was last resolved in.
        public long Frame { get; set; }
        // The residency last resolved.
        public SdfWorldResidency? Residency { get; set; }
        // The view the instance's passes follow, and how often a change of it could not be followed in place, which
        // moves the revision and so rebuilds the passes.
        public SdfWorldView? Followed { get; set; }
        public long Revision => ((Switches << 32) + (Residency?.CapacityRevision ?? 0L));
        public long Switches { get; set; }
        // How often the resolved view changed at all, and the binding the instance last rendered.
        public long Bindings { get; set; }

        public long RenderedBindings { get; set; } = -1;

        public SdfWorldView? View {
            get {
                lock (m_gate) {
                    return m_view;
                }
            }
            set {
                lock (m_gate) {
                    m_view = value;
                }
            }
        }

        public ShaderPipelineStorageCounts CountsAt(uint width, uint height) => (Residency?.CountsAt(
            height: height,
            width: width
        ) ?? new ShaderPipelineStorageCounts(
            Height: height,
            Width: width
        ));
    }
}
