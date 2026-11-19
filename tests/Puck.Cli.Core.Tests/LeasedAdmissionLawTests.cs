using Puck.Confinement;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Core.Tests;

public sealed class LeasedAdmissionLawTests : IDisposable {
    private readonly string m_root = Path.Combine(Path.GetTempPath(), "lease1-admission-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("build", false)]
    [InlineData("gpu", false)]
    [InlineData("heavy", false)]
    [InlineData("build", true)]
    [InlineData("gpu", true)]
    [InlineData("heavy", true)]
    public async Task AdmissionWaitHoldsNoResourceAndRetainsItsTicketAndTurn(string resource, bool loseHeadroom) {
        var clock = new VirtualClock();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clear = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preflights = 0;
        var reservations = 0;
        HostAdmit admit = async (_, _, _, _, _, bound, reserve, _, _, token) => {
            if (reserve) {
                Assert.Equal(TimeSpan.Zero, bound);
                Assert.True(CliLease.Inspect(m_root, resource, clock.GetUtcNow()).HolderAlive);
                return !loseHeadroom || ++reservations > 1;
            }
            Assert.False(CliLease.Inspect(m_root, resource, clock.GetUtcNow()).HolderAlive);
            if (loseHeadroom && ++preflights == 1) { return true; }
            blocked.SetResult();
            return await clear.Task.WaitAsync(token);
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = LeasedAdmission.AcquireAsync(m_root, resource, "a first", "a", admit, m_root, "first",
            resource == "gpu", resource == "heavy", HostStartKind.Step, clock, TimeSpan.FromMinutes(1), TextWriter.Null, null, cancel.Token);
        await blocked.Task.WaitAsync(Token);
        var ticket = Assert.Single(CliLease.Inspect(m_root, resource, clock.GetUtcNow()).Queue).Id;
        using (var second = await CliLease.AcquireAsync(m_root, resource, "b run", TimeSpan.Zero, clock, m_root, Token, lane: "b")) {
            Assert.Equal("b run", CliLease.Inspect(m_root, resource, clock.GetUtcNow()).Holder);
        }
        Assert.Equal(ticket, Assert.Single(CliLease.Inspect(m_root, resource, clock.GetUtcNow()).Queue).Id);

        // b queues another turn and a queues a younger ticket. Clearing admission must restore a's
        // original oldest ticket ahead of both, without a lost reservation counting as service.
        using var blocker = await CliLease.AcquireAsync(m_root, resource, "c run", TimeSpan.Zero, clock, m_root, Token, lane: "c");
        var later = CliLease.AcquireAsync(m_root, resource, "b later", TimeSpan.FromMinutes(1), clock, m_root, cancel.Token, lane: "b");
        var younger = CliLease.AcquireAsync(m_root, resource, "a younger", TimeSpan.FromMinutes(1), clock, m_root, cancel.Token, lane: "a");
        clear.SetResult(true);
        try {
            await clock.WhenArmedAsync(count: 3, ct: Token, dueTime: TimeSpan.FromMilliseconds(100));
            blocker.Dispose();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            using var resumed = await first.WaitAsync(Token);
            Assert.False(later.IsCompleted);
            Assert.False(younger.IsCompleted);
            Assert.Equal("a first", CliLease.Inspect(m_root, resource, clock.GetUtcNow()).Holder);
        } finally {
            cancel.Cancel();
            try { using var abandoned = await later; } catch (OperationCanceledException) { }
            try { using var abandoned = await younger; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CancellationAndFailedReservationReleaseTheGuardAndRemoveBlockedTickets() {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        HostAdmit admit = (_, _, _, _, _, _, reserve, _, _, _) => reserve ? throw new OperationCanceledException(cancel.Token) : Task.FromResult(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => LeasedAdmission.AcquireAsync(m_root, "heavy", "a", "a", admit,
            m_root, "law", false, true, HostStartKind.Step, TimeProvider.System, TimeSpan.Zero, TextWriter.Null, null, cancel.Token));
        Assert.False(CliLease.Inspect(m_root, "heavy", DateTimeOffset.UtcNow).HolderAlive);
        Assert.Empty(CliLease.Inspect(m_root, "heavy", DateTimeOffset.UtcNow).Queue);
        using var next = await CliLease.AcquireAsync(m_root, "heavy", "b", TimeSpan.Zero, TimeProvider.System, m_root, Token);
    }

    public void Dispose() => ConfinedDirectory.RemoveTree(m_root);
}
