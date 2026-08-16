using Puck.Commands;
using Puck.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// Covers <see cref="FixedStepPump.Advance"/>'s tick bookkeeping: bit-identical behavior at a constant step size
/// (the regression floor — <c>SimulationRate</c> is 240 Hz everywhere today), and correctness across a step-size
/// CHANGE between calls, which the pump did not handle before this fix (see FixedStepPump's remarks).
/// </summary>
public sealed class FixedStepPumpTests {
    // 50400 / 240 and 50400 / 120 — both legal per EngineTicks.PerRate, chosen so the "rate change" case exercises
    // a real divisor swap rather than an arbitrary pair of numbers.
    private const ulong StepTicks240Hz = 210UL;
    private const ulong StepTicks120Hz = 420UL;

    // A no-op simulation that records every FixedStepContext it is stepped with, in call order — the pump's own
    // bookkeeping is what these tests exercise, not any particular simulation's behavior.
    private sealed class RecordingSimulation : IFixedStepSimulation {
        public readonly List<FixedStepContext> Steps = [];

        // Unused by these tests — every Advance call here passes stepTicks explicitly rather than reading it off the
        // simulation — but the interface requires a value, so this names the same 240 Hz the module doc already
        // pins as "everywhere today".
        public uint RatePerSecond => 240U;

