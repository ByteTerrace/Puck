using Puck.Cli.Azure;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class WorldReleaseControllerLeaseTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyOneControllerOwnsTheGroupAndDisposeAllowsItsSuccessor() {
        var owner = new Ownership();
        var first = await WorldReleaseControllerLease.AcquireAsync(new Backend(owner), Token);
        await first.EnsureHeldAsync();
        await Assert.ThrowsAsync<IOException>(() => WorldReleaseControllerLease.AcquireAsync(new Backend(owner), Token));
        await first.DisposeAsync();
        Assert.True(first.Token.IsCancellationRequested);
        await using var successor = await WorldReleaseControllerLease.AcquireAsync(new Backend(owner), Token);
        await successor.EnsureHeldAsync();
    }

    [Fact]
    public async Task ExpiredControllerCannotPerformMoreEffectsOrReleaseItsSuccessor() {
        var owner = new Ownership();
        var stale = await WorldReleaseControllerLease.AcquireAsync(new Backend(owner), Token);
        owner.Current = Guid.Empty; // Provider expiry or administrative break.
        await using var successor = await WorldReleaseControllerLease.AcquireAsync(new Backend(owner), Token);
        await Assert.ThrowsAsync<IOException>(() => stale.EnsureHeldAsync());
        Assert.True(stale.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale.EnsureHeldAsync());
        await stale.DisposeAsync();
        Assert.NotNull(stale.ReleaseFailure);
        await successor.EnsureHeldAsync();
    }

    [Fact]
    public async Task UserCancellationClosesTheEffectBoundary() {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using var lease = await WorldReleaseControllerLease.AcquireAsync(new Backend(new Ownership()), cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.EnsureHeldAsync());
    }

    private sealed class Ownership { public Guid Current { get; set; } }
    private sealed class Backend(Ownership owner) : IWorldReleaseControllerLeaseBackend {
        private readonly Guid m_id = Guid.NewGuid();
        public Task AcquireAsync(CancellationToken cancellationToken) {
            if (owner.Current != Guid.Empty) { throw new IOException("already leased"); }
            owner.Current = m_id; return Task.CompletedTask;
        }
        public Task RenewAsync(CancellationToken cancellationToken) {
            if (owner.Current != m_id) { throw new IOException("lost lease"); }
            return Task.CompletedTask;
        }
        public Task ReleaseAsync(CancellationToken cancellationToken) {
            if (owner.Current != m_id) { throw new IOException("cannot release a different lease"); }
            owner.Current = Guid.Empty; return Task.CompletedTask;
        }
    }
}
