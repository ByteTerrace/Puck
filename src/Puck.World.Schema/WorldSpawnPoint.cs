using System.Numerics;
using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>One named spawn pose available to seats and population policies.</summary>
/// <param name="Id">The stable spawn name, unique within the definition.</param>
/// <param name="Position">The seat's spawn position.</param>
/// <param name="YawDegrees">The spawn yaw about +Y, in degrees.</param>
public readonly record struct WorldSpawnPoint(string Id, DocumentVector3 Position, float YawDegrees = 0f);
/// <summary>The implicit spawn point every world with no authored <c>spawnPoints</c> section resolves against.</summary>
public static class WorldSpawnPointDefaults {
    /// <summary>The spawn-point id an absent <c>population.seatSpawns</c> derives for every seat.</summary>
    public const string ImplicitOriginId = "origin";

    /// <summary>Gets the implicit spawn point at world-space zero, keyed <see cref="ImplicitOriginId"/>.</summary>
    public static WorldSpawnPoint ImplicitOrigin { get; } = new(
        Id: ImplicitOriginId,
        Position: Vector3.Zero
    );
}
/// <summary>The row-to-entity assignment declaration — nothing about <see cref="Sequence"/>/<see cref="Rows"/> is kit-specific,
/// so the same primitive distributes the kit table (a way of moving) and the look table (a way of looking) across the
/// population. Resolved once at construction into each entry's fixed row index (precompute; zero steady-state cost). The
/// kit assignment affects the simulation (it selects the compiled tuning/action bindings); the look assignment is
/// presentation-only (it selects the appearance row).</summary>
/// <param name="Sequence">The sequence that selects a row.</param>
/// <param name="Rows">An authored row-name view whose entries may be literals or Text state-cell references, or empty
/// to select from every declared row in declaration order.</param>
public sealed record WorldRowAssignment(WorldSequence Sequence, IReadOnlyList<DocumentIdentifier> Rows) {
    private readonly IReadOnlyList<DocumentIdentifier> m_rows = (Rows ?? []);

    /// <summary>Gets the authored row-name view. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="WorldMotionModel.Grounded.Response"/>'s does.</summary>
    public IReadOnlyList<DocumentIdentifier> Rows {
        get => m_rows;
        init => m_rows = (value ?? []);
    }

    /// <summary>Gets the inert assignment policy — the index sequence (row 0 of whatever it selects) over every
    /// declared row in declaration order.</summary>
    public static WorldRowAssignment Default { get; } = new(
        Sequence: WorldSequence.IndexDefault,
        Rows: []
    );
}
/// <summary>The deterministic pose compiled from one authored spawn point.</summary>
/// <param name="Position">The fixed-point world position.</param>
/// <param name="YawRadians">The fixed-point yaw in radians.</param>
public readonly record struct FixedSpawnPoint(FixedVector3 Position, FixedQ4816 YawRadians) {
    /// <summary>Compiles one authored spawn pose to deterministic numerics.</summary>
    /// <param name="point">The authored spawn point.</param>
    /// <returns>The compiled pose.</returns>
    public static FixedSpawnPoint Compile(in WorldSpawnPoint point) => new(
        Position: new FixedVector3(
            X: FixedQ4816.FromDouble(value: point.Position.X),
            Y: FixedQ4816.FromDouble(value: point.Position.Y),
            Z: FixedQ4816.FromDouble(value: point.Position.Z)
        ),
        YawRadians: FixedQ4816.FromDouble(value: (point.YawDegrees * (Math.PI / 180.0)))
    );
}
