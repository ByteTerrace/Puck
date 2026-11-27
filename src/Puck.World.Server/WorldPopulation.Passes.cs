using Puck.Maths;

namespace Puck.World.Server;

// THE PASSES A BODY HOLDS. A solid row that names pass registers (WorldSolid.Passes) lives by row in the contact
// providers' live set, and a body holding one of them nonzero meets none of its colliders. The invariant: every query
// a body's own step makes (its ride, its motion program's sweeps, its supports and step-ups, the growth its size asks
// for, the escape a kit change makes) runs with that body's held mask on the set and no other's, and no query runs
// with a mask while another thread reads the set. What enforces it: HoldPasses is the one door that sets the mask; it
// refuses inside a kit partition, and a body that holds a pass is never planned into one (HoldsPasses beside
// StepIsLocal), so a mask is only ever set on the serial path, around that body's own work. Where it stops: the rigid
// solve, body pairs and navigation read no passes, so a rigid body meets every gated row and a route plans as if
// every gated row stands.
public sealed partial class WorldPopulation {
    /// <summary>Opens a body's passes on the live contact set (<see cref="WorldColliderSet.HoldPasses"/>): until the
    /// scope ends, every query leaves out each gated row one of whose pass registers the body holds nonzero. Opens
    /// nothing in a world whose solids name no pass.</summary>
    /// <param name="index">The body's entity index.</param>
    /// <returns>The scope, which gives back the previous mask when disposed.</returns>
    /// <exception cref="InvalidOperationException">A kit partition is running.</exception>
    internal WorldColliderSet.PassSweep HoldPasses(int index) {
        if ((MovingSolids is not { } set) || set.PassSlots.IsEmpty) {
            return default;
        }
        var held = HeldPasses(index: index, slots: set.PassSlots);

        if (held != 0UL) {
            ThrowIfPartitioned();
        }
        // A body holding none still opens its scope when another body's mask stands (a scope opened inside another).
        return set.HoldPasses(held: held);
    }
    // Whether a body holds any pass a gated row names, so its step runs serially, where its mask may be set.
    private bool HoldsPasses(int index) => ((MovingSolids is { } set) && (HeldPasses(index: index, slots: set.PassSlots) != 0UL));
    // A body's held mask: bit i set when it holds the counter in the set's i-th pass slot nonzero.
    private ulong HeldPasses(int index, ReadOnlySpan<int> slots) {
        var held = 0UL;

        for (var bit = 0; (bit < slots.Length); bit++) {
            if (m_actionStateLane.Counter(ordinal: index, slot: slots[bit]) != FixedQ4816.Zero) {
                held |= (1UL << bit);
            }
        }
        return held;
    }
}
