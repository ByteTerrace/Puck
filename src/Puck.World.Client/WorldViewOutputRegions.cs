using Puck.Abstractions.Presentation;

namespace Puck.World.Client;

/// <summary>The allocation envelope of each view and pane the composition places: the largest width and height its
/// rect reaches over the transition in flight (<see cref="WorldViewComposer.StartSlots"/> to
/// <see cref="WorldViewComposer.EndSlots"/>), so easing between the two never resizes a node. A settled composition
/// allocates each occupant exactly its rect, and the allocation changes once a transition, when it starts or settles,
/// never as its rect eases. An envelope sizes an image, not a placement, so its origin is zero.</summary>
public static class WorldViewOutputRegions {
    /// <summary>Fills one envelope per view ordinal the presenter renders. A view's ordinal counts the slots before it
    /// that render a view, as the presenter counts them: a camera slot whose camera the definition authors, and a seat
    /// slot whose seat order is joined; a pane renders none. An ordinal covers the slot it holds at the start and the
    /// slot it holds at the end, since an occupant cuts at the transition's midpoint and a cut can move an ordinal to
    /// another slot, each slot over both its endpoints, which every rect it eases through lies between. With no slot
    /// rendering a view, the one view is the presenter's whole-display spectator.</summary>
    /// <param name="composer">The composer, after this frame's composition.</param>
    /// <param name="joinedCount">The joined local-seat count the presenter binds seat slots against.</param>
    /// <param name="cameras">The definition's cameras, which decide whether a camera slot renders a view.</param>
    /// <param name="envelopes">The list to fill, cleared first: one envelope per view ordinal.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static void Views(WorldViewComposer composer, int joinedCount, IReadOnlyList<WorldCamera> cameras, List<NormalizedRect> envelopes) {
        ArgumentNullException.ThrowIfNull(argument: composer);
        ArgumentNullException.ThrowIfNull(argument: cameras);
        ArgumentNullException.ThrowIfNull(argument: envelopes);

        envelopes.Clear();
        Cover(cameras: cameras, endpoint: composer.StartSlots, envelopes: envelopes, joinedCount: joinedCount, other: composer.EndSlots);
        Cover(cameras: cameras, endpoint: composer.EndSlots, envelopes: envelopes, joinedCount: joinedCount, other: composer.StartSlots);
        if (envelopes.Count == 0) {
            envelopes.Add(item: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f));
        }
    }
    /// <summary>Returns a pane's envelope: every slot it holds at either endpoint of the transition in flight, each over
    /// both its endpoints, or its current rect when neither endpoint shows it.</summary>
    /// <param name="composer">The composer, after this frame's composition.</param>
    /// <param name="instance">The pane's <c>views.graphs</c> instance name.</param>
    /// <param name="region">The pane's current rect.</param>
    /// <returns>The pane's envelope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="composer"/> or <paramref name="instance"/> is
    /// <see langword="null"/>.</exception>
    public static NormalizedRect Pane(WorldViewComposer composer, string instance, NormalizedRect region) {
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

        return (held
            ? new NormalizedRect(Height: height, Width: width, X: 0f, Y: 0f)
            : (region with { X = 0f, Y = 0f }));
    }

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
