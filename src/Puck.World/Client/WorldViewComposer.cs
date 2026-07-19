using Puck.Compositing;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>One resolved window slot this frame — a normalized rect plus its occupant. A <see cref="Camera"/> of
/// <see langword="null"/> shows the seat at <see cref="SeatOrder"/> (its position among the joined seats); a named camera
/// renders that authored view into the rect.</summary>
/// <param name="Region">The eased normalized rect.</param>
/// <param name="SeatOrder">The 0-based seat position for a seat slot, or -1 for a camera slot.</param>
/// <param name="Camera">The authored camera name for a camera slot, or <see langword="null"/> for a seat slot.</param>
internal readonly record struct WorldComposedSlot(NormalizedRect Region, int SeatOrder, string? Camera);

/// <summary>
/// Owns layout SELECTION and TRANSITION for the main window — the data-side replacement for the compiled layout switch.
/// Given the session shape and the authored <see cref="WorldViewDefaults"/>, it selects one layout (the live override,
/// else the authored layout matching the joined-seat count, else the seat-count-0 catch-all, else the built-in ladder)
/// and eases between compositions with a <see cref="ViewTransition"/> whenever the selection changes — regions move
/// continuously, occupants cut at the midpoint, and the render scale dips mid-ease then sharpens on settle.
/// </summary>
internal sealed class WorldViewComposer {
    // The built-in ladder's own transition envelope (ScreenLayoutDirector's compiled 0.6 s / 0.5 scale), used only when
    // easing into or out of the built-in composition — an authored layout brings its own.
    private const float BuiltinTransitionSeconds = 0.6f;
    private const float BuiltinTransitionRenderScale = 0.5f;

    private const string BuiltinName = "builtin";

    private readonly List<WorldComposedSlot> m_slots = new();
    private readonly List<WorldComposedSlot> m_targetSlots = new();
    private readonly List<ViewBinding> m_currentBindings = new();
    private readonly List<ViewBinding> m_toScratch = new();
    private readonly Dictionary<string, int> m_cameraIds = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_cameraNames = new();

    private ViewTransition? m_transition;
    private float m_transitionStart;
    private float m_transitionSeconds;
    private float m_transitionRenderScale;
    private string m_activeName = "";

    /// <summary>The selected layout's name — an authored layout name, or <c>builtin</c>.</summary>
    public string ActiveLayoutName { get; private set; } = BuiltinName;

    /// <summary>Why the active layout was selected — <c>override</c>, <c>authored</c>, or <c>builtin</c>.</summary>
    public string SelectionReason { get; private set; } = BuiltinName;

    /// <summary>The transition progress fraction [0, 1] — 1 when settled.</summary>
    public float TransitionProgress { get; private set; } = 1f;

    /// <summary>The render-scale multiplier this frame — <c>1</c> when settled, the active layout's
    /// <c>TransitionRenderScale</c> mid-ease.</summary>
    public float CurrentRenderScale { get; private set; } = 1f;

    /// <summary>This frame's resolved slots (a reused buffer, valid until the next <see cref="Compose"/>).</summary>
    public IReadOnlyList<WorldComposedSlot> Slots => m_slots;

    /// <summary>Composes the window for this frame.</summary>
    /// <param name="joinedCount">The joined local-seat count.</param>
    /// <param name="soleEditorIndex">The sole editing seat's view index (>= 0 when exactly one of 2+ seats edits), else -1.</param>
    /// <param name="workbenchFraction">The sole-editor workbench width fraction.</param>
    /// <param name="views">The resolved view defaults (authored or built-in).</param>
    /// <param name="layoutOverride">The live active-layout override, or <see langword="null"/> for auto selection.</param>
    /// <param name="cameraOverride">The live camera override for every camera-bearing slot, or <see langword="null"/>.</param>
    /// <param name="elapsedSeconds">The presentation clock (drives the ease).</param>
    public void Compose(int joinedCount, int soleEditorIndex, float workbenchFraction, WorldViewDefaults views,
        string? layoutOverride, string? cameraOverride, float elapsedSeconds) {
        var (name, reason, seconds, renderScale) = Select(joinedCount: joinedCount, soleEditorIndex: soleEditorIndex, workbenchFraction: workbenchFraction, views: views, layoutOverride: layoutOverride);

        ApplyCameraOverride(cameraOverride: cameraOverride);
        BuildBindings(slots: m_targetSlots, into: m_toScratch);

        if (m_activeName.Length == 0) {
            // First compose: settle immediately, no transition (the frozen boot path).
            CopyBindings(source: m_toScratch, into: m_currentBindings);
            m_transition = null;
            m_activeName = name;
        } else if (!string.Equals(a: name, b: m_activeName, comparisonType: StringComparison.Ordinal)) {
            // The selection changed: ease from the current (possibly mid-transition) bindings toward the new target.
            m_transition = new ViewTransition(
                from: new ViewLayout(Bindings: m_currentBindings.ToArray()),
                to: new ViewLayout(Bindings: m_toScratch.ToArray()),
                durationSeconds: seconds,
                easing: static t => (t * t * (3f - (2f * t)))
            );
            m_transitionStart = elapsedSeconds;
            m_transitionSeconds = MathF.Max(x: seconds, y: 0.001f);
            m_transitionRenderScale = renderScale;
            m_activeName = name;
        }

        ActiveLayoutName = name;
        SelectionReason = reason;

        if (m_transition is { } transition) {
            var eased = transition.Sample(elapsedSeconds: (elapsedSeconds - m_transitionStart), complete: out var complete);

            CopyBindings(source: eased.Bindings, into: m_currentBindings);
            TransitionProgress = Math.Clamp(value: ((elapsedSeconds - m_transitionStart) / m_transitionSeconds), min: 0f, max: 1f);
            CurrentRenderScale = m_transitionRenderScale;

            if (complete) {
                CopyBindings(source: m_toScratch, into: m_currentBindings);
                m_transition = null;
                TransitionProgress = 1f;
                CurrentRenderScale = 1f;
            }
        } else {
            CopyBindings(source: m_toScratch, into: m_currentBindings);
            TransitionProgress = 1f;
            CurrentRenderScale = 1f;
        }

        DecodeSlots();
    }

