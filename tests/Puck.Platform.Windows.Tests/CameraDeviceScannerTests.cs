using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class CameraDeviceScannerTests {
    // Polls once, which queues a due scan, then awaits that scan and consumes it. A poll that neither queues nor
    // finds a scan leaves nothing to await, so the final poll fails the test instead of waiting.
    private static async Task<CameraDeviceScanResult> Complete(CameraDeviceScanner scanner, long timestamp) {
        if (scanner.TryPoll(
            result: out var result,
            timestamp: timestamp
        )) { return result; }
        await scanner.ScanCompletion.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: scanner.TryPoll(
            result: out result,
            timestamp: timestamp
        ));
        return result;
    }

    [Fact]
    public async Task BlockingDiscoveryNeverRunsOnTheCallerOrQueuesOverlappingScans() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = Environment.CurrentManagedThreadId;
        var worker = 0;
        var service = new FakeService(enumerate: () => {
            worker = Environment.CurrentManagedThreadId;
            entered.TrySetResult();
            release.Wait(cancellationToken: cancellationToken);
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
            await entered.Task.WaitAsync(cancellationToken: cancellationToken);
            var unexpectedlyCompleted = false;
            var allocated = AllocationWindow.Least(window: () => {
                for (var index = 0; (index < 1000); index++) {
                    unexpectedlyCompleted |= scanner.TryPoll(
                        result: out _,
                        timestamp: (10 * Stopwatch.Frequency)
                    );
                }
            });

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
        // Any scan queued from here on blocks until released, so a scan the cadence should not have started stays
        // outstanding where the next assertion sees it.
        release.Reset();
        Assert.False(condition: scanner.TryPoll(
            result: out _,
            timestamp: ((12 * Stopwatch.Frequency) - 1)
        ));
        Assert.True(condition: scanner.ScanCompletion.IsCompleted);
        Assert.Equal(
            1,
            service.Calls
        );
        Assert.False(condition: scanner.TryPoll(
            result: out _,
            timestamp: (12 * Stopwatch.Frequency)
        ));
        Assert.False(condition: scanner.ScanCompletion.IsCompleted);
        release.Set();
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
        var cancellationToken = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService(enumerate: () => {
            entered.TrySetResult();
            try {
                release.Wait(cancellationToken: cancellationToken);
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
            await entered.Task.WaitAsync(cancellationToken: cancellationToken);
            scanner.Dispose();
            // A disposed scanner has abandoned the scan, so nothing is left for a caller to wait on.
            Assert.True(condition: scanner.ScanCompletion.IsCompleted);
            Assert.False(condition: scanner.TryPoll(
                result: out _,
                timestamp: (10 * Stopwatch.Frequency)
            ));
        } finally {
            release.Set();
        }
        await finished.Task.WaitAsync(cancellationToken: cancellationToken);
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
