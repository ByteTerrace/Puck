using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldStateReader.TryReadEased"/>/<see cref="WorldStateReader.TryEvaluateDynamics"/>
/// — the closed-form second-order read a <see cref="WorldStateDynamics"/> trait drives. <see cref="WorldStateReader.TryRead"/>
/// (truth) is proved unaffected by a trait's presence; the mutation-side rebase (<c>Server.WorldServer.RebaseCellTraits</c>)
/// is out of reach here (this project carries no <c>WorldServer</c>) and is proved by <c>StateDynamicsRebaseLawTests</c>
/// in <c>Puck.World.Tests</c> instead.
/// </summary>
public sealed class StateDynamicsReadLawTests {
    private static readonly WorldDynamicsRow s_critical = new(Damping: 1f, Frequency: 1f, Name: "critical", Response: 0f);
    private static readonly WorldDynamicsRow s_ringing = new(Damping: 0f, Frequency: 1f, Name: "ringing", Response: 0f);

    // 240 Hz — the repository's fixed simulation rate — so tick counts read directly as seconds/240.
    private static WorldDefinition BuildDefinition(WorldStateRow row) => new(
        DynamicsRaw: [s_critical, s_ringing],
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [row])
    );
    private static WorldStateRow EasingRow(string dynamicsRow) => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: CellKind.Int,
        Min: 0,
        Max: 1000,
        Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 300)],
        Dynamics: new WorldStateDynamics(EpochTick: 0, Row: dynamicsRow, V0: 0, Y0: 0)
    );
    private static WorldStateRow PlainRow() => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: CellKind.Int,
        Min: 0,
        Max: 1000,
        Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 300)]
    );

    [Fact]
    public void TryRead_ReportsTheStoredTruth_RegardlessOfTickOrTheTraitsPresence() {
        var definition = BuildDefinition(row: EasingRow(dynamicsRow: s_critical.Name));

        foreach (var tick in ((ulong[])[0UL, 1UL, 240UL, 1000UL])) {
            Assert.True(condition: WorldStateReader.TryRead(definition: definition, key: null, rawValue: out var raw, row: out _, rowName: "gauge", text: out _, tick: tick));
            Assert.Equal(actual: raw, expected: 300L);
        }
    }
    [Fact]
    public void TryReadEased_AtTheEpoch_ReadsExactlyY0() {
        var definition = BuildDefinition(row: EasingRow(dynamicsRow: s_critical.Name));

        Assert.True(condition: WorldStateReader.TryReadEased(definition: definition, key: null, rawValue: out var raw, row: out _, rowName: "gauge", text: out _, tick: 0UL));
        Assert.Equal(actual: raw, expected: 0L);
    }
    [Fact]
    public void TryReadEased_PastTheSettleHorizon_ReadsExactlyTheTruth() {
        var definition = BuildDefinition(row: EasingRow(dynamicsRow: s_critical.Name));

        // ζω·t·log2e >> 17 (the documented settle bound) at 1000 ticks / 240 Hz for f=1 Hz, ζ=1.
        Assert.True(condition: WorldStateReader.TryReadEased(definition: definition, key: null, rawValue: out var raw, row: out _, rowName: "gauge", text: out _, tick: 1000UL));
        Assert.Equal(actual: raw, expected: 300L);
    }
    [Fact]
    public void TryReadEased_AtCriticalDamping_RisesMonotonicallyFromRestToTruth() {
        var definition = BuildDefinition(row: EasingRow(dynamicsRow: s_critical.Name));
        var previous = -1L;

        foreach (var tick in ((ulong[])[0UL, 24UL, 48UL, 96UL, 192UL, 384UL, 768UL, 1500UL])) {
            Assert.True(condition: WorldStateReader.TryReadEased(definition: definition, key: null, rawValue: out var raw, row: out _, rowName: "gauge", text: out _, tick: tick));
            Assert.True(condition: (raw >= previous), userMessage: $"tick {tick}: eased {raw} regressed below the previous sample {previous} — critical damping never overshoots, so a rest-start rise must be monotone.");
            Assert.True(condition: (raw <= 300L), userMessage: $"tick {tick}: eased {raw} exceeded the target 300 — critical damping never overshoots.");
            previous = raw!.Value;
        }

        Assert.True(condition: (previous == 300L), userMessage: "the final sampled tick did not reach the settled truth.");
    }
    [Fact]
    public void TryReadEased_WithNoTrait_AgreesWithTryReadBitForBit() {
        var definition = BuildDefinition(row: PlainRow());

        foreach (var tick in ((ulong[])[0UL, 1UL, 500UL])) {
            Assert.True(condition: WorldStateReader.TryRead(definition: definition, key: null, rawValue: out var truth, row: out _, rowName: "gauge", text: out _, tick: tick));
            Assert.True(condition: WorldStateReader.TryReadEased(definition: definition, key: null, rawValue: out var eased, row: out _, rowName: "gauge", text: out _, tick: tick));
            Assert.Equal(actual: eased, expected: truth);
        }
    }
    // ζ = 0: an undamped free oscillation from rest never settles and rings forever — a discriminating control
    // against the critically-damped law above, whose whole point is that it DOES settle. From y0 = 0 chasing a
    // target of 300, the closed form is EXACT: y(t) = 300·(1 − cos(ωt)), ω = 2π rad/s at f = 1 Hz — a full trough at
    // t = 0, a peak at t = 0.5s (tick 120), and back to the trough at t = 1.0s (tick 240, one full period later),
    // bounded to [0, 600] throughout rather than diverging.
    [Fact]
    public void TryReadEased_AtZeroDamping_RingsInABoundedOscillation() {
        var definition = BuildDefinition(row: EasingRow(dynamicsRow: s_ringing.Name));

        Assert.True(condition: WorldStateReader.TryReadEased(definition: definition, key: null, rawValue: out var trough, row: out _, rowName: "gauge", text: out _, tick: 0UL));
        Assert.Equal(actual: trough, expected: 0L);

        Assert.True(condition: WorldStateReader.TryReadEased(definition: definition, key: null, rawValue: out var peak, row: out _, rowName: "gauge", text: out _, tick: 120UL));
        Assert.InRange(actual: peak!.Value, low: 590L, high: 600L);

        Assert.True(condition: WorldStateReader.TryReadEased(definition: definition, key: null, rawValue: out var fullPeriod, row: out _, rowName: "gauge", text: out _, tick: 240UL));
        Assert.InRange(actual: fullPeriod!.Value, low: 0L, high: 10L);
    }
}
