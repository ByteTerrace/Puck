using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldStateReader.TryReadEased"/>/<see cref="WorldStateReader.TryEvaluateDynamics"/>
/// — the closed-form second-order read a <see cref="StateDynamics"/> trait drives. <see cref="WorldStateReader.TryRead"/>
/// (truth) is proved unaffected by a trait's presence; the mutation-side rebase (the arena a write composes through)
/// is out of reach here (this project carries no <c>WorldServer</c>) and is proved by <c>StateDynamicsRebaseLawTests</c>
/// in <c>Puck.World.Tests</c> instead.
/// </summary>
public sealed class StateDynamicsReadLawTests {
    private static readonly DynamicsRow Critical = new(
        Damping: 1f,
        Frequency: 1f,
        Name: "critical",
        Response: 0f
    );
    private static readonly DynamicsRow Ringing = new(
        Damping: 0f,
        Frequency: 1f,
        Name: "ringing",
        Response: 0f
    );

    // 240 Hz — the repository's fixed simulation rate — so tick counts read directly as seconds/240.
    private static WorldDefinition BuildDefinition(WorldStateRow row) => new(
        DynamicsRaw: [Critical, Ringing],
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [row])
    );
    private static WorldStateRow EasingRow(string dynamicsRow) => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: CellKind.Int,
        Min: 0,
        Max: 1000,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 300)
            )],
        Dynamics: new StateDynamics(Row: dynamicsRow)
    );
    // A cell carrying a dynamics trait and an authored clock starts its follower at the clock's own Y0/V0, not at
    // its stored value — the only way to displace the follower from its target for a law that must observe it move.
    private static WorldStateRow EasingRowWithClock(string dynamicsRow, long y0, long v0) => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: CellKind.Int,
        Min: 0,
        Max: 1000,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 300),
                Clock: new StateCellClock(
                    Y0: y0,
                    V0: v0
                )
            )],
        Dynamics: new StateDynamics(Row: dynamicsRow)
    );
    private static WorldStateRow PlainRow() => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: CellKind.Int,
        Min: 0,
        Max: 1000,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 300)
            )]
    );

    [Fact]
    public void TryReadEased_AtCriticalDamping_RisesMonotonicallyFromRestToTruth() {
        var definition = BuildDefinition(row: EasingRow(dynamicsRow: Critical.Name));
        var previous = -1L;

        foreach (var tick in ((ulong[])[0UL, 24UL, 48UL, 96UL, 192UL, 384UL, 768UL, 1500UL])) {
            Assert.True(condition: WorldStateReader.TryReadEased(
                definition: definition,
                key: null,
                rawValue: out var raw,
                row: out _,
                rowName: "gauge",
                text: out _,
                tick: tick,
                engineTick: tick
            ));
            Assert.True(
                condition: (raw >= previous),
                userMessage: $"tick {tick}: eased {raw} regressed below the previous sample {previous} — critical damping never overshoots, so a rest-start rise must be monotone."
            );
            Assert.True(
                condition: (raw <= 300L),
                userMessage: $"tick {tick}: eased {raw} exceeded the target 300 — critical damping never overshoots."
            );
            previous = raw!.Value;
        }

        Assert.True(
            condition: (previous == 300L),
            userMessage: "the final sampled tick did not reach the settled truth."
        );
    }
    // A cell carrying no clock reads at rest — its follower starts at its own stored value, not at zero — so its
    // eased value at the epoch is exactly the cell's own stored truth.
    [Fact]
    public void TryReadEased_AtTheEpochWithNoClock_ReadsTheCellsOwnStoredValue() {
        var definition = BuildDefinition(row: EasingRow(dynamicsRow: Critical.Name));

        Assert.True(condition: WorldStateReader.TryReadEased(
            definition: definition,
            key: null,
            rawValue: out var raw,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: 0UL,
            engineTick: 0UL
        ));
        Assert.Equal(
            actual: raw,
            expected: 300L
        );
    }
    // ζ = 0: an undamped free oscillation from rest never settles and rings forever — a discriminating control
    // against the critically-damped law above, whose whole point is that it DOES settle. From an authored y0 = 0
    // chasing a target of 300, the closed form is EXACT: y(t) = 300·(1 − cos(ωt)), ω = 2π rad/s at f = 1 Hz — a full
    // trough at t = 0, a peak at t = 0.5s (tick 120), and back to the trough at t = 1.0s (tick 240, one full period
    // later), bounded to [0, 600] throughout rather than diverging.
    [Fact]
    public void TryReadEased_AtZeroDamping_RingsInABoundedOscillation() {
        var definition = BuildDefinition(row: EasingRowWithClock(
            dynamicsRow: Ringing.Name,
            v0: 0L,
            y0: 0L
        ));

        Assert.True(condition: WorldStateReader.TryReadEased(
            definition: definition,
            key: null,
            rawValue: out var trough,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: 0UL,
            engineTick: 0UL
        ));
        Assert.Equal(
            actual: trough,
            expected: 0L
        );

        Assert.True(condition: WorldStateReader.TryReadEased(
            definition: definition,
            key: null,
            rawValue: out var peak,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: 120UL,
            engineTick: 120UL
        ));
        Assert.InRange(
            actual: peak!.Value,
            low: 590L,
            high: 600L
        );

        Assert.True(condition: WorldStateReader.TryReadEased(
            definition: definition,
            key: null,
            rawValue: out var fullPeriod,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: 240UL,
            engineTick: 240UL
        ));
        Assert.InRange(
            actual: fullPeriod!.Value,
            low: 0L,
            high: 10L
        );
    }
    [Fact]
    public void TryReadEased_PastTheSettleHorizon_ReadsExactlyTheTruth() {
        var definition = BuildDefinition(row: EasingRow(dynamicsRow: Critical.Name));

        // ζω·t·log2e >> 17 (the documented settle bound) at 1000 ticks / 240 Hz for f=1 Hz, ζ=1.
        Assert.True(condition: WorldStateReader.TryReadEased(
            definition: definition,
            key: null,
            rawValue: out var raw,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: 1000UL,
            engineTick: 1000UL
        ));
        Assert.Equal(
            actual: raw,
            expected: 300L
        );
    }
    [Fact]
    public void TryReadEased_WithNoTrait_AgreesWithTryReadBitForBit() {
        var definition = BuildDefinition(row: PlainRow());

        foreach (var tick in ((ulong[])[0UL, 1UL, 500UL])) {
            Assert.True(condition: WorldStateReader.TryRead(
                definition: definition,
                key: null,
                rawValue: out var truth,
                row: out _,
                rowName: "gauge",
                text: out _,
                tick: tick,
                engineTick: tick
            ));
            Assert.True(condition: WorldStateReader.TryReadEased(
                definition: definition,
                key: null,
                rawValue: out var eased,
                row: out _,
                rowName: "gauge",
                text: out _,
                tick: tick,
                engineTick: tick
            ));
            Assert.Equal(
                actual: eased,
                expected: truth
            );
        }
    }
    [Fact]
    public void TryRead_ReportsTheStoredTruth_RegardlessOfTickOrTheTraitsPresence() {
        var definition = BuildDefinition(row: EasingRow(dynamicsRow: Critical.Name));

        foreach (var tick in ((ulong[])[0UL, 1UL, 240UL, 1000UL])) {
            Assert.True(condition: WorldStateReader.TryRead(
                definition: definition,
                key: null,
                rawValue: out var raw,
                row: out _,
                rowName: "gauge",
                text: out _,
                tick: tick,
                engineTick: tick
            ));
            Assert.Equal(
                actual: raw,
                expected: 300L
            );
        }
    }
}
