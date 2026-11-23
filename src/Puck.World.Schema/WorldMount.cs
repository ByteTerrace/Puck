using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
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
/// <param name="Board">The press that boards the body and leaves it, or <see langword="null"/> for a body boarded only
/// by a composed control set.</param>
public sealed record WorldMount(
    DocumentVector3 Seat,
    DocumentVector3 Exit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMountBoard? Board = null
);
/// <summary>How a seat boards a mount by its own press: standing within <paramref name="Radius"/> of the body, a seat
/// that holds Drive over it and drives nothing else boards on the rising edge of <paramref name="Channel"/>, and a
/// rider's next press of the same channel dissolves its control set and sets it down at the mount's exit. It is the
/// context button's rule for a screen and a tabletop, for a body.</summary>
/// <param name="Channel">The declared channel whose press boards and leaves.</param>
/// <param name="Radius">The distance, in world units at full scale, from the body's origin within which the press
/// boards. Positive.</param>
public sealed record WorldMountBoard([property: DocumentText("logical input channels")] string Channel, float Radius);
/// <summary>The one-time fixed-point compilation of a kit's <see cref="WorldMount"/> facet.</summary>
/// <param name="Seat">The compiled seat point.</param>
/// <param name="Exit">The compiled exit point.</param>
/// <param name="BoardChannel">The channel whose press boards and leaves, or <see langword="null"/> for none.</param>
/// <param name="BoardRadius">The boarding distance at full scale; zero with no channel.</param>
public readonly record struct FixedWorldMount(FixedVector3 Seat, FixedVector3 Exit, string? BoardChannel, FixedQ4816 BoardRadius) {
    /// <summary>Compiles an authored mount facet.</summary>
    /// <param name="mount">The authored facet.</param>
    /// <returns>The facet in fixed point.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mount"/> is <see langword="null"/>.</exception>
    public static FixedWorldMount Compile(WorldMount mount) {
        ArgumentNullException.ThrowIfNull(argument: mount);

        return new FixedWorldMount(
            BoardChannel: mount.Board?.Channel,
            BoardRadius: ((mount.Board is { } board) ? FixedQ4816.FromDouble(value: board.Radius) : FixedQ4816.Zero),
            Exit: FixedVector3.FromVector3(value: mount.Exit),
            Seat: FixedVector3.FromVector3(value: mount.Seat)
        );
    }
}
