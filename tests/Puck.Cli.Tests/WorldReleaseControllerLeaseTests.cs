using Puck.Cli.Azure;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class WorldReleaseControllerLeaseTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExpiredControllerCannotPerformMoreEffectsOrReleaseItsSuccessor() {
        var owner = new Ownership();
        var stale = await WorldReleaseControllerLease.AcquireAsync(
            new Backend(owner: owner),
            Token
        );

        owner.Current = Guid.Empty; // Provider expiry or administrative break.
        await using var successor = await WorldReleaseControllerLease.AcquireAsync(
            new Backend(owner: owner),
            Token
        );

        await Assert.ThrowsAsync<IOException>(testCode: () => stale.EnsureHeldAsync());
        Assert.True(stale.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => stale.EnsureHeldAsync());
        await stale.DisposeAsync();
        Assert.NotNull(stale.ReleaseFailure);
        await successor.EnsureHeldAsync();
    }
    [Fact]
    public async Task OnlyOneControllerOwnsTheGroupAndDisposeAllowsItsSuccessor() {
        var owner = new Ownership();
        var first = await WorldReleaseControllerLease.AcquireAsync(
            new Backend(owner: owner),
            Token
        );

        await first.EnsureHeldAsync();
        await Assert.ThrowsAsync<IOException>(() => WorldReleaseControllerLease.AcquireAsync(
            new Backend(owner: owner),
            Token
        ));
        await first.DisposeAsync();
        Assert.True(first.Token.IsCancellationRequested);
        await using var successor = await WorldReleaseControllerLease.AcquireAsync(
            new Backend(owner: owner),
            Token
        );

        await successor.EnsureHeldAsync();
    }
    [Fact]
    public async Task UserCancellationClosesTheEffectBoundary() {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        await using var lease = await WorldReleaseControllerLease.AcquireAsync(
            new Backend(owner: new Ownership()),
            cancelled.Token
        );

        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => lease.EnsureHeldAsync());
    }

    private sealed class Ownership { public Guid Current { get; set; } }
    private sealed class Backend(Ownership owner) : IWorldReleaseControllerLeaseBackend {
        private readonly Guid m_id = Guid.NewGuid();

        public Task AcquireAsync(CancellationToken cancellationToken) {
            if (owner.Current != Guid.Empty) { throw new IOException(message: "already leased"); }
            owner.Current = m_id; return Task.CompletedTask;
        }
        public Task ReleaseAsync(CancellationToken cancellationToken) {
            if (owner.Current != m_id) { throw new IOException(message: "cannot release a different lease"); }
            owner.Current = Guid.Empty; return Task.CompletedTask;
        }
        public Task RenewAsync(CancellationToken cancellationToken) {
            if (owner.Current != m_id) { throw new IOException(message: "lost lease"); }
            return Task.CompletedTask;
        }
    }
}
