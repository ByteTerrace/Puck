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

    // Admitted heavy and gpu holds share their lease's slots, each recording its profile and device for other processes'
    // admission; an exclusive hold (a timing leg) and a quiet window wait for every slot to drain, and a shared hold waits
    // while an exclusive one runs.
    [InlineData("heavy")]
    [InlineData("gpu")]
    [Theory]
    public async Task AdmittedHoldsShareTheLeaseWhileExclusiveAndQuietHoldsWaitForAllToDrain(string resource) {
        var gpu = (resource == "gpu");
        HostAdmit admit = (_, _, _, _, _, _, _, _, _, _) => Task.FromResult(true);
        Task<CliLease> Take(string lane, TimeSpan wait, bool exclusive = false, bool device = false) => LeasedAdmission.AcquireAsync(m_root, resource, lane + " run", lane, admit,
            m_root, lane, (gpu || device), !gpu, new HostStartProfile(HostStartKind.ReproduceReplica, Device: "vulkan", Store: StoreWarmth.Cold), TimeProvider.System, wait, TextWriter.Null, null, Token, exclusive);

        using (var a = await Take("a", TimeSpan.Zero))
        using (var b = await Take("b", TimeSpan.Zero, device: true)) {
            Assert.Equal(["a run", "b run"], CliLease.Inspect(m_root, resource, DateTimeOffset.UtcNow).Holders.Select(held => held.Holder));
            Assert.Equal([(resource, "a run", HostStartKind.ReproduceReplica, gpu), (resource, "b run", HostStartKind.ReproduceReplica, true)],
                CliLease.AdmittedHolds(m_root, DateTimeOffset.UtcNow).Select(hold => (hold.Lease, hold.Holder, hold.Kind, hold.Device)));
            await Assert.ThrowsAsync<TimeoutException>(() => Take("c", TimeSpan.Zero, exclusive: true));
            await Assert.ThrowsAsync<TimeoutException>(() => CliLease.AcquireQuietAsync(m_root, "measurement", TimeSpan.Zero, TimeProvider.System, m_root, Token));
        }
        using (var alone = await Take("c", TimeSpan.Zero, exclusive: true)) {
            Assert.True(Assert.Single(CliLease.Inspect(m_root, resource, DateTimeOffset.UtcNow).Holders).Exclusive);
            await Assert.ThrowsAsync<TimeoutException>(() => Take("d", TimeSpan.Zero));
        }
        // A hold without an admission has nothing measuring whether a second fits, so it runs alone.
        using (var unmeasured = await CliLease.AcquireAsync(m_root, resource, "e run", TimeSpan.Zero, TimeProvider.System, m_root, Token)) {
            await Assert.ThrowsAsync<TimeoutException>(() => Take("d", TimeSpan.Zero));
        }
        using var d = await Take("d", TimeSpan.Zero);
    }
    // A hold records its whole profile (kind, backend and mesh store) beside its holder, so another process's admission
    // judges the work it covers by what that profile has measured.
    [Fact]
    public async Task AHoldRecordsItsProfileForOtherProcessesAdmission() {
        HostAdmit admit = (_, _, _, _, _, _, _, _, _, _) => Task.FromResult(true);
        var cold = new HostStartProfile(HostStartKind.WindowedWorld, Device: "directx", Store: StoreWarmth.Cold);

        using var hold = await LeasedAdmission.AcquireAsync(m_root, "gpu", "a run", "a", admit, m_root, "a", true, false, cold, TimeProvider.System, TimeSpan.Zero, TextWriter.Null, null, Token);

        Assert.Equal(cold, Assert.Single(CliLease.AdmittedHolds(m_root, DateTimeOffset.UtcNow)).Profile);
        Assert.Equal("WindowedWorld.directx.cold", cold.Name);
    }
    [InlineData("WindowedWorld", HostStartKind.WindowedWorld, null, StoreWarmth.Warm)]
    [InlineData("WindowedWorld.vulkan", HostStartKind.WindowedWorld, "vulkan", StoreWarmth.Warm)]
    [InlineData("WindowedWorld.cold", HostStartKind.WindowedWorld, null, StoreWarmth.Cold)]
    [InlineData("WindowedWorld.directx.cold", HostStartKind.WindowedWorld, "directx", StoreWarmth.Cold)]
    [InlineData("HeavySuite", HostStartKind.HeavySuite, null, StoreWarmth.Warm)]
    [Theory]
    public void AProfileNameRoundTripsAndOnlyAWindowedWorldKeepsItsDeviceAndStore(string name, HostStartKind kind, string? device, StoreWarmth store) {
        Assert.True(HostStartProfile.TryParse(name, out var parsed));
        Assert.Equal(new HostStartProfile(kind, device, store), parsed);
        Assert.Equal(name, parsed.Name);
        // The other kinds measure neither dimension, so a start that names them is the same profile.
        Assert.Equal(new HostStartProfile(HostStartKind.HeavySuite).Name, new HostStartProfile(HostStartKind.HeavySuite, "vulkan", StoreWarmth.Cold).Name);
    }
    [InlineData("")]
    [InlineData("Nonsense")]
    [InlineData("99")]
    [InlineData("WindowedWorld.cold.vulkan")]
    [InlineData("WindowedWorld.a.b")]
    [InlineData("WindowedWorld.VULKAN")]
    [Theory]
    public void AnUnrecognizedProfileNameIsRefused(string name) => Assert.False(HostStartProfile.TryParse(name, out _));

    public void Dispose() => ConfinedDirectory.RemoveTree(m_root);
}
