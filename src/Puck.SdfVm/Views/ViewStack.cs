using System.Diagnostics;
using System.Numerics;
using System.Text;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;

namespace Puck.SdfVm.Views;

/// <summary>A stable handle for one <see cref="ViewStack"/> registration — returned by <see cref="ViewStack.Register"/>,
/// consumed by <see cref="ViewStack.Release(ViewId)"/>. <see cref="None"/> is the sentinel a dropped (ceiling-exceeding)
/// registration returns; every other caller resolves content by name (<see cref="ViewStack.Resolve"/>), so holding
/// onto the id itself is only needed to release a specific registration later.</summary>
/// <param name="Value">The raw id, unique for the lifetime of the owning <see cref="ViewStack"/> (never reused).</param>
public readonly record struct ViewId(int Value) {
    /// <summary>The sentinel a failed/dropped registration returns.</summary>
    public static readonly ViewId None = new(Value: -1);

    /// <summary>Whether this id names a real registration (not <see cref="None"/>).</summary>
    public bool IsValid => (Value >= 0);
}
/// <summary>One piece of view content — anything that can produce a samplable image handle for a screen surface (or
/// any other consumer) to show: a posed offscreen world render, a hosted guest's raw framebuffer, a nested world's
/// own offscreen composite. The shared vocabulary <see cref="ViewStack"/> registers, budgets, and resolves by name;
/// see <see cref="Views.SdfCameraView"/>/<see cref="Views.GuestSurfaceView"/>/<see cref="Views.NestedWorldView"/> for
/// the three shapes built against it.</summary>
public interface IViewContent {
    /// <summary>Drops device-owned state after device loss. The next <see cref="Resolve"/> rebuilds it against the
    /// replacement device. Default no-op for content that only forwards an externally owned handle.</summary>
    void NotifyDeviceLost() { }
    /// <summary>Produces this frame's image handle, or 0 for "no signal" (the caller's fallback — a flat/procedural
    /// material — is its own call, never this content's). Called by <see cref="ViewStack.RenderFrame"/>, either every
    /// frame (<see cref="IsBudgeted"/> false) or round-robin against the stack's shared refresh budget
    /// (<see cref="IsBudgeted"/> true, the default).</summary>
    /// <param name="context">This frame's shared render inputs.</param>
    nint Resolve(in ViewRenderContext context);

