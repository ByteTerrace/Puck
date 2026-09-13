using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class CameraDeviceScannerTests {
    private static async Task<CameraDeviceScanResult> Complete(CameraDeviceScanner scanner, long timestamp) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        timeout.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 10));
        while (true) {
            if (scanner.TryPoll(
                result: out var result,
                timestamp: timestamp
            )) { return result; }
            await Task.Delay(
                1,
                timeout.Token
            );
        }
    }

    [Fact]
    public async Task BlockingDiscoveryNeverRunsOnTheCallerOrQueuesOverlappingScans() {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = Environment.CurrentManagedThreadId;
        var worker = 0;
        var service = new FakeService(enumerate: () => {
            worker = Environment.CurrentManagedThreadId;
            entered.TrySetResult();
            if (!release.Wait(timeout: TimeSpan.FromSeconds(seconds: 10))) { throw new InvalidOperationException(message: "test release timed out"); }
            return [new(
                    Id: "camera-a",
                    Name: "A",
                    Sensors: [CameraSensor.Color]
                )];
        });
        using var scanner = new CameraDeviceScanner(
            service,
            TimeSpan.FromSeconds(seconds: 2)
        );

        try {
            Assert.False(condition: scanner.TryPoll(
                result: out _,
                timestamp: 0
            ));
            // Check before yielding: once awaited, the pool may legitimately reuse the former caller thread.
            Assert.NotEqual(
                caller,
                Volatile.Read(location: ref worker)
            );
            await entered.Task.WaitAsync(
                TimeSpan.FromSeconds(seconds: 10),
                TestContext.Current.CancellationToken
            );
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var unexpectedlyCompleted = false;

            for (var index = 0; (index < 1000); index++) { unexpectedlyCompleted |= scanner.TryPoll(
                result: out _,
                timestamp: (10 * Stopwatch.Frequency)
            ); }
            var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);

            Assert.False(condition: unexpectedlyCompleted);
            Assert.Equal(
                actual: allocated,
                expected: 0
            );
            Assert.Equal(
                1,
                service.Calls
            );
        } finally {
            release.Set();
        }
        var result = await Complete(
            scanner: scanner,
            timestamp: (10 * Stopwatch.Frequency)
        );

        Assert.Null(@object: result.Failure);
        Assert.Equal(
            "camera-a",
            Assert.Single(collection: result.Devices).Id
        );
        Assert.False(condition: scanner.TryPoll(
            result: out _,
            timestamp: ((12 * Stopwatch.Frequency) - 1)
        ));
        Assert.Equal(
            1,
            service.Calls
        );
        Assert.False(condition: scanner.TryPoll(
            result: out _,
            timestamp: (12 * Stopwatch.Frequency)
        ));
        await Complete(
            scanner: scanner,
            timestamp: (12 * Stopwatch.Frequency)
        );
        Assert.Equal(
            2,
            service.Calls
        );
    }
    [Fact]
    public async Task DisposalDoesNotWaitForOrPublishALateScanFailure() {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService(enumerate: () => {
            entered.TrySetResult();
            try {
                if (!release.Wait(timeout: TimeSpan.FromSeconds(seconds: 10))) { throw new InvalidOperationException(message: "test release timed out"); }
                throw new NotSupportedException(message: "late platform failure");
            } finally {
                finished.TrySetResult();
            }
        });
        using var scanner = new CameraDeviceScanner(
            service,
            TimeSpan.FromSeconds(seconds: 1)
        );

        try {
            Assert.False(condition: scanner.TryPoll(
                result: out _,
                timestamp: 0
            ));
            await entered.Task.WaitAsync(
                TimeSpan.FromSeconds(seconds: 10),
                TestContext.Current.CancellationToken
            );
            scanner.Dispose();
            Assert.False(condition: scanner.TryPoll(
                result: out _,
                timestamp: (10 * Stopwatch.Frequency)
            ));
        } finally {
            release.Set();
        }
        await finished.Task.WaitAsync(
            TimeSpan.FromSeconds(seconds: 10),
            TestContext.Current.CancellationToken
        );
        Assert.False(condition: scanner.TryPoll(
            result: out _,
            timestamp: (20 * Stopwatch.Frequency)
        ));
        Assert.Equal(
            1,
            service.Calls
        );
    }
    [Fact]
    public async Task FailureRemainsDistinctFromAnEmptySuccessfulScanAndCanRecover() {
        var fail = true;
        var service = new FakeService(enumerate: () => (fail
            ? throw new InvalidOperationException(message: "scan refused")
            : []));
        using var scanner = new CameraDeviceScanner(
            service,
            TimeSpan.FromSeconds(seconds: 1)
        );
        var failure = await Complete(
            scanner: scanner,
            timestamp: 0
        );

        Assert.Equal(
            "scan refused",
            failure.Failure
        );
        Assert.Empty(collection: failure.Devices);
        fail = false;
        var success = await Complete(
            scanner: scanner,
            timestamp: Stopwatch.Frequency
        );

        Assert.Null(@object: success.Failure);
        Assert.Empty(collection: success.Devices);
    }
    [InlineData(0)]
    [InlineData(-1)]
    [Theory]
    public void NonpositiveIntervalsAreRefused(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new CameraDeviceScanner(
            new FakeService(enumerate: () => []),
            TimeSpan.FromSeconds(seconds: seconds)
        ));
    [Fact]
    public async Task SnapshotOwnsTheDeviceAndSensorLists() {
        CameraSensor[] sensors = [CameraSensor.Color];
        CameraDeviceInfo[] devices = [new(
                Id: "camera-a",
                Name: "A",
                Sensors: sensors
            )];
        using var scanner = new CameraDeviceScanner(
            new FakeService(enumerate: () => devices),
            TimeSpan.FromSeconds(seconds: 1)
        );
        var result = await Complete(
            scanner: scanner,
            timestamp: 0
        );

        sensors[0] = CameraSensor.Infrared;
        devices[0] = new(
            Id: "camera-b",
            Name: "B",
            Sensors: []
        );
        var device = Assert.Single(collection: result.Devices);

        Assert.Equal(
            "camera-a",
            device.Id
        );
        Assert.Equal(
            CameraSensor.Color,
            Assert.Single(collection: device.Sensors)
        );
    }
    [Fact]
    public async Task UnsupportedPlatformCompletesWithoutEnumerating() {
        var service = new FakeService(enumerate: () => throw new InvalidOperationException(message: "must not enumerate")) { IsSupported = false };
        using var scanner = new CameraDeviceScanner(
            service,
            TimeSpan.FromSeconds(seconds: 1)
        );
        var result = await Complete(
            scanner: scanner,
            timestamp: 0
        );

        Assert.Null(@object: result.Failure);
        Assert.Empty(collection: result.Devices);
        Assert.Equal(
            0,
            service.Calls
        );
    }

    private sealed class FakeService(Func<IReadOnlyList<CameraDeviceInfo>> enumerate) : ICameraCaptureService {
        private int m_calls;

        public int Calls => Volatile.Read(location: ref m_calls);
        public bool IsSupported { get; init; } = true;

        public IReadOnlyList<CameraDeviceInfo> EnumerateDevices() {
            Interlocked.Increment(location: ref m_calls);
            return enumerate();
        }
        public bool TryOpenPixels(string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraPixelStream>? graph) { graph = null; return false; }
        public bool TryOpenShared(long adapterLuid, string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraSharedStream>? graph) { graph = null; return false; }
    }
}
