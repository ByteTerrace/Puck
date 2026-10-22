using System.Globalization;
using Puck.Maths;

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
    /// scales the body by the pair's scale (<see cref="WorldFrameIsometry.TryScale"/>): its cell of
    /// <c>bodies.scaleRow</c> is multiplied by it and published before the next step, so its collider, speeds, jump,
    /// reach and drawn form all follow. Reports a crossing it applied to <see cref="CrossingTap"/>. A live crossing and
    /// a replay's re-drive both apply here. The caller holds the authority gate.</summary>
    /// <param name="crossing">The crossing.</param>
    /// <param name="reason">Why the body did not move, when it did not.</param>
    /// <returns><see langword="true"/> when the index held an active body and it moved; <see langword="false"/> when
    /// it held none, or the pair cannot be crossed at its scale: the faces are not one rectangle at two sizes, the
    /// world authors no <c>bodies.scaleRow</c>, or the scaled size would leave that row's envelope.</returns>
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
            // The row is the body's size; publishing it resyncs the body (WorldPopulation.SyncBodyScale) before the
            // next step moves it, and delivers the size to presentation with the same tick's state.
            _ = PublishArena();
        }

        CrossingTap?.Invoke(obj: crossing);
        reason = string.Empty;

        return true;
    }
    // Writes the body's scaled size into its bodies.scaleRow cell, refusing a world that authors no row and a size
    // outside the row's envelope before anything moves.
    private bool TryWriteCrossingScale(WorldBody body, int slot, FixedQ4816 scale, out string reason) {
        var definition = m_document.Definition;

        if (
            (definition.Population.ScaleRow is not { } scaleRow) ||
            (WorldDefinitionRows.FindStateRow(
                rows: definition.State,
                name: scaleRow
            ) is not { } row)
        ) {
            reason = $"the pair scales a traveller by {Format(value: scale)}, and the world authors no bodies.scaleRow to hold a body's size";

            return false;
        }

        var scaled = (body.Scale * scale);

        if (
            ((row.Min is { } minimum) && (scaled.Value < minimum)) ||
            ((row.Max is { } maximum) && (scaled.Value > maximum))
        ) {
            reason = $"the pair scales body {slot} from {Format(value: body.Scale)} to {Format(value: scaled)}, outside bodies.scaleRow '{scaleRow}' {Format(value: FixedQ4816.FromRawBits(value: row.Min ?? 0L))}..{Format(value: FixedQ4816.FromRawBits(value: row.Max ?? 0L))}";

            return false;
        }
        if (!m_arena.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: CellName.Parse(candidate: scaleRow)
        )) {
            reason = $"bodies.scaleRow '{scaleRow}' is not in the state catalog";

            return false;
        }

        return m_arena.TryWriteOrMint(
            evicted: out _,
            key: m_arena.Keys.Intern(name: CellName.Parse(candidate: slot.ToString(provider: CultureInfo.InvariantCulture))),
            reason: out reason,
            rowOrdinal: handle.Ordinal,
            value: CellValue.Fixed(rawBits: scaled.Value)
        );

        static string Format(FixedQ4816 value) => ((double)value).ToString(
            format: "0.#####",
            provider: CultureInfo.InvariantCulture
        );
    }
}