    /// <summary>This content's room-light contribution — zero for content that only films an already-lit scene (a
    /// camera, a nested world), non-zero for content that is its own light source (a CPU-drawn face, a CRT terminal).</summary>
    Vector3 RoomGlow { get; }
    /// <summary>Whether this content counts against <see cref="OffscreenRenderBudget.PerProducedFrame"/>'s round-robin
    /// share. <see langword="true"/> (the default) for anything that pays a real render pass to resolve (an offscreen world
    /// engine submit) — <see cref="Views.SdfCameraView"/> and <see cref="Views.NestedWorldView"/> both do.
    /// <see langword="false"/> for content whose <see cref="Resolve"/> is a cheap read of state some other path
    /// already refreshed (<see cref="Views.GuestSurfaceView"/> — its producer delegate is already ticked/uploaded by
    /// its owner every frame, so gating it behind the budget would only serve a stale cached handle on the frames it
    /// is skipped).</summary>
    bool IsBudgeted => true;
}
/// <summary>This frame's shared render inputs, handed to every <see cref="IViewContent.Resolve"/> call — the same
/// program/transforms/time the room itself rendered with, so a view content can pose a camera over the identical
/// world a screen surface would otherwise show head-on.</summary>
/// <remarks><see cref="HostFrame"/> is the primary member and the rest are conveniences beside it: an offscreen
/// content derives its own frame from the room's rather than building one, so every per-frame lever the host set
/// reaches it by construction. The convenience members are the ones a content needs before it has a frame to derive
/// (which device, which program revision, what to pose a rig against), never a second spelling of something the
/// frame already carries — duplicating the packed dynamic transforms here would let a content render the wrong
/// array whenever the two copies drift.</remarks>
/// <param name="Host">The host frame context (its <see cref="Puck.Hosting.FrameContext.Host"/> resolves the live GPU
/// device).</param>
/// <param name="HostFrame">The frame the room is rendering this frame — the base every offscreen content derives its
/// own submission from (see <see cref="Views.SdfCameraView.Resolve"/>). Carries the shared program, the packed dynamic
/// transforms, the content clock, and every per-frame quality/lighting lever.</param>
/// <param name="Program">This frame's composed world program (the same instance every content shares — an offscreen
/// engine re-uploads only when <paramref name="ProgramRevision"/> advances).</param>
/// <param name="ProgramRevision">The program's revision counter — content compares this against its own
/// last-uploaded revision to decide whether to re-upload.</param>
/// <param name="Time">The frame's content clock (seconds) — content renders the same animated world the room does.</param>
/// <param name="AuthoritativeTick">The latest authoritative simulation tick available to presentation.</param>
/// <param name="ResolveScreenSource">Resolves a program-declared screen-surface index to its bound image handle —
/// what a content's own render sees on every other screen surface (see <see cref="ViewStack"/>'s self-reference
/// remarks for why "other" matters).</param>
public readonly record struct ViewRenderContext(
    Puck.Hosting.FrameContext Host,
    SdfFrame HostFrame,
    SdfProgram Program,
    int ProgramRevision,
    float Time,
    ulong AuthoritativeTick,
    Func<int, nint> ResolveScreenSource
);
/// <summary>
/// Manages named, role-neutral view content for diegetic screens and other image consumers. A posed offscreen camera render
/// (<see cref="Views.SdfCameraView"/>), a hosted guest's raw framebuffer (<see cref="Views.GuestSurfaceView"/>), and a
/// fully nested world's own offscreen composite (<see cref="Views.NestedWorldView"/>) all register, budget, and
/// resolve through this one small vocabulary — no consumer-specific channel exists.
/// <para>
/// Registering a view is cheap: up to
/// <see cref="OffscreenRenderBudget.RegisteredViews"/> may be live at once — but only <see cref="OffscreenRenderBudget.PerProducedFrame"/>
/// of the budgeted ones (<see cref="IViewContent.IsBudgeted"/> true — an offscreen render pays a real GPU pass)
/// actually re-render on any one produced frame; the rest keep their last resolved handle until the round-robin
/// cursor reaches them again.
/// Unbudgeted content (a cheap producer some other path already refreshed) resolves every frame regardless — see
/// <see cref="IViewContent.IsBudgeted"/>'s remarks. So a wall of monitors costs the same per-frame render as four:
/// registration is cheap, only the refresh budget is spent each frame. Beyond the budget, views share it
/// round-robin (narrated, never dropped); only past the <see cref="OffscreenRenderBudget.RegisteredViews"/> ceiling is a new
/// registration truly refused (narrated).
/// </para>
/// <para>
/// A registered budgeted view has no intrinsic "is anybody watching" gate — <see cref="RenderFrame"/> cannot see every
/// legitimate consumer, since content is resolved by name. <see cref="Resolve"/>/<see cref="ResolveGlow"/> are valid,
/// screen-free reads in their own right (a <c>ViewTransition</c> sampling a camera view by name for its own layout,
/// never wiring it to a screen surface, is exactly this shape) — an intrinsic "wired to some screen" gate would starve
/// that consumer silently, so that design is rejected. The gate is instead consumer-supplied: <see cref="Register"/>
/// takes an optional <c>isLive</c> predicate — only the registrant knows its own full consumer set. When the predicate
/// returns <see langword="false"/>, the round-robin cursor passes over that view without spending refresh budget on it
/// (the budget goes to the next live view instead), and the view keeps serving its last resolved
/// <see cref="Resolve"/>/<see cref="ResolveGlow"/> image — never black, never zero. A registrant that instead wants a
/// budgeted view to stop costing a render pass entirely (and release any GPU resource its content owns) still
/// <see cref="Release(ViewId)"/>/<see cref="Release(string)"/>s it itself and re-<see cref="Register"/>s (fresh
/// content) when it becomes wanted again; the predicate is additive to that pattern, not a replacement for it.
/// </para>
/// <para>
/// Inside a view's own render, any screen surface currently wired to that view
/// binds 0 (the flat/procedural fallback) — a view never samples the image it is itself writing, which would compound
/// every frame. Every other screen (including one wired to a different view) resolves normally, so one-frame-lag
/// TV-in-TV chains (a view showing a screen that shows a different view) are legal and desirable. A host records,
/// per view name, which screen indices are wired to that view via <see cref="SetWiredScreens"/>; <see cref="RenderFrame"/>
/// wraps the caller's <see cref="ViewRenderContext.ResolveScreenSource"/> per view so those indices always resolve to
/// 0 for that view's own render.
/// </para>
/// </summary>
public sealed class ViewStack : IDisposable {

