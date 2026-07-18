namespace Puck.SdfVm.Views;

/// <summary>One view occupying one region — a single row of a <see cref="ViewLayout"/>. Pure data: WHICH registered
/// view (see <see cref="ViewStack.Register"/>) sits WHERE, normalized to the frame.</summary>
/// <param name="View">The view's id.</param>
/// <param name="Region">Its normalized screen region.</param>
public readonly record struct ViewBinding(ViewId View, Puck.Compositing.NormalizedRect Region);

/// <summary>A full frame's slot assignment at one moment — the view-stack analogue of
/// <c>Puck.Demo.Overworld.ScreenLayoutDirector</c>'s per-slot rect array, generalized to name ANY registered view (not
/// only a room/pane camera). <see cref="ViewTransition"/> eases between two of these.</summary>
/// <param name="Bindings">The layout's bindings, in slot order (index N of one layout corresponds to index N of the
/// other layout a <see cref="ViewTransition"/> eases between/toward — see its remarks for what happens when the two
/// counts differ).</param>
public readonly record struct ViewLayout(IReadOnlyList<ViewBinding> Bindings) {
    /// <summary>The empty layout — no views, no regions (a valid <c>to</c> or <c>from</c> for a transition that fades
    /// a slot out/in entirely).</summary>
    public static ViewLayout Empty { get; } = new(Bindings: []);
}

/// <summary>
/// Eases a <see cref="ViewStack"/> composition from one <see cref="ViewLayout"/> to another. Each slot can change
/// both its normalized region and the identity of the view occupying it.
/// <para>
/// PER-SLOT SEMANTICS: slot N of the starting layout pairs with slot N of the ending layout (by INDEX, not by
/// matching <see cref="ViewId"/> — a caller wanting a specific view to persist across the transition puts it at the
/// same index in both layouts). The REGION always eases continuously (a plain lerp under the easing curve); the VIEW
/// occupying that region is a HARD CUT at the eased midpoint (0.5) — the starting view before,
/// the ending view at and after. This mirrors <c>ScreenLayoutDirector.AdvanceLayout</c>'s own "a
/// departing rect collapses in place / an arriving rect grows from its target's center" reading: two different
/// content sources cannot cross-fade pixel-for-pixel without alpha compositing this primitive does not attempt, so
/// the collapse-then-grow cut is the honest generalization — continuous motion, discontinuous content, exactly like
/// the existing director's own boot/mode transitions already read.
/// </para>
/// <para>
/// A layout with fewer bindings than the other pads the SHORT side by holding its OWN view at the OTHER layout's
/// region collapsed to its center point (zero-area) — a slot appearing/disappearing grows from / shrinks to nothing,
/// the same convention <c>ScreenLayoutDirector</c> uses for a boot/reveal's arriving/departing panes.
/// </para>
/// </summary>
public sealed class ViewTransition {
    private readonly ViewLayout m_from;
    private readonly ViewLayout m_to;
    private readonly float m_durationSeconds;
    private readonly Func<float, float> m_easing;
    private readonly ViewBinding[] m_scratch;

    /// <summary>Initializes a transition between two layouts.</summary>
    /// <param name="from">The starting layout shown at elapsed time 0; see <see cref="Sample"/>.</param>
    /// <param name="to">The ending layout (what is shown once settled).</param>
    /// <param name="durationSeconds">How long the ease takes; clamped to a small positive floor so a caller can never
    /// divide by zero by passing 0.</param>
    /// <param name="easing">The easing curve applied to the [0,1] progress fraction before it drives the region lerp
    /// and the view-identity cut point; <see langword="null"/> defaults to linear.</param>
    public ViewTransition(ViewLayout from, ViewLayout to, float durationSeconds, Func<float, float>? easing = null) {
        m_from = from;
        m_to = to;
        m_durationSeconds = MathF.Max(x: durationSeconds, y: 0.001f);
        m_easing = (easing ?? (static t => t));
        m_scratch = new ViewBinding[Math.Max(val1: from.Bindings.Count, val2: to.Bindings.Count)];
    }

    /// <summary>Samples this transition at <paramref name="elapsedSeconds"/> since it started.</summary>
    /// <param name="elapsedSeconds">Seconds since the transition began (the render clock, not wall time — a caller
    /// advancing this on a deterministic tick gets a deterministic transition).</param>
    /// <param name="complete">Whether the transition has fully settled (<paramref name="elapsedSeconds"/> at or past
    /// the duration) — the caller's cue to stop sampling and, if it wishes, release the FROM view's registration.</param>
    /// <returns>The eased layout at this instant — a REUSED buffer, valid only until the next <see cref="Sample"/>
    /// call.</returns>
    public ViewLayout Sample(float elapsedSeconds, out bool complete) {
        var t = Math.Clamp(value: (elapsedSeconds / m_durationSeconds), min: 0f, max: 1f);

        complete = (t >= 1f);

        var eased = Math.Clamp(value: m_easing(t), min: 0f, max: 1f);
        var cutToDestination = (eased >= 0.5f);

        for (var index = 0; (index < m_scratch.Length); index++) {
            var fromBinding = ((index < m_from.Bindings.Count)
                ? m_from.Bindings[index]
                : new ViewBinding(View: m_to.Bindings[index].View, Region: CenterOf(rect: m_to.Bindings[index].Region)));
            var toBinding = ((index < m_to.Bindings.Count)
                ? m_to.Bindings[index]
                : new ViewBinding(View: m_from.Bindings[index].View, Region: CenterOf(rect: m_from.Bindings[index].Region)));

            m_scratch[index] = new ViewBinding(
                Region: Lerp(a: fromBinding.Region, b: toBinding.Region, t: eased),
                View: (cutToDestination ? toBinding.View : fromBinding.View)
            );
        }

        return new ViewLayout(Bindings: m_scratch);
    }

    private static Puck.Compositing.NormalizedRect CenterOf(Puck.Compositing.NormalizedRect rect) =>
        new(X: (rect.X + (0.5f * rect.Width)), Y: (rect.Y + (0.5f * rect.Height)), Width: 0f, Height: 0f);
    private static Puck.Compositing.NormalizedRect Lerp(Puck.Compositing.NormalizedRect a, Puck.Compositing.NormalizedRect b, float t) =>
        new(
            Height: (a.Height + ((b.Height - a.Height) * t)),
            Width: (a.Width + ((b.Width - a.Width) * t)),
            X: (a.X + ((b.X - a.X) * t)),
            Y: (a.Y + ((b.Y - a.Y) * t))
        );
}
