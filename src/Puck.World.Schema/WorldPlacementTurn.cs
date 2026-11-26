using Puck.Maths;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>
/// A placement's turn facet: the row turns about its own up axis, through its origin, by <c>phase + rate × clock</c>
/// turns added to its <see cref="WorldPlacement.YawDegrees"/>, a turntable driven from state. Each member is a literal
/// or a numeric state binding, never keys, so the authority evaluates the angle exactly from the arena at a tick and a
/// presentation reads the same formula over the cells its mirror presents. Nothing is integrated or stored: the angle
/// is a function of the three values at the moment asked, so a clock row that advances on its own (a slot carrying
/// <c>advance(perSecond:)</c>) turns the row continuously without a rule firing, a rate of zero holds it at its phase,
/// and a rule that changes the rate keeps the angle continuous by rebasing the phase in the same write
/// (<c>phase += (rate − newRate) × clock</c>). Absent (null) is a row that keeps its authored yaw.
/// </summary>
/// <remarks>Whatever reads a row's transform reads it with the turn: its drawn copy every frame
/// (<c>Client.WorldTurnedPose</c>) and, for a solid row under the analytic contact provider, its colliders every tick
/// (<c>Server.WorldColliderSet.RefreshAttached</c>), so a body standing on a turntable rides it. Parent frames, pointer
/// surfaces, spatial volumes, boards and surface projections read the row's static frame, so a turning row carries none
/// of them and parents no row (<see cref="WorldDefinitionValidator"/>).</remarks>
/// <param name="Clock">The clock the rate multiplies, in the clock's own unit (gallery seconds, say); usually a state
/// binding to a slot that advances.</param>
/// <param name="Rate">The turns per clock unit; positive turns the row the way a growing
/// <see cref="WorldPlacement.YawDegrees"/> does. Absent is one turn per unit.</param>
/// <param name="Phase">The turns added before the rate's share. Absent is zero.</param>
public sealed record WorldPlacementTurn(
    BindableScalar Clock,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Rate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Phase = null
) {
    /// <summary>Returns the turn's yaw at an authority tick, in radians in <c>[0, 2π)</c>: the fractional part of
    /// <c>phase + rate × clock</c> turns, each member read from the arena in fixed point. A binding that does not read
    /// a number reads as its member's absent value; a product past the fixed-point range reads as no turn.</summary>
    /// <param name="definition">The document whose state the bindings read.</param>
    /// <param name="tick">The authority tick.</param>
    /// <param name="engineTick">The engine tick, which an advancing clock row reads.</param>
    /// <param name="arena">The live store, or <see langword="null"/> for the one bound to
    /// <paramref name="definition"/>.</param>
    /// <returns>The yaw, in radians.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public FixedQ4816 YawRadiansAt(WorldDefinition definition, ulong tick, ulong engineTick, StateArena? arena = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        try {
            var turns = checked((Read(definition: definition, engineTick: engineTick, fallback: FixedQ4816.Zero, scalar: Phase, tick: tick, arena: arena) +
                (Read(definition: definition, engineTick: engineTick, fallback: FixedQ4816.One, scalar: Rate, tick: tick, arena: arena) *
                Read(definition: definition, engineTick: engineTick, fallback: FixedQ4816.Zero, scalar: Clock, tick: tick, arena: arena))));

            return (FixedQ4816.Fractional(value: turns) * FixedQ4816.Tau);
        } catch (OverflowException) {
            return FixedQ4816.Zero;
        }
    }
    /// <summary>Returns the turn's yaw a presentation shows, in radians in <c>[0, 2π)</c>: the same formula over the values
    /// <paramref name="read"/> answers for each member, carried in double so a long-running clock keeps its fraction.</summary>
    /// <param name="read">Reads one member's presented value, or returns the fallback it is handed when the member reads
    /// none.</param>
    /// <returns>The yaw, in radians.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    public double PresentedYawRadians(Func<BindableScalar, double, double> read) {
        ArgumentNullException.ThrowIfNull(argument: read);

        var turns = (((Phase is { } phase) ? read(arg1: phase, arg2: 0d) : 0d) + (((Rate is { } rate) ? read(arg1: rate, arg2: 1d) : 1d) * read(arg1: Clock, arg2: 0d)));

        return (double.IsFinite(d: turns) ? ((turns - Math.Floor(d: turns)) * Math.Tau) : 0d);
    }
    /// <summary>Returns each member the facet authors, with its name, for validation and the presentation manifest's
    /// readers.</summary>
    /// <returns>The authored members, the clock first.</returns>
    public IEnumerable<(string Name, BindableScalar Value)> Members() {
        yield return ("clock", Clock);
        if (Rate is { } rate) { yield return ("rate", rate); }
        if (Phase is { } phase) { yield return ("phase", phase); }
    }

    // A member's value at a tick: its literal, or its bound cell's live number, a Fixed cell's raw bits or an Int cell's
    // whole number.
    private static FixedQ4816 Read(BindableScalar? scalar, FixedQ4816 fallback, WorldDefinition definition, ulong tick, ulong engineTick, StateArena? arena) {
        if (scalar is not { } value) { return fallback; }
        if (value.Literal is { } literal) { return (float.IsFinite(f: literal) ? FixedQ4816.FromDouble(value: literal) : fallback); }
        if ((value.State is not { } binding) ||
            !WorldStateReader.TryRead(arena: arena, definition: definition, engineTick: engineTick, key: binding.Key, rawValue: out var raw, row: out var row, rowName: binding.Row, text: out _, tick: tick) ||
            (raw is not { } bits)) {
            return fallback;
        }

        return row.Kind switch {
            CellKind.Fixed => FixedQ4816.FromRawBits(value: bits),
            CellKind.Int => FixedQ4816.FromInteger(value: bits),
            _ => fallback,
        };
    }
}