    private static readonly IReadOnlySet<int> EmptyScreenSet = new HashSet<int>();

    private bool m_disposed;
    private int m_nextId;
    private int m_refreshCursor;
    // [view-timing] (armed GPU timing): this stack's own produced-frame counter, gating the throttled digest — separate
    // from any host's produced-frame count so a ViewStack used standalone (a test, a future non-overworld host) still
    // reports on its own cadence.
    private ulong m_timingFrame;

    private sealed class Entry {
        public ScreenSlotPriority Band;
        public required IViewContent Content;
        // True once this view's own Resolve has thrown — permanently skipped by RenderFrame thereafter, never
        // retried on a later produced frame, mirroring WorldOutputHub's own detach-on-fault contract for a typed
        // subscriber that throws mid-delivery. The REGISTRATION itself survives (ActiveViewCount, the
        // OffscreenRenderBudget.RegisteredViews ceiling, and Release/Resolve-by-name all still see it) — only the render pass stops
        // calling into content proven unsafe to call. Resolve/ResolveGlow keep serving whatever LastHandle/LastGlow
        // this entry last resolved successfully (0/zero if it never resolved at all), which is what "holds the last
        // completed image" means at this layer.
        public bool Disabled;
        public required ViewId Id;
        // Registrant-supplied liveness gate for the BUDGETED round-robin only (see the type remarks); null (the
        // default) means always live — today's unconditional round-robin behavior.
        public Func<bool>? IsLive;
        public Vector3 LastGlow;
        public nint LastHandle;
        // [view-timing] (armed GPU timing): this entry's most recent Resolve wall time — 0 until it has resolved at
        // least once, or always 0 when ViewTiming.Enabled is false.
        public long LastResolveTicks;
        public required string Name;
        public IReadOnlySet<int> WiredScreens = EmptyScreenSet;
    }

    private readonly Dictionary<string, Entry> m_byName = new(comparer: StringComparer.Ordinal);
    // Registration order (also the round-robin's base order) — a List so it can be re-sorted by band without
    // disturbing m_byName; reused across RenderFrame calls (Register/Release keep it in sync incrementally).
    private readonly List<Entry> m_order = [];
    // Scratch for RenderFrame's budgeted subset — reused (cleared, not reallocated) every call.
    private readonly List<Entry> m_budgetedScratch = [];
    private int m_lastNarratedShareCount = -1;

    /// <summary>How many views are registered and live right now (the stack's occupancy).</summary>
    public int ActiveViewCount => m_order.Count;

