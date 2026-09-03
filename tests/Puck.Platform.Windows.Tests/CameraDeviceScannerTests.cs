using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class CameraDeviceScannerTests {
    [Fact]
    public async Task BlockingDiscoveryNeverRunsOnTheCallerOrQueuesOverlappingScans() {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = Environment.CurrentManagedThreadId;
        var worker = 0;
        var service = new FakeService(() => {
            worker = Environment.CurrentManagedThreadId;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new InvalidOperationException("test release timed out"); }
            return [new("camera-a", "A", [CameraSensor.Color])];
        });
        using var scanner = new CameraDeviceScanner(service, TimeSpan.FromSeconds(2));
        try {
            Assert.False(scanner.TryPoll(0, out _));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotEqual(caller, worker);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var unexpectedlyCompleted = false;
            for (var index = 0; index < 1000; index++) { unexpectedlyCompleted |= scanner.TryPoll(10 * Stopwatch.Frequency, out _); }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.False(unexpectedlyCompleted);
            Assert.Equal(0, allocated);
            Assert.Equal(1, service.Calls);
        } finally {
            release.Set();
        }
        var result = await Complete(scanner, 10 * Stopwatch.Frequency);
        Assert.Null(result.Failure);
        Assert.Equal("camera-a", Assert.Single(result.Devices).Id);
        Assert.False(scanner.TryPoll(12 * Stopwatch.Frequency - 1, out _));
        Assert.Equal(1, service.Calls);
        Assert.False(scanner.TryPoll(12 * Stopwatch.Frequency, out _));
        await Complete(scanner, 12 * Stopwatch.Frequency);
        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public async Task FailureRemainsDistinctFromAnEmptySuccessfulScanAndCanRecover() {
        var fail = true;
        var service = new FakeService(() => fail ? throw new InvalidOperationException("scan refused") : []);
        using var scanner = new CameraDeviceScanner(service, TimeSpan.FromSeconds(1));
        var failure = await Complete(scanner, 0);
        Assert.Equal("scan refused", failure.Failure);
        Assert.Empty(failure.Devices);
        fail = false;
        var success = await Complete(scanner, Stopwatch.Frequency);
        Assert.Null(success.Failure);
        Assert.Empty(success.Devices);
    }

    [Fact]
    public async Task DisposalDoesNotWaitForOrPublishALateScanFailure() {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService(() => {
            entered.TrySetResult();
            try {
                if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new InvalidOperationException("test release timed out"); }
                throw new NotSupportedException("late platform failure");
            } finally {
                finished.TrySetResult();
            }
        });
        using var scanner = new CameraDeviceScanner(service, TimeSpan.FromSeconds(1));
        try {
            Assert.False(scanner.TryPoll(0, out _));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            scanner.Dispose();
            Assert.False(scanner.TryPoll(10 * Stopwatch.Frequency, out _));
        } finally {
            release.Set();
        }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(scanner.TryPoll(20 * Stopwatch.Frequency, out _));
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task SnapshotOwnsTheDeviceAndSensorLists() {
        CameraSensor[] sensors = [CameraSensor.Color];
        CameraDeviceInfo[] devices = [new("camera-a", "A", sensors)];
        using var scanner = new CameraDeviceScanner(new FakeService(() => devices), TimeSpan.FromSeconds(1));
        var result = await Complete(scanner, 0);
        sensors[0] = CameraSensor.Infrared;
        devices[0] = new("camera-b", "B", []);
        var device = Assert.Single(result.Devices);
        Assert.Equal("camera-a", device.Id);
        Assert.Equal(CameraSensor.Color, Assert.Single(device.Sensors));
    }

    [Fact]
    public async Task UnsupportedPlatformCompletesWithoutEnumerating() {
        var service = new FakeService(() => throw new InvalidOperationException("must not enumerate")) { IsSupported = false };
        using var scanner = new CameraDeviceScanner(service, TimeSpan.FromSeconds(1));
        var result = await Complete(scanner, 0);
        Assert.Null(result.Failure);
        Assert.Empty(result.Devices);
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void NonpositiveIntervalsAreRefused(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraDeviceScanner(new FakeService(() => []), TimeSpan.FromSeconds(seconds)));

    private static async Task<CameraDeviceScanResult> Complete(CameraDeviceScanner scanner, long timestamp) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true) {
            if (scanner.TryPoll(timestamp, out var result)) { return result; }
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class FakeService(Func<IReadOnlyList<CameraDeviceInfo>> enumerate) : ICameraCaptureService {
        private int m_calls;
        public int Calls => Volatile.Read(ref m_calls);
        public bool IsSupported { get; init; } = true;
        public IReadOnlyList<CameraDeviceInfo> EnumerateDevices() {
            Interlocked.Increment(ref m_calls);
            return enumerate();
        }
        public bool TryOpenPixels(string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraPixelStream>? graph) { graph = null; return false; }
        public bool TryOpenShared(long adapterLuid, string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraSharedStream>? graph) { graph = null; return false; }
    }
}