    // Selects the target layout, filling m_targetSlots with its resolved (rect, occupant) slots.
    private (string Name, string Reason, float Seconds, float RenderScale) Select(int joinedCount, int soleEditorIndex, float workbenchFraction, WorldViewDefaults views, string? layoutOverride) {
        var layouts = (views.Layouts ?? []);

        if ((layoutOverride is { } wanted) && (FindLayout(layouts: layouts, name: wanted) is { } overridden)) {
            ResolveLayoutSlots(layout: overridden);

            return (overridden.Name, "override", overridden.TransitionSeconds, overridden.TransitionRenderScale);
        }

        if (FindBySeatCount(layouts: layouts, seatCount: joinedCount) is { } sized) {
            ResolveLayoutSlots(layout: sized);

            return (sized.Name, "authored", sized.TransitionSeconds, sized.TransitionRenderScale);
        }

        if (FindBySeatCount(layouts: layouts, seatCount: 0) is { } catchall) {
            ResolveLayoutSlots(layout: catchall);

            return (catchall.Name, "authored", catchall.TransitionSeconds, catchall.TransitionRenderScale);
        }

        ResolveBuiltin(joinedCount: joinedCount, soleEditorIndex: soleEditorIndex, workbenchFraction: workbenchFraction);

        return (BuiltinName, BuiltinName, BuiltinTransitionSeconds, BuiltinTransitionRenderScale);
    }

    private void ResolveLayoutSlots(WorldViewLayout layout) {
        m_targetSlots.Clear();

        var seatOrder = 0;

        foreach (var slot in (layout.Slots ?? [])) {
            var region = new NormalizedRect(X: slot.X, Y: slot.Y, Width: slot.Width, Height: slot.Height);

            if (slot.Camera is null) {
                m_targetSlots.Add(item: new WorldComposedSlot(Region: region, SeatOrder: seatOrder++, Camera: null));
            } else {
                m_targetSlots.Add(item: new WorldComposedSlot(Region: region, SeatOrder: -1, Camera: slot.Camera));
            }
        }
    }

    private void ResolveBuiltin(int joinedCount, int soleEditorIndex, float workbenchFraction) {
        m_targetSlots.Clear();

        for (var index = 0; (index < joinedCount); index++) {
            var region = WorldFrameSource.LayoutRegion(count: joinedCount, index: index, soleEditorIndex: soleEditorIndex, workbenchFraction: workbenchFraction);

            m_targetSlots.Add(item: new WorldComposedSlot(Region: region, SeatOrder: index, Camera: null));
        }
    }

    // Every camera-bearing slot resolves to the live camera override (SelectCamera) when one is set.
    private void ApplyCameraOverride(string? cameraOverride) {
        if (cameraOverride is not { } camera) {
            return;
        }

        for (var index = 0; (index < m_targetSlots.Count); index++) {
            if (m_targetSlots[index].Camera is not null) {
                m_targetSlots[index] = (m_targetSlots[index] with { Camera = camera });
            }
        }
    }

    private void BuildBindings(List<WorldComposedSlot> slots, List<ViewBinding> into) {
        into.Clear();

        foreach (var slot in slots) {
            var id = ((slot.Camera is { } camera) ? new ViewId(Value: CameraId(name: camera)) : new ViewId(Value: (-(slot.SeatOrder + 1))));

            into.Add(item: new ViewBinding(View: id, Region: slot.Region));
        }
    }

    private void DecodeSlots() {
        m_slots.Clear();

        foreach (var binding in m_currentBindings) {
            var value = binding.View.Value;

            m_slots.Add(item: ((value < 0)
                ? new WorldComposedSlot(Region: binding.Region, SeatOrder: (-value - 1), Camera: null)
                : new WorldComposedSlot(Region: binding.Region, SeatOrder: -1, Camera: m_cameraNames[index: value])));
        }
    }

    private int CameraId(string name) {
        if (m_cameraIds.TryGetValue(key: name, value: out var id)) {
            return id;
        }

        id = m_cameraNames.Count;
        m_cameraNames.Add(item: name);
        m_cameraIds[name] = id;

        return id;
    }

    private static void CopyBindings(IReadOnlyList<ViewBinding> source, List<ViewBinding> into) {
        into.Clear();

        for (var index = 0; (index < source.Count); index++) {
            into.Add(item: source[index]);
        }
    }

    private static WorldViewLayout? FindLayout(IReadOnlyList<WorldViewLayout> layouts, string name) {
        foreach (var layout in layouts) {
            if (string.Equals(a: layout.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return layout;
            }
        }

        return null;
    }

    private static WorldViewLayout? FindBySeatCount(IReadOnlyList<WorldViewLayout> layouts, int seatCount) {
        foreach (var layout in layouts) {
            if (layout.SeatCount == seatCount) {
                return layout;
            }
        }

        return null;
    }
}