    // Narrates refresh-rate SHARING (never a drop) the frame the live budgeted-view count first exceeds the per-frame
    // budget, or when that count changes — hoisted verbatim from CameraFeedPool.NarrateRefreshSharing.
    private void NarrateRefreshSharing(int liveCount) {
        if (liveCount <= OffscreenRenderBudget.PerProducedFrame) {
            m_lastNarratedShareCount = -1;

            return;
        }

        if (liveCount == m_lastNarratedShareCount) {
            return;
        }

        var everyFrames = ((liveCount + (OffscreenRenderBudget.PerProducedFrame - 1)) / OffscreenRenderBudget.PerProducedFrame);

        Console.Error.WriteLine(value: $"[view-stack] {liveCount} view(s) live but the per-frame refresh budget is {OffscreenRenderBudget.PerProducedFrame} — sharing round-robin: each view refreshes every {everyFrames} frame(s), last image persists in between");
        m_lastNarratedShareCount = liveCount;
    }
    // Resolves one entry, scoping its own wired screens to 0 (the self-reference rule) — a wrapped delegate only when
    // that entry actually has wired screens this frame (the common case, no wire at all, pays no closure). The `in`
    // context is copied to a local before the closure (an `in` parameter cannot itself be captured).
    //
    // EXCEPTION ISOLATION: a throwing Resolve/RoomGlow is caught here, narrated by name (the view's registered
    // name AND its concrete content type, so the offending registrant is unambiguous), and the entry is permanently
    // Disabled — mirroring WorldOutputHub's own detach-on-fault contract (a broken observer never gets a second
    // chance to corrupt delivery to the healthy ones). LastHandle/LastGlow are left exactly as they were before this
    // call, which is what "holds the last completed image" means for content that has no narrower recovery of its
    // own; WorldSessionView's own upload/capacity catch (this wave's other half) is the narrower, self-healing case
    // that this backstop never needs to see.
    private static void RenderEntry(Entry entry, in ViewRenderContext context, bool timingEnabled) {
        var baseline = context;
        var scoped = ((entry.WiredScreens.Count == 0)
            ? baseline
            : (baseline with {
                ResolveScreenSource = (screenIndex => (entry.WiredScreens.Contains(item: screenIndex)
                ? 0
                : baseline.ResolveScreenSource(arg: screenIndex))),
            })
        );
        // [view-timing]: wall time of THIS view content's own Resolve — a camera/nested-world's offscreen submit, or a
        // guest surface's cheap state read. Reported per-view (not tiled against anything) since views resolve at
        // different cadences (unbudgeted every frame, budgeted round-robin).
        var start = (timingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );

        try {
            entry.LastHandle = entry.Content.Resolve(context: in scoped);
            entry.LastGlow = entry.Content.RoomGlow;
        } catch (Exception exception) {
            entry.Disabled = true;

            Console.Error.WriteLine(value: $"[view-stack] view '{entry.Name}' ({entry.Content.GetType().Name}) threw in Resolve — disabled (holding its last resolved image, handle={entry.LastHandle}): {exception}");

            return;
        }

        if (timingEnabled) {
            entry.LastResolveTicks = (Stopwatch.GetTimestamp() - start);
        }
    }
    // Throttled [view-timing] digest: one line naming every LIVE view's most recent resolve time (a round-robin view
    // not refreshed this call still shows its last real measurement, not a stale zero, since it persists in the Entry
    // until its next refresh — the same "last resolved" contract Resolve()/ResolveGlow() already expose).
    private void ReportViewTiming(bool timingEnabled) {
        if (!timingEnabled) {
            return;
        }

        m_timingFrame++;

        if (
            (m_timingFrame == 0UL) ||
            (0UL != (m_timingFrame % ViewTiming.ReportInterval))
        ) {
            return;
        }

        var builder = new StringBuilder(value: "[view-timing] stack");

        foreach (var entry in m_order) {
            _ = builder.Append(value: $" | {entry.Name} {ViewTiming.Milliseconds(ticks: entry.LastResolveTicks):0.000}ms");
        }

        Console.Error.WriteLine(value: builder.ToString());
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var entry in m_order) {
            (entry.Content as IDisposable)?.Dispose();
        }

