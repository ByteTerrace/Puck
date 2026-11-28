using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// A placement's shift facet: the row's root moves by an offset read from state, in world axes, added to wherever the
/// row stands (its resolved frame, or the body it rides): a thing a rule places (a field cast where its caster stood) or
/// moves (a floor button sinking under a foot and rising back). Each member is a literal or a numeric state binding,
/// never keys, so the authority reads the offset exactly from the arena at a tick and a presentation reads the cells its
/// mirror presents, interpolated between ticks. Absent (null) members are zero.
/// </summary>
/// <remarks>Whatever reads a row's transform reads it with the shift: its drawn copy every frame
/// (<c>Client.WorldTurnedPose.Shifted</c>) and, for a solid row under the analytic contact provider, its colliders every tick
/// (<c>Server.WorldColliderSet.RefreshAttached</c>). Parent frames, pointer surfaces, spatial volumes, boards and surface
/// projections read the row's static frame, so a shifting row carries none of them and parents no row
/// (<see cref="WorldDefinitionValidator"/>).</remarks>
/// <param name="X">The offset along world X, in metres.</param>
/// <param name="Y">The offset along world Y, in metres.</param>
/// <param name="Z">The offset along world Z, in metres.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementShift(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? X = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Y = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Z = null
) {
    /// <summary>Returns the offset at an authority tick, each member read exactly (<see cref="BindableScalar.ExactAt"/>);
    /// an absent or unreadable member is zero.</summary>
    /// <param name="definition">The document whose state the bindings read.</param>
    /// <param name="tick">The authority tick.</param>
    /// <param name="engineTick">The engine tick, which an advancing clock row reads.</param>
    /// <param name="arena">The live store, or <see langword="null"/> for the one bound to <paramref name="definition"/>.</param>
    /// <returns>The offset, in metres.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public FixedVector3 OffsetAt(WorldDefinition definition, ulong tick, ulong engineTick, StateArena? arena = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new FixedVector3(X: Read(scalar: X), Y: Read(scalar: Y), Z: Read(scalar: Z));

        FixedQ4816 Read(BindableScalar? scalar) => ((scalar is { } value)
            ? value.ExactAt(arena: arena, definition: definition, engineTick: engineTick, fallback: FixedQ4816.Zero, tick: tick)
            : FixedQ4816.Zero);
    }
    /// <summary>Returns the offset a presentation shows: each member's presented value through <paramref name="read"/>.</summary>
    /// <param name="read">Reads one member's presented value.</param>
    /// <returns>The offset, in metres.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    public Vector3 PresentedOffset(Func<BindableScalar, float> read) {
        ArgumentNullException.ThrowIfNull(argument: read);

        return new Vector3(x: ((X is { } x) ? read(arg: x) : 0f), y: ((Y is { } y) ? read(arg: y) : 0f), z: ((Z is { } z) ? read(arg: z) : 0f));
    }
    /// <summary>Returns each member the facet authors, with its name, for validation.</summary>
    /// <returns>The authored members, X first.</returns>
    public IEnumerable<(string Name, BindableScalar Value)> Members() {
        if (X is { } x) { yield return ("x", x); }
        if (Y is { } y) { yield return ("y", y); }
        if (Z is { } z) { yield return ("z", z); }
    }
}
