using Puck.Cli.Azure;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Release.Tests;

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

        var lost = await Assert.ThrowsAsync<WorldReleaseControllerLeaseLostException>(testCode: () => stale.EnsureHeldAsync());

        Assert.Contains(expectedSubstring: "no longer this controller's", actualString: lost.Message);
        Assert.True(condition: stale.Token.IsCancellationRequested);
        await Assert.ThrowsAsync<WorldReleaseControllerLeaseLostException>(testCode: () => stale.EnsureHeldAsync());
        await stale.DisposeAsync();
        Assert.NotNull(@object: stale.ReleaseFailure);
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
        await Assert.ThrowsAsync<IOException>(testCode: () => WorldReleaseControllerLease.AcquireAsync(
            new Backend(owner: owner),
            Token
        ));
        await first.DisposeAsync();
        Assert.True(condition: first.Token.IsCancellationRequested);
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
    /// <summary>A renewal the provider never answers leaves the confirmation from acquisition standing, so the
    /// controller's effects end exactly <see cref="WorldReleaseControllerLease.Lifetime"/> after it on the clock, and the
    /// work they cancel leaves naming that deadline rather than as a bare cancellation.</summary>
    [Fact]
    public async Task StalledRenewalEndsEffectsExactlyAtTheLocalDeadline() {
        var clock = new VirtualClock();
        var backend = new StallingBackend { StallRenewal = true };
        await using var lease = await WorldReleaseControllerLease.AcquireAsync(
            backend: backend,
            cancellationToken: Token,
            clock: clock
        );
        var running = lease.RunAsync(action: async token => {
            await Task.Delay(
                cancellationToken: token,
                delay: Timeout.InfiniteTimeSpan
            );
            return 0;
        });

        clock.Advance(by: WorldReleaseControllerLease.RenewalPeriod);
        await backend.RenewalEntered.Task.WaitAsync(cancellationToken: Token);
        clock.Advance(by: ((WorldReleaseControllerLease.Lifetime - WorldReleaseControllerLease.RenewalPeriod) - TimeSpan.FromTicks(value: 1)));
        Assert.False(condition: lease.Token.IsCancellationRequested);
        clock.Advance(by: TimeSpan.FromTicks(value: 1));
        Assert.True(condition: lease.Token.IsCancellationRequested);
        var lost = await Assert.ThrowsAsync<WorldReleaseControllerLeaseLostException>(testCode: () => running);

        Assert.Contains(expectedSubstring: "no lease renewal was confirmed", actualString: lost.Message);
    }
    /// <summary>A renewal attempt that goes unanswered is abandoned after
    /// <see cref="WorldReleaseControllerLease.RenewalAttemptTimeout"/> and tried again on a fresh request, and the
    /// confirmed retry keeps the lease past the deadline the acquisition alone allowed.</summary>
    [Fact]
    public async Task UnansweredRenewalAttemptIsRetriedWhileTheLeaseIsHeld() {
        var clock = new VirtualClock();
        var backend = new StallingBackend { StallFirstRenewalOnly = true };
        await using var lease = await WorldReleaseControllerLease.AcquireAsync(
            backend: backend,
            cancellationToken: Token,
            clock: clock
        );

        clock.Advance(by: WorldReleaseControllerLease.RenewalPeriod);
        await backend.RenewalEntered.Task.WaitAsync(cancellationToken: Token);
        await clock.WhenArmedAsync(
            count: 1,
            ct: Token,
            dueTime: WorldReleaseControllerLease.RenewalAttemptTimeout
        );
        clock.Advance(by: WorldReleaseControllerLease.RenewalAttemptTimeout);
        await clock.WhenArmedAsync(
            count: 1,
            ct: Token,
            dueTime: WorldReleaseControllerLease.RenewalRetryDelay
        );
        clock.Advance(by: WorldReleaseControllerLease.RenewalRetryDelay);
        await backend.Renewed.Task.WaitAsync(cancellationToken: Token);
        // The check waits for the loop's confirmation, so the deadline below counts from the retried one.
        await lease.EnsureHeldAsync();
        clock.Advance(by: (((WorldReleaseControllerLease.Lifetime - WorldReleaseControllerLease.RenewalPeriod) - WorldReleaseControllerLease.RenewalAttemptTimeout) - WorldReleaseControllerLease.RenewalRetryDelay));
        Assert.False(condition: lease.Token.IsCancellationRequested);
        Assert.Null(@object: lease.LossReason);
    }
    /// <summary>An acquisition that took the whole local lifetime on the clock may already have lost the lease, so it
    /// is refused; one tick less is held.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquisitionThatSpendsTheLifetimeIsRefused(bool spendsIt) {
        var clock = new VirtualClock();
        var spent = (spendsIt
            ? WorldReleaseControllerLease.Lifetime
            : (WorldReleaseControllerLease.Lifetime - TimeSpan.FromTicks(value: 1)));
        var backend = new StallingBackend { OnAcquire = () => clock.Advance(by: spent) };

        if (spendsIt) {
            await Assert.ThrowsAsync<IOException>(testCode: () => WorldReleaseControllerLease.AcquireAsync(
                backend: backend,
                cancellationToken: Token,
                clock: clock
            ));
        } else {
            await using var lease = await WorldReleaseControllerLease.AcquireAsync(
                backend: backend,
                cancellationToken: Token,
                clock: clock
            );

            Assert.False(condition: lease.Token.IsCancellationRequested);
        }
    }
    /// <summary>Disposal stops waiting for a release the provider never answers when
    /// <see cref="WorldReleaseControllerLease.ReleaseTimeout"/> expires on the clock, and reports it.</summary>
    [Fact]
    public async Task StalledReleaseEndsAtItsTimeout() {
        var clock = new VirtualClock();
        var backend = new StallingBackend { StallRelease = true };
        var lease = await WorldReleaseControllerLease.AcquireAsync(
            backend: backend,
            cancellationToken: Token,
            clock: clock
        );
        var disposed = lease.DisposeAsync().AsTask();

        await backend.ReleaseEntered.Task.WaitAsync(cancellationToken: Token);
        await clock.ExpireAsync(
            ct: Token,
            dueTime: WorldReleaseControllerLease.ReleaseTimeout,
            pending: disposed
        );
        await disposed;
        Assert.IsAssignableFrom<OperationCanceledException>(@object: lease.ReleaseFailure);
    }

    private sealed class StallingBackend : IWorldReleaseControllerLeaseBackend {
        public Action? OnAcquire { get; init; }

        public TaskCompletionSource ReleaseEntered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RenewalEntered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Renewed { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public bool StallFirstRenewalOnly { get; init; }
        public bool StallRelease { get; init; }
        public bool StallRenewal { get; init; }

        private int m_renewals;

        public Task AcquireAsync(CancellationToken cancellationToken) { OnAcquire?.Invoke(); return Task.CompletedTask; }
        public Task ReleaseAsync(CancellationToken cancellationToken) {
            ReleaseEntered.TrySetResult();
            return (StallRelease
                ? Task.Delay(
                    cancellationToken: cancellationToken,
                    delay: Timeout.InfiniteTimeSpan
                )
                : Task.CompletedTask);
        }
        public Task RenewAsync(CancellationToken cancellationToken) {
            RenewalEntered.TrySetResult();
            if (StallRenewal || (StallFirstRenewalOnly && (Interlocked.Increment(location: ref m_renewals) == 1))) {
                return Task.Delay(
                    cancellationToken: cancellationToken,
                    delay: Timeout.InfiniteTimeSpan
                );
            }
            Renewed.TrySetResult();
            return Task.CompletedTask;
        }
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
            if (owner.Current != m_id) {
                throw new WorldReleaseControllerLeaseLostException(
                    innerException: new IOException(message: "lost lease"),
                    message: "lost lease"
                );
            }
            return Task.CompletedTask;
        }
    }
}
