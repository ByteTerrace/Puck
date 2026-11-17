using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The bake pool's backlog (<see cref="SdfMeshBakeBacklog"/>) is wall time on both sides: what the workers take to drain
/// the pool's stages against what a new admission takes to reach them. So the same queue admits or waits by how long
/// the lead is, not by when the take happened to read it.
/// </summary>
public sealed class SdfMeshBakeBacklogLawTests {
    private const int Workers = 16;

    [Fact]
    public void UnmeasuredTheBacklogIsARoundOfTheWorkers() {
        var backlog = new SdfMeshBakeBacklog();

        Assert.Equal(expected: Workers, actual: backlog.Round(workers: Workers));
        Assert.False(condition: backlog.Holds(stages: (Workers - 1), workers: Workers));
        Assert.True(condition: backlog.Holds(stages: Workers, workers: Workers));
    }
    [Fact]
    public void ALongerLeadAdmitsWhatTheWorkersDrainBeforeTheAdmissionArrives() {
        var fast = new SdfMeshBakeBacklog();
        var slow = new SdfMeshBakeBacklog();

        // The same pool on two hosts: stages of five milliseconds, and an admission's GPU rounds answered within ten
        // on a host that services them as they complete, or a hundred on one that takes them a slow frame apart.
        for (var sample = 0; (sample < 64); sample++) {
            fast.ObserveStage(seconds: 0.005);
            slow.ObserveStage(seconds: 0.005);
            fast.ObserveLead(seconds: 0.010);
            slow.ObserveLead(seconds: 0.100);
        }

        // In ten milliseconds each worker finishes two stages, in a hundred twenty: that much may stand queued or on
        // its way before a group admitted now would only wait.
        Assert.Equal(expected: (2 * Workers), actual: fast.Round(workers: Workers));
        Assert.Equal(expected: (SdfMeshBakeBacklog.MostRounds * Workers), actual: slow.Round(workers: Workers));
        // A queue of three rounds right after a burst of answers: the fast host waits, and the slow one admits, since
        // its workers drain that long before the next admission's stage arrives. A count read at the take would have
        // held both, and left the slow host's workers idle until its next frame.
        Assert.True(condition: fast.Holds(stages: (3 * Workers), workers: Workers));
        Assert.False(condition: slow.Holds(stages: (3 * Workers), workers: Workers));
        // Groups admitted and still on their way count as stages, so a take cannot admit past the round in one go.
        Assert.True(condition: slow.Holds(stages: ((3 * Workers) + (13 * Workers)), workers: Workers));
    }
    [Fact]
    public void SlowerStagesHoldAdmissionSoonerAndNeverBelowARound() {
        var backlog = new SdfMeshBakeBacklog();

        for (var sample = 0; (sample < 64); sample++) {
            backlog.ObserveStage(seconds: 0.200);
            backlog.ObserveLead(seconds: 0.010);
        }

        // Stages twenty times an admission's lead: the workers stay busy past it with a round queued, and a round is
        // the least the pool is given.
        Assert.Equal(expected: Workers, actual: backlog.Round(workers: Workers));
        Assert.True(condition: backlog.Holds(stages: Workers, workers: Workers));
        Assert.False(condition: backlog.Holds(stages: (Workers - 1), workers: Workers));
    }
}
