using System.Globalization;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>One body's crossing through a portal pair whose two faces stand in the same world: the body and the two
/// face frames it moves between (<see cref="WorldBody.CrossInPlace"/>).</summary>
/// <param name="Slot">The body index, which the crossing keeps.</param>
/// <param name="Source">The frame of the face the body entered.</param>
/// <param name="Destination">The frame of that face's counterpart.</param>
public readonly record struct WorldInPlaceCrossing(int Slot, WorldFaceFrame Source, WorldFaceFrame Destination);
public sealed partial class WorldServer {
    /// <summary>Gets or sets the observer of every in-place crossing this authority applies, heard inside the
    /// authority operation that applied it, so its position among the authority's inputs is the crossing's own. The
    /// replay tape attaches here while it records.</summary>
    public Action<WorldInPlaceCrossing>? CrossingTap { get; set; }

    /// <summary>Moves one body through a portal pair whose two faces both stand in this world
    /// (<see cref="WorldBody.CrossInPlace"/>), keeping its body index, kit, grants, motion and action state and tape,
    /// and accumulates the pair's turn into the body's arrival turn. A pair whose counterpart face is a different size
    /// scales the body by the pair's scale (<see cref="WorldFrameIsometry.TryScale"/>): the register
    /// <c>bodies.scale</c> names is multiplied by it and the body resynced before the next step, so its collider,
    /// speeds, jump, reach and drawn form all follow, and the size goes with the body to the next world. Reports a crossing it applied to <see cref="CrossingTap"/>. A live crossing and
    /// a replay's re-drive both apply here. The caller holds the authority gate.</summary>
    /// <param name="crossing">The crossing.</param>
    /// <param name="reason">Why the body did not move, when it did not.</param>
    /// <returns><see langword="true"/> when the index held an active body and it moved; <see langword="false"/> when
    /// it held none, or the pair cannot be crossed at its scale: the faces are not one rectangle at two sizes, the
    /// world authors no <c>bodies.scale</c> register, or the scaled size would leave that register's envelope.</returns>
    public bool TryApplyInPlaceCrossing(in WorldInPlaceCrossing crossing, out string reason) {
        if (
            (((uint)crossing.Slot) >= ((uint)m_population.Capacity)) ||
            !m_population.IsActive(index: crossing.Slot) ||
            (m_population.EntryBody(index: crossing.Slot) is not { } body)
        ) {
            reason = $"body {crossing.Slot} is not live";

            return false;
        }

        var source = crossing.Source;
        var destination = crossing.Destination;

        if (!WorldFrameIsometry.TryScale(
            destination: in destination,
            reason: out reason,
            scale: out var scale,
            source: in source
        )) {
            return false;
        }
        if (
            (scale != FixedQ4816.One) &&
            !TryWriteCrossingScale(
                body: body,
                reason: out reason,
                scale: scale,
                slot: crossing.Slot
            )
        ) {
            return false;
        }

        body.CrossInPlace(
            destination: in destination,
            source: in source
        );
        m_population.SetTravelTurn(
            slot: crossing.Slot,
            travelTurn: FixedQ4816.WrapAngle(radians: (m_population.TravelTurn(index: crossing.Slot) + WorldFrameIsometry.YawDelta(
                destination: in destination,
                source: in source
            )))
        );

        if (scale != FixedQ4816.One) {
            // The register is the body's size; the resync brings the body to it before the next step moves it, and the
            // next snapshot delivers it to presentation with the body's other registers.
            m_population.SyncBodyScale(definition: m_document.Definition);
        }

        CrossingTap?.Invoke(obj: crossing);
        reason = string.Empty;

        return true;
    }
    // Writes the body's scaled size into the register bodies.scale names, refusing a world that authors none and a size
    // outside the register's envelope before anything moves.
    private bool TryWriteCrossingScale(WorldBody body, int slot, FixedQ4816 scale, out string reason) {
        var definition = m_document.Definition;

        if (
            (definition.Population.Scale is not { } register) ||
            !WorldBodyStateReads.TryResolveSlot(
            definition: definition,
            kind: out var kind,
            name: register,
            slot: out var registerSlot
        ) ||
            (kind != ActionStateKind.Counter)
        ) {
            reason = $"the pair scales a traveller by {Format(value: scale)}, and the world authors no bodies.scale register to hold a body's size";

            return false;
        }

        var lane = m_population.ActionStateLane;
        var current = lane.Counter(
            ordinal: slot,
            slot: registerSlot
        );
        var scaled = (current * scale);

        if (
            (lane.Definitions[registerSlot].Envelope is { } envelope) &&
            !envelope.Contains(value: scaled.Value)
        ) {
            reason = $"the pair scales body {slot} from {Format(value: current)} to {Format(value: scaled)}, outside bodies.scale '{register}' {Format(value: FixedQ4816.FromRawBits(value: envelope.Minimum))}..{Format(value: FixedQ4816.FromRawBits(value: envelope.Maximum))}";

            return false;
        }

        body.ApplyCrossingState(
            requested: scaled.Value,
            slot: registerSlot
        );
        reason = string.Empty;

        return true;

        static string Format(FixedQ4816 value) => ((double)value).ToString(
            format: "0.#####",
            provider: CultureInfo.InvariantCulture
        );
    }
}
