using System.Collections.Concurrent;
using Puck.Commands;
using Puck.Input.Devices;
using Puck.Input.Hid;
using Puck.Input.Output;

namespace Puck.Input.Tests;

internal sealed class ManualInputClock : IInputClock {
    public ulong NowTicks { get; set; }
}
internal sealed class EmptyHidDeviceSource : IHidDeviceSource {
    public IEnumerable<HidDeviceInfo> EnumerateInterfaces() => [];
    public IHidDevice? Open(string devicePath) => null;
}
/// <summary>
/// An in-memory HID transport. Reads honor the <see cref="IHidDevice"/> contract: a report enqueued while a
/// read is pending completes that read, and a timed read returns zero only once its timeout elapses. A test that
/// must order its observations against the device's silence watchdog holds read timeouts, which keeps every
/// timed read pending until a report arrives or the hold is released.
/// </summary>
internal sealed class TestHidDevice : IHidDevice {
    private readonly TaskCompletionSource m_disposedSignal = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<byte[]> m_reports = new();

    private int m_activeReads;
    private bool m_disposed;
    private TaskCompletionSource? m_readTimeoutHold;

    private TaskCompletionSource m_reportArrived = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

    public string DevicePath { get; init; } = "test:hid";

    public ushort ProductId { get; init; }
    public ushort VendorId { get; init; }

    public ushort UsagePage { get; init; } = 1;
    public ushort Usage { get; init; } = 5;
    public HidTransport Transport { get; init; } = HidTransport.Usb;

    public bool BlockReadUntilDisposed { get; init; }
    public bool DisposedWhileReading { get; private set; }
    public int FeatureReportByteLength { get; init; }
    public int InputReportByteLength { get; init; }
    public int OutputReportByteLength { get; init; }

    public TaskCompletionSource ReadEntered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    public List<byte[]> Writes { get; } = [];
    public List<byte[]> FeatureWrites { get; } = [];

    public void EnqueueReport(params byte[] report) {
        m_reports.Enqueue(item: report);
        // Publish the arrival after the enqueue: a reader that snapshotted the previous pulse before probing the
        // queue either dequeues this report or is woken by this completion, never neither.
        _ = Interlocked.Exchange(
            location1: ref m_reportArrived,
            value: new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously)
        ).TrySetResult();
    }
    /// <summary>Keeps timed reads pending until a report arrives or <see cref="ReleaseReadTimeouts"/> runs.</summary>
    public void HoldReadTimeouts() =>
        m_readTimeoutHold ??= new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Lets pending and future timed reads expire after their timeout again.</summary>
    public void ReleaseReadTimeouts() =>
        _ = Interlocked.Exchange(location1: ref m_readTimeoutHold, value: null)?.TrySetResult();
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        ReadCoreAsync(buffer: buffer, cancellationToken: cancellationToken, timeoutInMilliseconds: null);
    public ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        bool throwOnTimeout = false,
        int timeoutInMilliseconds = 120,
        CancellationToken cancellationToken = default
    ) => ReadCoreAsync(buffer: buffer, cancellationToken: cancellationToken, timeoutInMilliseconds: timeoutInMilliseconds);

    private async Task ExpireAsync(int timeoutInMilliseconds, CancellationToken cancellationToken) {
        if (Volatile.Read(location: ref m_readTimeoutHold) is { } hold) {
            await hold.Task.WaitAsync(cancellationToken: cancellationToken);
        }

        await Task.Delay(cancellationToken: cancellationToken, millisecondsDelay: timeoutInMilliseconds);
    }
    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, int? timeoutInMilliseconds, CancellationToken cancellationToken) {
        _ = Interlocked.Increment(location: ref m_activeReads);
        _ = ReadEntered.TrySetResult();

        try {
            if (BlockReadUntilDisposed) {
                await m_disposedSignal.Task;

                return 0;
            }

            var expiry = ((timeoutInMilliseconds is { } timeout)
                ? ExpireAsync(cancellationToken: cancellationToken, timeoutInMilliseconds: timeout)
                : null
            );

            while (true) {
                // Snapshot the arrival pulse before probing the queue so an enqueue between the probe and the
                // await still wakes this read.
                var arrival = Volatile.Read(location: ref m_reportArrived).Task;

                if (m_reports.TryDequeue(result: out var report)) {
                    report.CopyTo(destination: buffer);

                    return report.Length;
                }

                if (expiry is null) {
                    await arrival.WaitAsync(cancellationToken: cancellationToken);
                } else if (expiry == await Task.WhenAny(task1: arrival, task2: expiry)) {
                    // Propagates cancellation the way a timed delay does.
                    await expiry;

                    return 0;
                }
            }
        } finally {
            _ = Interlocked.Decrement(location: ref m_activeReads);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
        lock (Writes) {
            Writes.Add(item: buffer.ToArray());
        }

        return ValueTask.CompletedTask;
    }
    public bool TryGetFeatureReport(Span<byte> buffer) => false;
    public bool TrySetFeatureReport(ReadOnlySpan<byte> buffer) {
        lock (FeatureWrites) {
            FeatureWrites.Add(item: buffer.ToArray());
        }

        return true;
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        DisposedWhileReading = (Volatile.Read(location: ref m_activeReads) != 0);
        m_disposed = true;
        _ = m_disposedSignal.TrySetResult();
    }

    public bool IsDisposed => m_disposed;
}
internal sealed class TestParser : IGamepadParser, IRumbleParser, ITriggerEffectParser, IWirelessSlotParser, IGamepadStreamReset, IDisposable {
    private readonly Lock m_gate = new();