        m_order.Clear();
        m_byName.Clear();
    }
    /// <summary>Whether named view <paramref name="name"/> is currently producing pixels (a non-zero last-resolved
    /// handle).</summary>
    /// <param name="name">The view name.</param>
    public bool IsLive(string name) =>
        (m_byName.TryGetValue(
            key: name,
            value: out var entry
        ) && (entry.LastHandle != 0));
    /// <summary>Propagates device loss to every registered content producer and clears cached handles from the lost
    /// device. Registration and round-robin state survive.</summary>
    public void NotifyDeviceLost() {
        foreach (var entry in m_order) {
            entry.Content.NotifyDeviceLost();
            entry.LastHandle = 0;
            entry.LastGlow = Vector3.Zero;
        }
    }
    /// <summary>Registers (or, for an existing name, updates) one named view. Calling this again for a name already
    /// held replaces its content/band in place and keeps its <see cref="ViewId"/> and any GPU resource that content
    /// instance itself owns — a caller that wants a persistent offscreen engine to survive across frames passes the
    /// same <see cref="IViewContent"/> instance back every frame (mutating its own pose/binding fields), rather than
    /// constructing a fresh one each call. A first-time registration past <see cref="OffscreenRenderBudget.RegisteredViews"/> is refused
    /// (narrated to stderr, like <c>CameraFeedPool</c>'s own ceiling) and returns <see cref="ViewId.None"/>.</summary>
    /// <param name="name">The view's stable name — the handle every consumer resolves by (see <see cref="Resolve"/>).</param>
    /// <param name="content">The content this name shows.</param>
    /// <param name="band">The view's priority band (today informational/ordering only — see the type remarks; a
    /// screen-surface slot claim is a separate arbitration, owned by the host).</param>
    /// <param name="isLive">Optional registrant-supplied liveness gate for the budgeted round-robin (see the type
    /// remarks) — <see langword="null"/> (the default) means always live, today's behavior. Consulted only while this
    /// view's content is budgeted (<see cref="IViewContent.IsBudgeted"/>); unbudgeted content already resolves every
    /// frame unconditionally. Returning <see langword="false"/> skips this view's turn without spending refresh
    /// budget on it and without touching its cached <see cref="Resolve"/>/<see cref="ResolveGlow"/> image.</param>
    /// <returns>The view's stable id, or <see cref="ViewId.None"/> when the ceiling refused a new registration.</returns>
    public ViewId Register(string name, IViewContent content, ScreenSlotPriority band, Func<bool>? isLive = null) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(content);

        if (m_byName.TryGetValue(
            key: name,
            value: out var existing
        )) {
            existing.Content = content;
            existing.Band = band;
            existing.IsLive = isLive;

            return existing.Id;
        }

        if (m_order.Count >= OffscreenRenderBudget.RegisteredViews) {
            Console.Error.WriteLine(value: $"[view-stack] view '{name}' not registered — the pool ceiling is {OffscreenRenderBudget.RegisteredViews} and every slot is taken");

            return ViewId.None;
        }

        var entry = new Entry { Band = band, Content = content, Id = new ViewId(Value: m_nextId++), IsLive = isLive, Name = name };

        m_byName[name] = entry;
        m_order.Add(item: entry);

        return entry.Id;
    }
    /// <summary>Withdraws a registration — disposes its content if <see cref="IDisposable"/>, frees its name for a
    /// future <see cref="Register"/>. A no-op for an id already released (or <see cref="ViewId.None"/>).</summary>
    /// <param name="id">The id <see cref="Register"/> returned.</param>
    public void Release(ViewId id) {
        if (!id.IsValid) {
            return;
        }

        for (var index = 0; (index < m_order.Count); index++) {
            if (m_order[index].Id != id) {
                continue;
            }

            var entry = m_order[index];

            m_order.RemoveAt(index: index);
            _ = m_byName.Remove(key: entry.Name);
            (entry.Content as IDisposable)?.Dispose();

            return;
        }
    }
    /// <summary>The by-name counterpart to <see cref="Release(ViewId)"/> — every other lookup on this stack
    /// (<see cref="Resolve"/>, <see cref="ResolveGlow"/>, <see cref="IsLive"/>, <see cref="SetWiredScreens"/>) is
    /// already by name, so a registrant that never stashed the <see cref="ViewId"/> <see cref="Register"/> returned
    /// (the common case for a lazily-registered, cached-by-name content instance) can withdraw without tracking one
    /// solely for this. A no-op for a name not currently registered.</summary>
    /// <param name="name">The view's name.</param>
    public void Release(string name) {
        if (m_byName.TryGetValue(
            key: name,
            value: out var entry
        )) {
            Release(id: entry.Id);
        }
    }
    /// <summary>Renders this frame's views: every unbudgeted view resolves unconditionally, then up to
    /// <see cref="OffscreenRenderBudget.PerProducedFrame"/> budgeted views resolve round-robin (see the type remarks)
    /// — a budgeted view whose <c>isLive</c> predicate (see <see cref="Register"/>) returns <see langword="false"/> is skipped for its turn
    /// without spending budget, and the cursor moves on to the next candidate. Each view's own render sees every
    /// other screen surface normally and its own wired screens as unbound (the self-reference rule).</summary>
    /// <param name="context">This frame's shared render inputs (see <see cref="ViewRenderContext"/>).</param>
    public void RenderFrame(in ViewRenderContext context) {
        if (m_order.Count == 0) {
            return;
        }

        m_budgetedScratch.Clear();

        var timingEnabled = ViewTiming.Enabled;

        foreach (var entry in m_order) {
            if (entry.Disabled) {
                continue;
            }

            if (entry.Content.IsBudgeted) {
                m_budgetedScratch.Add(item: entry);
            } else {
                RenderEntry(
                    context: in context,
                    entry: entry,
                    timingEnabled: timingEnabled
                );
            }
        }

        var budgetedCount = m_budgetedScratch.Count;

        if (budgetedCount == 0) {
            ReportViewTiming(timingEnabled: timingEnabled);

            return;
        }

        var refreshTarget = Math.Min(
            val1: budgetedCount,
            val2: OffscreenRenderBudget.PerProducedFrame
        );

        NarrateRefreshSharing(liveCount: budgetedCount);

        // Skipped (predicate-gated) views pass the cursor over WITHOUT spending refresh budget — `examined` bounds
        // the walk to one full cycle so an all-gated stack cannot spin forever, and `rendered` (not `examined`) is
        // what the budget counts against. The stored cursor is taken modulo THIS frame's budgetedCount before use:
        // a view disabled by a caught Resolve fault (or one simply Released) can shrink the budgeted population
        // between two RenderFrame calls, and an un-modulo'd stale cursor from a LARGER population would index
        // m_budgetedScratch out of range on the very first access — the exact kind of unhandled exception this
        // frame loop is meant to be safe from.
        var cursor = (m_refreshCursor % budgetedCount);
        var rendered = 0;
        var examined = 0;

        while (
            (rendered < refreshTarget) &&
            (examined < budgetedCount)
        ) {
            var entry = m_budgetedScratch[cursor];

            cursor = ((cursor + 1) % budgetedCount);
            examined++;

            if (!(entry.IsLive?.Invoke() ?? true)) {
                continue;
            }

            RenderEntry(
                context: in context,
                entry: entry,
                timingEnabled: timingEnabled
            );
            rendered++;
        }

        m_refreshCursor = cursor;

        ReportViewTiming(timingEnabled: timingEnabled);
    }
    /// <summary>The image-view handle named view <paramref name="name"/> resolved to as of the most recent
    /// <see cref="RenderFrame"/> (the frame it was last refreshed on — see the round-robin remarks), or 0 when no
    /// view carries that name or it has never resolved.</summary>
    /// <param name="name">The view name.</param>
    public nint Resolve(string name) =>
        (m_byName.TryGetValue(
            key: name,
            value: out var entry
        )
            ? entry.LastHandle
            : 0
        );
    /// <summary>The room-glow color named view <paramref name="name"/> reported as of its last resolve, or zero.</summary>
    /// <param name="name">The view name.</param>
    public Vector3 ResolveGlow(string name) =>
        (m_byName.TryGetValue(
            key: name,
            value: out var entry
        )
            ? entry.LastGlow
            : Vector3.Zero
        );
    /// <summary>Records which program-declared screen-surface indices are wired to view <paramref name="name"/> this
    /// frame — the self-reference set <see cref="RenderFrame"/> zeroes out of that view's own
    /// <see cref="ViewRenderContext.ResolveScreenSource"/> (see the type remarks). A host calls this once per frame,
    /// alongside (or instead of, when unchanged) re-registering — a no-op for a name not currently registered.</summary>
    /// <param name="name">The view's name.</param>
    /// <param name="screenIndices">The screen indices wired to this view this frame (an empty/null set clears it).</param>
    public void SetWiredScreens(string name, IReadOnlySet<int>? screenIndices) {
        ArgumentNullException.ThrowIfNull(name);

        if (m_byName.TryGetValue(
            key: name,
            value: out var entry
        )) {
            entry.WiredScreens = (screenIndices ?? EmptyScreenSet);
        }
    }
}
