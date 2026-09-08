using System.Diagnostics;

namespace Puck.Platform;

/// <summary>A completed camera scan. A failure carries no device list and must not retire known devices.</summary>
/// <param name="Devices">The worker-owned snapshot from a successful scan, or an empty list on failure.</param>
/// <param name="Failure">The scan failure message, or null on success.</param>
public readonly record struct CameraDeviceScanResult(IReadOnlyList<CameraDeviceInfo> Devices, string? Failure);

/// <summary>Runs at most one physical-camera enumeration off the presentation thread. The caller polls completed
/// snapshots without waiting; device/roster reconciliation remains on the caller's thread.</summary>
/// <remarks>Poll and dispose from one owning thread. Disposal neither waits for nor adopts an outstanding scan;
/// the platform service must outlive that operation. The platform scan has no cancellation door.</remarks>
public sealed class CameraDeviceScanner : IDisposable {
    private readonly ICameraCaptureService m_service;
    private readonly long m_intervalTicks;
    private Task<CameraDeviceScanResult>? m_pending;
    private long m_nextTimestamp;
    private bool m_disposed;

    /// <summary>Creates a scanner. The interval starts when a completed scan is consumed, so a slow scan cannot
    /// accumulate queued retries.</summary>
    /// <param name="service">The platform's worker-thread-safe camera service.</param>
    /// <param name="interval">The positive delay between completion and the next scan.</param>
    /// <exception cref="ArgumentNullException">The service is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The interval is shorter than one stopwatch tick or exceeds its range.</exception>
    public CameraDeviceScanner(ICameraCaptureService service, TimeSpan interval) {
        ArgumentNullException.ThrowIfNull(service);
        var ticks = interval.TotalSeconds * Stopwatch.Frequency;
        if (!double.IsFinite(ticks) || ticks < 1 || ticks >= long.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }
        m_service = service;
        m_intervalTicks = (long)ticks;
    }

    /// <summary>Consumes one completed scan, or queues a due scan and returns false immediately.</summary>
    /// <param name="timestamp">The caller's current monotonic <see cref="Stopwatch.GetTimestamp"/> value.</param>
    /// <param name="result">The completed result only when true is returned.</param>
    /// <returns>Whether a new snapshot is available. Returns false after disposal.</returns>
    public bool TryPoll(long timestamp, out CameraDeviceScanResult result) {
        result = default;
        if (m_disposed) { return false; }
        if (m_pending is { } pending) {
            if (!pending.IsCompleted) { return false; }
            result = pending.GetAwaiter().GetResult();
            m_pending = null;
            m_nextTimestamp = timestamp > long.MaxValue - m_intervalTicks ? long.MaxValue : timestamp + m_intervalTicks;
            return true;
        }
        if (timestamp < m_nextTimestamp) { return false; }
        m_pending = StartScan(m_service);
        return false;
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_disposed = true;
        m_pending = null;
    }

    // Keep the captured service outside TryPoll's scope: C# otherwise allocates its display class on every poll,
    // including the in-flight and cadence-wait branches. Only a due scan needs a task and its capture.
    private static Task<CameraDeviceScanResult> StartScan(ICameraCaptureService service) => Task.Run(() => Scan(service));

    private static CameraDeviceScanResult Scan(ICameraCaptureService service) {
        try {
            if (!service.IsSupported) { return new([], null); }
            var devices = service.EnumerateDevices();
            var snapshot = new CameraDeviceInfo[devices.Count];
            for (var index = 0; index < snapshot.Length; index++) {
                var device = devices[index];
                snapshot[index] = device with { Sensors = device.Sensors.ToArray() };
            }
            return new(snapshot, null);
        } catch (Exception exception) {
            // A late result is safe to abandon: expected and unexpected platform faults are observed here, never
            // left on an unobserved task. The consumer preserves its previous table and narrates the message.
            return new([], exception.Message);
        }
    }
}
