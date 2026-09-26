using Puck.Abstractions.Gpu;

namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for <see cref="GpuBuildCache{TKey, T}"/>, the one leased-build mechanism under the pass-pipeline cache and the
/// SDF engine's pipeline sets: leases on one device and key share one value built once, and another device or key has
/// its own; the last release disposes the value and a later lease builds anew, which is how a device loss empties the
/// device's entries; a holder's own pool build waits for an entry; the last release cancels a build still running and
/// waits only for the creation in the driver, and a wait racing its own lease's release throws without building again; a
/// failed build rethrows once and the next poll builds afresh; and a lone holder can take its entry out of sharing.
/// Counted, not timed: the build delegate counts its builds, and a gate holds one in the driver.
/// </summary>
public sealed class GpuBuildCacheLawTests {
    [Fact]
    public void LeasesOnOneDeviceAndKeyShareOneValueAndTheLastReleaseDisposesIt() {
        var builds = new Builds();
        var cache = builds.Cache();
        var device = new StubDevice();
        var other = new StubDevice();
        var first = cache.Acquire(device: device, key: "a");
        var second = cache.Acquire(device: device, key: "a");
        var otherKey = cache.Acquire(device: device, key: "b");
        var otherDevice = cache.Acquire(device: other, key: "a");
        var value = Ready(lease: first);

        Assert.Same(expected: value, actual: Ready(lease: second));
        Assert.NotSame(expected: value, actual: Ready(lease: otherKey));
        Assert.NotSame(expected: value, actual: Ready(lease: otherDevice));
        Assert.Equal(expected: (3, 3), actual: (builds.Count, cache.SharedEntries));

        first.Release();
        first.Release();
        Assert.False(condition: value.IsDisposed);
        Assert.Null(@object: first.Current);
        _ = Assert.Throws<ObjectDisposedException>(testCode: () => first.Poll());

        // Every holder on a lost device releases, which empties the device's entries; the next lease builds afresh.
        second.Release();
        Assert.True(condition: value.IsDisposed);
        Assert.Equal(expected: 2, actual: cache.SharedEntries);

        var rebuilt = cache.Acquire(device: device, key: "a");

        Assert.NotSame(expected: value, actual: Ready(lease: rebuilt));
        Assert.Equal(expected: 4, actual: builds.Count);

        foreach (var lease in ((GpuBuildLease<string, Built>[])[rebuilt, otherKey, otherDevice])) {
            lease.Release();
        }

        Assert.Equal(expected: 0, actual: cache.SharedEntries);
    }
    [Fact]
    public async Task AHoldersOwnPoolBuildWaitsForTheEntry() {
        var builds = new Builds();
        var cache = builds.Cache();
        var device = new StubDevice();
        var lease = cache.Acquire(device: device, key: "a");
        var waited = await Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: () => lease.Wait(cancellationToken: TestContext.Current.CancellationToken)
        ).WaitAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromSeconds(value: 30)
        );

        Assert.Same(expected: waited, actual: lease.Poll());
        Assert.Equal(expected: 1, actual: builds.Count);
        lease.Release();
        Assert.True(condition: waited.IsDisposed);
    }
    [Fact]
    public void AWaitEndsOnItsTokenWhileTheEntryKeepsBuildingForItsOtherHolders() {
        using var gate = new ManualResetEventSlim();
        var builds = new Builds(gate: gate);
        var cache = builds.Cache();
        var device = new StubDevice();
        var waiting = cache.Acquire(device: device, key: "a");
        var holding = cache.Acquire(device: device, key: "a");
        using var cancel = new CancellationTokenSource();

        Assert.True(condition: builds.Entered.Wait(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        cancel.Cancel();
        _ = Assert.Throws<OperationCanceledException>(testCode: () => waiting.Wait(cancellationToken: cancel.Token));
        waiting.Release();
        gate.Set();

        Assert.Equal(expected: 1, actual: Ready(lease: holding).Serial);
        Assert.Equal(expected: 1, actual: builds.Count);
        holding.Release();
    }
    [Fact]
    public void TheLastReleaseCancelsABuildInTheDriverAndWaitsOnlyForThatCreation() {
        using var gate = new ManualResetEventSlim();
        var builds = new Builds(gate: gate);
        var cache = builds.Cache();
        var lease = cache.Acquire(device: new StubDevice(), key: "a");

        Assert.True(condition: builds.Entered.Wait(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        var release = new Thread(start: lease.Release);

        release.Start();

        // The entry leaves sharing inside the gate at once; the release itself returns only after the driver does.
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => (cache.SharedEntries == 0),
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        Assert.True(condition: release.IsAlive);
        gate.Set();
        release.Join();

        // The creation in the driver finished and was discarded; the build saw its cancel and created nothing more.
        Assert.Equal(expected: (1, 1), actual: (builds.Count, builds.Disposed));
    }
    // A holder's pool build blocked in Wait while the frame thread gives up the same lease: the wait wakes to a released
    // entry and throws, and nothing builds again for it, so no value outlives the release.
    [Fact]
    public void AWaitRacingItsOwnLeasesLastReleaseThrowsAndStartsNoBuild() {
        using var gate = new ManualResetEventSlim();
        var builds = new Builds(gate: gate);
        var cache = builds.Cache();
        var lease = cache.Acquire(device: new StubDevice(), key: "a");
        Exception? waited = null;

        Assert.True(condition: builds.Entered.Wait(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        var waiter = new Thread(start: () => {
            try {
                _ = lease.Wait(cancellationToken: CancellationToken.None);
            } catch (Exception error) {
                waited = error;
            }
        });

        waiter.Start();

        // The wait has passed the lease's own check and blocks on the build in the driver before the release begins.
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => waiter.ThreadState.HasFlag(flag: ThreadState.WaitSleepJoin),
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        var release = new Thread(start: lease.Release);

        release.Start();
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => (cache.SharedEntries == 0),
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        gate.Set();
        release.Join();
        waiter.Join();

        _ = Assert.IsType<ObjectDisposedException>(@object: waited);
        // The one build ran, its value was discarded by the release, and none started after it.
        Assert.Equal(expected: (1, 1), actual: (builds.Count, builds.Disposed));
    }
    [Fact]
    public void AFailedBuildRethrowsOnceAndTheNextPollBuildsAfresh() {
        var builds = new Builds(failFirst: true);
        var cache = builds.Cache();
        var lease = cache.Acquire(device: new StubDevice(), key: "a");
        Exception? failure = null;

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                try {
                    _ = lease.Poll();
                } catch (InvalidOperationException error) {
                    failure = error;
                }

                return (failure is not null);
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        Assert.Equal(expected: 2, actual: Ready(lease: lease).Serial);
        lease.Release();
    }
    [Fact]
    public void ALoneHolderTakesItsEntryOutOfSharing() {
        var builds = new Builds();
        var cache = builds.Cache();
        var device = new StubDevice();
        var first = cache.Acquire(device: device, key: "a");
        var second = cache.Acquire(device: device, key: "a");

        Assert.False(condition: first.TryMakePrivate());
        second.Release();
        Assert.True(condition: first.TryMakePrivate());
        Assert.Equal(expected: 0, actual: cache.SharedEntries);

        var later = cache.Acquire(device: device, key: "a");

        Assert.NotSame(expected: Ready(lease: first), actual: Ready(lease: later));
        first.Release();
        later.Release();
        Assert.False(condition: first.TryMakePrivate());
    }
    [Fact]
    public void TheCacheLedgerIsNamedByTheCache() {
        var cache = new Builds().Cache();

        Assert.Equal(expected: "gpu.test-builds", actual: cache.Work.Name);
        _ = Assert.Throws<ArgumentNullException>(testCode: () => cache.Acquire(device: null!, key: "a"));
    }

    // Polls until the lease's value has built on the thread pool. The bound is liveness for a build that creates
    // nothing.
    private static Built Ready(GpuBuildLease<string, Built> lease) {
        Built? built = null;

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => ((built = lease.Poll()) is not null),
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        return built!;
    }

    // The builds one cache runs: each counted, numbered, optionally held at a gate as a creation in the driver, and the
    // first optionally failing.
    private sealed class Builds(ManualResetEventSlim? gate = null, bool failFirst = false) {
        private int m_count;
        private int m_disposed;

        public int Count => Volatile.Read(location: ref m_count);
        public int Disposed => Volatile.Read(location: ref m_disposed);
        public ManualResetEventSlim Entered { get; } = new();

        public GpuBuildCache<string, Built> Cache() =>
            new(
                build: Build,
                workSourceName: "gpu.test-builds"
            );

        private Built Build(GpuBuildRequest<string> request, CancellationToken cancellationToken) {
            var serial = Interlocked.Increment(location: ref m_count);

            Entered.Set();
            gate?.Wait(cancellationToken: CancellationToken.None);

            if (
                failFirst &&
                (serial == 1)
            ) {
                throw new InvalidOperationException(message: "The first build fails.");
            }

            var built = new Built(serial: serial, onDispose: () => Interlocked.Increment(location: ref m_disposed));

            if (cancellationToken.IsCancellationRequested) {
                built.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return built;
        }
    }
    private sealed class Built(int serial, Action onDispose) : IDisposable {
        public bool IsDisposed { get; private set; }
        public int Serial { get; } = serial;

        public void Dispose() {
            if (IsDisposed) {
                return;
            }

            IsDisposed = true;
            onDispose();
        }
    }
    // A device the cache matches by reference and never calls.
    private sealed class StubDevice : IGpuDeviceContext {
        public long AdapterLuid => 0L;
        public GpuDeviceCapabilities? Capabilities => null;
        public GpuDeviceIdentity? Identity => null;
        public GpuMemoryProfile MemoryProfile => default;
        public GpuDeviceServices Services => throw new NotSupportedException();

        public void WaitIdle() {
        }
    }
}