        public void Step(in FixedStepContext context, in CommandSnapshot commands) {
            Steps.Add(item: context);
        }
    }
    // Binds nothing — every Advance call in these tests drives zero captured input, so no source needs a mapping.
    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    // One fixed slot, one fixed principal — the tests probe tick/elapsed-tick bookkeeping, not principal routing.
    private sealed class SingleSlotPrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }

    private static (FixedStepPump Pump, RecordingSimulation Simulation) NewPump() {
        var registry = new CommandRegistry(modules: []);
        var inputRouter = new InputRouter(registry: registry, bindings: new EmptyBindings(), principalResolver: new SingleSlotPrincipal());
        var simulation = new RecordingSimulation();
        var pump = new FixedStepPump(captureOriginTicks: 0UL, inputRouter: inputRouter, registry: registry, simulation: simulation);

        return (pump, simulation);
    }

    /// <summary>At a step size that never changes across the pump's lifetime — the only case reachable before
    /// <c>SimulationRate</c> becomes authorable — every step's <see cref="FixedStepContext.Tick"/> and
    /// <see cref="FixedStepContext.ElapsedTicks"/> must match the pre-fix formula exactly
    /// (<c>tick = elapsedTicks/stepTicks</c> going in, <c>elapsedTicks = (tick+1)*stepTicks</c> coming out). This is
    /// the bit-identical-at-240-Hz regression floor.</summary>
    [Fact]
    public void Advance_ConstantStepTicks_MatchesLegacyTickAndElapsedTicksFormula() {
        var (pump, simulation) = NewPump();

        // Jittery deltas on purpose (a whole step, then a fraction, then two-and-a-fraction) — the accumulator
        // remainder crossing Advance-call boundaries is exactly what the legacy formula (division on a running
        // total) was relying on being exact.
        pump.Advance(deltaTicks: StepTicks240Hz, maxFrameTicks: 10_000UL, stepTicks: StepTicks240Hz);
        pump.Advance(deltaTicks: (StepTicks240Hz / 2UL), maxFrameTicks: 10_000UL, stepTicks: StepTicks240Hz);
        pump.Advance(deltaTicks: ((StepTicks240Hz * 2UL) + (StepTicks240Hz / 2UL)), maxFrameTicks: 10_000UL, stepTicks: StepTicks240Hz);

        // Call 1 delivers exactly one whole step (210 ticks due). Call 2 delivers half a step (held in the
        // accumulator, no step runs). Call 3 delivers the held half plus 2.5 more steps' worth (105 + 525 = 630
        // ticks due = 3 whole steps). Four steps total.
        Assert.Equal(expected: 4, actual: simulation.Steps.Count);

        for (var index = 0; (index < simulation.Steps.Count); index++) {
            var step = simulation.Steps[index];
            var expectedTick = ((ulong)index);
            var expectedElapsedTicks = ((expectedTick + 1UL) * StepTicks240Hz);

            Assert.Equal(expected: expectedTick, actual: step.Tick);
            Assert.Equal(expected: expectedElapsedTicks, actual: step.ElapsedTicks);
            Assert.Equal(expected: StepTicks240Hz, actual: step.StepTicks);
        }

        Assert.Equal(expected: (4UL * StepTicks240Hz), actual: pump.ElapsedTicks);
    }
    /// <summary>The step-size-change case: a live rate swap between two <see cref="FixedStepPump.Advance"/> calls —
    /// unreachable today (the rate is a compile-time constant) but exactly what a portal crossing into a
    /// differently-rated world would exercise. Before this fix, <c>Tick</c> was re-derived as
    /// <c>previousElapsedTicks / stepTicks</c> using the NEW call's stepTicks against a total accumulated under the
    /// OLD one — an integer division that silently reuses tick numbers already assigned. This test proves the fixed
    /// pump instead continues the tick ordinal with no reuse and no gap, and keeps ElapsedTicks as the true
    /// cumulative engine-tick total throughout.</summary>
    [Fact]
    public void Advance_StepTicksChangesBetweenCalls_TickStaysMonotonicAndElapsedTicksStaysCorrect() {
        var (pump, simulation) = NewPump();

        // First call: 3 steps at 240 Hz. previousElapsedTicks starts at 0, so old and new formulas agree here —
        // ticks 0, 1, 2; ElapsedTicks 630 afterward.
        pump.Advance(deltaTicks: (StepTicks240Hz * 3UL), maxFrameTicks: 10_000UL, stepTicks: StepTicks240Hz);

        Assert.Equal(expected: 3, actual: simulation.Steps.Count);
        Assert.Equal(expected: (StepTicks240Hz * 3UL), actual: pump.ElapsedTicks);

        var previousElapsedTicksBeforeRateChange = pump.ElapsedTicks; // 630

        // What the PRE-FIX formula would have computed for the next call's first tick: previousElapsedTicks (630) /
        // NEW stepTicks (420) = 1 — colliding with the tick already assigned to the second step above. Recorded here
        // as the oracle this test refutes, not as code under test.
        var legacyBuggyFirstTick = (previousElapsedTicksBeforeRateChange / StepTicks120Hz);

        Assert.Equal(actual: legacyBuggyFirstTick, expected: 1UL); // sanity: the bug is real for these inputs

        // Second call: rate changes to 120 Hz mid-life. 2 steps' worth of wall time is due.
        pump.Advance(deltaTicks: (StepTicks120Hz * 2UL), maxFrameTicks: 10_000UL, stepTicks: StepTicks120Hz);

        Assert.Equal(expected: 5, actual: simulation.Steps.Count);

        var fourthStep = simulation.Steps[3];
        var fifthStep = simulation.Steps[4];

        // The fixed pump: ticks continue 3, 4 — no reuse of the 1, 2 already assigned to the FIRST call's steps,
        // unlike the legacy formula's collision computed above.
        Assert.Equal(expected: 3UL, actual: fourthStep.Tick);
        Assert.Equal(expected: 4UL, actual: fifthStep.Tick);
        Assert.NotEqual(expected: legacyBuggyFirstTick, actual: fourthStep.Tick);

        // ElapsedTicks is the true running total (previous 630 + N * new stepTicks), never re-derived from
        // tick * stepTicks (which would be wrong the instant stepTicks differs across the boundary).
        Assert.Equal(expected: (previousElapsedTicksBeforeRateChange + StepTicks120Hz), actual: fourthStep.ElapsedTicks);
        Assert.Equal(expected: (previousElapsedTicksBeforeRateChange + (2UL * StepTicks120Hz)), actual: fifthStep.ElapsedTicks);
        Assert.Equal(expected: StepTicks120Hz, actual: fourthStep.StepTicks);
        Assert.Equal(expected: StepTicks120Hz, actual: fifthStep.StepTicks);

        Assert.Equal(expected: (previousElapsedTicksBeforeRateChange + (2UL * StepTicks120Hz)), actual: pump.ElapsedTicks);

        // Every Tick across the whole run is unique — the load-bearing property a downstream tick-keyed consumer
        // (InputRouter.SnapshotForTick's stamp, a replay tape, a grant expiration) relies on.
        var distinctTicks = new HashSet<ulong>();

        foreach (var step in simulation.Steps) {
            Assert.True(condition: distinctTicks.Add(item: step.Tick), userMessage: $"tick {step.Tick} was assigned to more than one step");
        }
    }
}