    public int DisposeCount { get; private set; }
    public int InitializeCount { get; private set; }
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.None;
    public int ResetCount { get; private set; }
    public GamepadType Type => GamepadType.Unknown;

    public List<(float Low, float High)> RumbleWrites { get; } = [];
    public List<(TriggerEffectSpec Left, TriggerEffectSpec Right)> TriggerWrites { get; } = [];

    public ValueTask InitializeAsync(int playerIndex, CancellationToken cancellationToken = default) {
        lock (m_gate) {
            ++InitializeCount;
        }

        return ValueTask.CompletedTask;
    }
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state) {
        if (!report.IsEmpty && (report[0] == 1)) {
            state = GamepadState.Neutral with {
                Buttons = ((report.Length > 1) ? (GamepadButtons)report[1] : GamepadButtons.None),
            };

            return true;
        }

        state = GamepadState.Neutral;

        return false;
    }
    public WirelessSlotEvent ClassifySlotEvent(ReadOnlySpan<byte> report) =>
        ((!report.IsEmpty && (report[0] == 2)) ? WirelessSlotEvent.Connected
            : ((!report.IsEmpty && (report[0] == 3)) ? WirelessSlotEvent.Disconnected
            : WirelessSlotEvent.None));
    public ValueTask SetRumbleAsync(float lowFrequency, float highFrequency, CancellationToken cancellationToken = default) {
        lock (m_gate) {
            RumbleWrites.Add(item: (lowFrequency, highFrequency));
        }

        return ValueTask.CompletedTask;
    }
    public ValueTask SetTriggerEffectAsync(TriggerEffectSpec left, TriggerEffectSpec right, CancellationToken cancellationToken = default) {
        lock (m_gate) {
            TriggerWrites.Add(item: (left, right));
        }

        return ValueTask.CompletedTask;
    }
    public void ResetStreamState() {
        lock (m_gate) {
            ++ResetCount;
        }
    }
    public void Dispose() {
        lock (m_gate) {
            ++DisposeCount;
        }
    }
}
internal static class TestWait {
    public static async Task UntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000) {
        using var cancellation = new CancellationTokenSource(millisecondsDelay: timeoutMilliseconds);

        while (!condition()) {
            await Task.Delay(millisecondsDelay: 5, cancellationToken: cancellation.Token);
        }
    }
}
