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

    // Admitted heavy holds share the lease's slots, each recording its kind and device for other processes' admission; an
    // exclusive hold and a quiet window wait for every slot to drain, and a shared hold waits while an exclusive one runs.
    [Fact]
    public async Task AdmittedHeavyHoldsShareTheLeaseWhileExclusiveAndQuietHoldsWaitForAllToDrain() {
        HostAdmit admit = (_, _, _, _, _, _, _, _, _, _) => Task.FromResult(true);
        Task<CliLease> Take(string lane, TimeSpan wait, bool exclusive = false, bool device = false) => LeasedAdmission.AcquireAsync(m_root, "heavy", lane + " run", lane, admit,
            m_root, lane, device, true, HostStartKind.ReproduceReplica, TimeProvider.System, wait, TextWriter.Null, null, Token, exclusive);

        using (var a = await Take("a", TimeSpan.Zero))
        using (var b = await Take("b", TimeSpan.Zero, device: true)) {
            Assert.Equal(["a run", "b run"], CliLease.Inspect(m_root, "heavy", DateTimeOffset.UtcNow).Holders.Select(held => held.Holder));
            Assert.Equal([("heavy", "a run", HostStartKind.ReproduceReplica, false), ("heavy", "b run", HostStartKind.ReproduceReplica, true)],
                CliLease.AdmittedHolds(m_root, DateTimeOffset.UtcNow).Select(hold => (hold.Lease, hold.Holder, hold.Kind, hold.Device)));
            await Assert.ThrowsAsync<TimeoutException>(() => Take("c", TimeSpan.Zero, exclusive: true));
            await Assert.ThrowsAsync<TimeoutException>(() => CliLease.AcquireQuietAsync(m_root, "measurement", TimeSpan.Zero, TimeProvider.System, m_root, Token));
        }
        using (var alone = await Take("c", TimeSpan.Zero, exclusive: true)) {
            Assert.True(Assert.Single(CliLease.Inspect(m_root, "heavy", DateTimeOffset.UtcNow).Holders).Exclusive);
            await Assert.ThrowsAsync<TimeoutException>(() => Take("d", TimeSpan.Zero));
        }
        // A heavy hold without an admission has nothing measuring whether a second fits, so it runs alone.
        using (var unmeasured = await CliLease.AcquireAsync(m_root, "heavy", "e run", TimeSpan.Zero, TimeProvider.System, m_root, Token)) {
            await Assert.ThrowsAsync<TimeoutException>(() => Take("d", TimeSpan.Zero));
        }
        using var d = await Take("d", TimeSpan.Zero);
    }

    public void Dispose() => ConfinedDirectory.RemoveTree(m_root);
}
