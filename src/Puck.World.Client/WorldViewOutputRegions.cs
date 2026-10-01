using Puck.Abstractions.Presentation;

namespace Puck.World.Client;

/// <summary>The allocation envelope of each view and pane the composition places: the largest width and height its
/// rect reaches over the transition in flight (<see cref="WorldViewComposer.StartSlots"/> to
/// <see cref="WorldViewComposer.EndSlots"/>), so easing between the two never resizes a node. A settled composition
/// requests each occupant's own rect. Reservations grow through interrupted transitions and shrink only when the
/// chain settles, never as a rect eases. An envelope sizes an image, not a placement, so its origin is zero.</summary>
public sealed class WorldViewOutputRegions {
    private readonly List<NormalizedRect> m_viewReservations = new();
    private readonly Dictionary<string, NormalizedRect> m_paneReservations = new(comparer: StringComparer.Ordinal);

    /// <summary>Fills an envelope per view ordinal the transition reserves. A view's ordinal counts the slots before it
    /// that render a view, as the presenter counts them: a camera slot whose camera the definition authors, and a seat
    /// slot whose seat order is joined; a pane renders none. An ordinal covers the slot it holds at the start and the
    /// slot it holds at the end, since an occupant cuts at the transition's midpoint and a cut can move an ordinal to
    /// another slot, each slot over both its endpoints, which every rect it eases through lies between. With no slot
    /// rendering a view, the one view is the presenter's whole-display spectator. Call before <see cref="Pane"/> each
    /// frame: settling releases the chain's pane reservations too.</summary>
    /// <param name="composer">The composer, after this frame's composition.</param>
    /// <param name="joinedCount">The joined local-seat count the presenter binds seat slots against.</param>
    /// <param name="cameras">The definition's cameras, which decide whether a camera slot renders a view.</param>
    /// <param name="envelopes">The list to fill, cleared first: one envelope per reserved view ordinal.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public void Views(WorldViewComposer composer, int joinedCount, IReadOnlyList<WorldCamera> cameras, List<NormalizedRect> envelopes) {
        ArgumentNullException.ThrowIfNull(argument: composer);
        ArgumentNullException.ThrowIfNull(argument: cameras);
        ArgumentNullException.ThrowIfNull(argument: envelopes);

        envelopes.Clear();
        Cover(cameras: cameras, endpoint: composer.StartSlots, envelopes: envelopes, joinedCount: joinedCount, other: composer.EndSlots);
        Cover(cameras: cameras, endpoint: composer.EndSlots, envelopes: envelopes, joinedCount: joinedCount, other: composer.StartSlots);
        if (composer.TransitionProgress < 1f) {
            for (var index = 0; ((index < envelopes.Count) && (index < m_viewReservations.Count)); index++) {
                envelopes[index] = Union(first: envelopes[index], second: m_viewReservations[index]);
            }
            for (var index = envelopes.Count; (index < m_viewReservations.Count); index++) {
                envelopes.Add(item: m_viewReservations[index]);
            }
        } else {
            m_paneReservations.Clear();
        }
        m_viewReservations.Clear();
        m_viewReservations.AddRange(collection: envelopes);
    }
    /// <summary>Returns a pane's envelope: every slot it holds at either endpoint of the transition in flight, each over
    /// both its endpoints, or its current rect when neither endpoint shows it, retaining its largest reservation until
    /// the transition chain settles.</summary>
    /// <param name="composer">The composer, after this frame's composition.</param>
    /// <param name="instance">The pane's <c>views.graphs</c> instance name.</param>
    /// <param name="region">The pane's current rect.</param>
    /// <returns>The pane's envelope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="composer"/> or <paramref name="instance"/> is
    /// <see langword="null"/>.</exception>
    public NormalizedRect Pane(WorldViewComposer composer, string instance, NormalizedRect region) {
        ArgumentNullException.ThrowIfNull(argument: composer);
        ArgumentNullException.ThrowIfNull(argument: instance);

        var start = composer.StartSlots;
        var end = composer.EndSlots;
        var width = 0f;
        var height = 0f;
        var held = false;

        for (var index = 0; (index < start.Count); index++) {
            if (
                string.Equals(a: start[index].Instance, b: instance, comparisonType: StringComparison.Ordinal) ||
                string.Equals(a: end[index].Instance, b: instance, comparisonType: StringComparison.Ordinal)
            ) {
                held = true;
                width = MathF.Max(x: width, y: MathF.Max(x: start[index].Region.Width, y: end[index].Region.Width));
                height = MathF.Max(x: height, y: MathF.Max(x: start[index].Region.Height, y: end[index].Region.Height));
            }
        }

        var envelope = (held
            ? new NormalizedRect(Height: height, Width: width, X: 0f, Y: 0f)
            : (region with { X = 0f, Y = 0f }));

        if ((composer.TransitionProgress < 1f) && m_paneReservations.TryGetValue(key: instance, value: out var reserved)) {
            envelope = Union(first: envelope, second: reserved);
        }
        m_paneReservations[instance] = envelope;
        return envelope;
    }

    private static NormalizedRect Union(NormalizedRect first, NormalizedRect second) => new(
        Height: MathF.Max(x: first.Height, y: second.Height),
        Width: MathF.Max(x: first.Width, y: second.Width),
        X: 0f,
        Y: 0f
    );
    // Widens each ordinal's envelope by the slot it holds at one endpoint, the slot spanning both its endpoints.
    private static void Cover(IReadOnlyList<WorldComposedSlot> endpoint, IReadOnlyList<WorldComposedSlot> other, int joinedCount, IReadOnlyList<WorldCamera> cameras, List<NormalizedRect> envelopes) {
        var ordinal = 0;

        for (var index = 0; (index < endpoint.Count); index++) {
            var slot = endpoint[index];

            if (!RendersView(cameras: cameras, joinedCount: joinedCount, slot: slot)) {
                continue;
            }

            var width = MathF.Max(x: slot.Region.Width, y: other[index].Region.Width);
            var height = MathF.Max(x: slot.Region.Height, y: other[index].Region.Height);

            if (ordinal == envelopes.Count) {
                envelopes.Add(item: new NormalizedRect(Height: height, Width: width, X: 0f, Y: 0f));
            } else {
                var envelope = envelopes[ordinal];

                envelopes[ordinal] = new NormalizedRect(
                    Height: MathF.Max(x: envelope.Height, y: height),
                    Width: MathF.Max(x: envelope.Width, y: width),
                    X: 0f,
                    Y: 0f
                );
            }

            ordinal++;
        }
        // An endpoint without a rendered slot still renders the presenter's whole-display spectator. Reserve it on
        // both sides of the midpoint cut, so switching between a small view and that fallback never resizes mid-ease.
        if (ordinal == 0) {
            var whole = new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f);

            if (envelopes.Count == 0) {
                envelopes.Add(item: whole);
            } else {
                envelopes[0] = whole;
            }
        }
    }
    // Whether the presenter renders a view for a slot: a camera slot whose camera the definition authors, or a seat slot
    // whose seat order is joined.
    private static bool RendersView(WorldComposedSlot slot, int joinedCount, IReadOnlyList<WorldCamera> cameras) {
        if (slot.Instance is not null) {
            return false;
        }

        if (slot.Camera is not { } camera) {
            return (((uint)slot.SeatOrder) < ((uint)joinedCount));
        }

        for (var index = 0; (index < cameras.Count); index++) {
            if (string.Equals(a: cameras[index].Name, b: camera, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
}
