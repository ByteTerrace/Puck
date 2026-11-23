using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>A kit's mount facet: where the participant driving a body of this kit rides. While a participant's control
/// set names the body and leaves its own body out, the participant's own body takes <paramref name="Seat"/> in the
/// driven body's frame every tick in place of its own motion, so it goes wherever the driven body goes, across an
/// adjacency included. When the set dissolves it stands at <paramref name="Exit"/> and moves on its own again. A kit
/// without the facet carries no one: a participant driving such a body leaves its own body where it stood.</summary>
/// <param name="Seat">Where the rider's origin is held, in the driven body's own axes at full scale.</param>
/// <param name="Exit">Where the rider's origin is set down when it stops driving, in the same axes.</param>
public sealed record WorldMount(DocumentVector3 Seat, DocumentVector3 Exit);
/// <summary>The one-time fixed-point compilation of a kit's <see cref="WorldMount"/> facet.</summary>
/// <param name="Seat">The compiled seat point.</param>
/// <param name="Exit">The compiled exit point.</param>
public readonly record struct FixedWorldMount(FixedVector3 Seat, FixedVector3 Exit) {
    /// <summary>Compiles an authored mount facet.</summary>
    /// <param name="mount">The authored facet.</param>
    /// <returns>The facet in fixed point.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mount"/> is <see langword="null"/>.</exception>
    public static FixedWorldMount Compile(WorldMount mount) {
        ArgumentNullException.ThrowIfNull(argument: mount);

        return new FixedWorldMount(
            Exit: FixedVector3.FromVector3(value: mount.Exit),
            Seat: FixedVector3.FromVector3(value: mount.Seat)
        );
    }
}
