using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Crossing logs that answer late, as a store across a network does. Neither authority waits for an answer:
/// each step's record is in flight while the authorities keep stepping, and the step resumes at a tick boundary once
/// its record is durable. Two invariants hold throughout: an arrival lands only once its record is durable, and each
/// authority's crossing sequence stays dense, with at most one record in flight per authority and a refused record's
/// sequence taken again by the next. The red legs are a destination that lands the cohort while its arrival record is
/// in flight, and an authority that sends a second record behind one whose fate is unknown, which leaves a gap when the
/// first is refused.</summary>
public sealed class CrossingInFlightLawTests {
    // Generous: the colocated source learns its commit's answer through a task the thread pool completes.
    private const int StepBudget = 600;

    // Drives the crossing until the traveler has landed at the destination and the source has settled it, answering
    // each deferred append through `answer` and checking at every step that a landed traveler's arrival is durable.
    private static void Drive(CrossingWorld world, Func<MemoryCrossingStore, WorldCrossingDurability> answer) {
        for (var step = 0; (step < StepBudget); step++) {
            if (world.TravelerAt(row: world.Destination)) {
                Assert.Contains(
                    collection: world.DestinationLog.Read(),
                    filter: static entry => (entry.Record is WorldCrossingRecord.Arrival)
                );
                if (world.SourceLog.Read().Any(predicate: static entry => entry.Record is WorldCrossingRecord.Settlement)) {
                    return;
                }
            }
            foreach (var log in new[] { world.SourceLog, world.DestinationLog }) {
                if (log.DeferredRecord is not null) {
                    _ = log.AnswerDeferred(durability: answer(arg: log));
                }
            }
            world.Step(ticks: 1);
            Thread.Sleep(millisecondsTimeout: 1);
        }
        Assert.Fail(message: $"the crossing did not settle within {StepBudget} steps");
    }
    private static void AssertDense(MemoryCrossingStore log) {
        var sequences = log.Read().Select(selector: static entry => entry.Sequence).ToArray();

        Assert.Equal(
            expected: Enumerable.Range(start: 0, count: sequences.Length).Select(selector: static index => (ulong)index),
            actual: sequences
        );
        Assert.Equal(expected: 0, actual: log.Overlaps);
    }

    // THE LAW: an arrival whose record is in flight lands nothing. The destination holds the cohort's slots under its
    // lease while the source holds the detached cohort in doubt, so for as long as the record goes unanswered the
    // traveler is embodied nowhere; once the record is durable the destination lands it at the next tick boundary, and
    // the source settles it. Every log stays dense and never takes a second record while one is in flight.
    [Fact]
    public void AnArrivalLandsOnlyOnceItsRecordIsDurable() {
        using var world = CrossingWorld.Build();
        var held = false;

        world.SourceLog.Defer = true;
        world.DestinationLog.Defer = true;
        _ = world.Cross();
        Drive(world: world, answer: log => {
            if (!held && (log.DeferredRecord is WorldCrossingRecord.Arrival)) {
                held = true;
                // The arrival record stays unanswered across several ticks: nothing lands, and the source answers no one.
                world.Step(ticks: 4);
                Assert.False(condition: world.TravelerAt(row: world.Destination));
                Assert.Equal(expected: 0, actual: world.CountTraveler());
                Assert.IsType<WorldCrossingRecord.Arrival>(@object: log.DeferredRecord);
            }
            return WorldCrossingDurability.Durable;
        });

        Assert.True(condition: held, userMessage: "the destination never sent its arrival record");
        Assert.Equal(expected: 1, actual: world.CountTraveler());
        Assert.True(condition: world.TravelerAt(row: world.Destination));
        AssertDense(log: world.SourceLog);
        AssertDense(log: world.DestinationLog);
    }

    // THE LAW: a record the log refuses frees its sequence. The destination's lease record is refused once while in
    // flight; the reservation is refused as transient, the source sends it again, and the lease record that lands takes
    // the refused one's sequence, so the log has no gap and the crossing completes.
    [Fact]
    public void ARefusedRecordFreesItsSequenceForTheRetry() {
        using var world = CrossingWorld.Build();
        var refused = (ulong?)null;

        world.SourceLog.Defer = true;
        world.DestinationLog.Defer = true;
        _ = world.Cross();
        Drive(world: world, answer: log => {
            if ((refused is null) && (log == world.DestinationLog) && (log.DeferredRecord is WorldCrossingRecord.Lease)) {
                refused = log.Sent[^1];
                return WorldCrossingDurability.Refused;
            }
            return WorldCrossingDurability.Durable;
        });

        Assert.NotNull(@object: refused);
        Assert.Equal(
            expected: 2,
            actual: world.DestinationLog.Sent.Count(predicate: sequence => (sequence == refused))
        );
        Assert.IsType<WorldCrossingRecord.Lease>(@object: world.DestinationLog.Read().Single(predicate: entry => (entry.Sequence == refused)).Record);
        Assert.Equal(expected: 1, actual: world.CountTraveler());
        AssertDense(log: world.SourceLog);
        AssertDense(log: world.DestinationLog);
    }
}
