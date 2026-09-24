using System.Collections.Concurrent;
using Puck.Commands;
using Puck.Input.Devices;
using Puck.Input.Hid;
using Puck.Input.Output;
using Puck.Testing;

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
/// read is pending completes that read, and a timed read returns zero only once its timeout elapses on
/// <see cref="Time"/>, which moves only when the test advances it. Every read announces itself once its expiry is
/// armed, so a test that awaits read <c>n</c> knows the device loop finished everything it did with read
/// <c>n - 1</c> and is parked on the transport.
/// </summary>
internal sealed class TestHidDevice : IHidDevice {
    private readonly TaskCompletionSource m_disposedSignal = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock m_readGate = new();
    private readonly List<(int Ordinal, TaskCompletionSource Signal)> m_readWaiters = [];
    private readonly ConcurrentQueue<byte[]> m_reports = new();
    private TaskCompletionSource m_reportArrived = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

    public string DevicePath { get; init; } = "test:hid";
    public ushort UsagePage { get; init; } = 1;
    public ushort Usage { get; init; } = 5;
    public HidTransport Transport { get; init; } = HidTransport.Usb;
    public List<byte[]> Writes { get; } = [];
    public List<byte[]> FeatureWrites { get; } = [];

    private int m_activeReads;
    private bool m_disposed;
    private int m_readsEntered;

    public bool BlockReadUntilDisposed { get; init; }
    public bool DisposedWhileReading { get; private set; }
    public int FeatureReportByteLength { get; init; }
    public int InputReportByteLength { get; init; }
    public bool IsDisposed => m_disposed;
    public int OutputReportByteLength { get; init; }
    public ushort ProductId { get; init; }

    /// <summary>Gets the clock timed reads expire on; a device under test shares it for its own deadlines.</summary>
    public VirtualClock Time { get; } = new();

    public ushort VendorId { get; init; }

    private void AnnounceRead() {
        lock (m_readGate) {
            ++m_readsEntered;

            for (var index = (m_readWaiters.Count - 1); (index >= 0); --index) {
                if (m_readWaiters[index].Ordinal <= m_readsEntered) {
                    _ = m_readWaiters[index].Signal.TrySetResult();
                    m_readWaiters.RemoveAt(index: index);
                }
            }
        }
    }
    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, int? timeoutInMilliseconds, CancellationToken cancellationToken) {
        using var settled = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

        _ = Interlocked.Increment(location: ref m_activeReads);

        try {
            if (BlockReadUntilDisposed) {
                AnnounceRead();
                await m_disposedSignal.Task;

                return 0;
            }

            // Arm the expiry before announcing the read, so an advance the test makes after observing the read
            // always reaches its timer. A read a report completes disarms its expiry on the way out.
            var expiry = ((timeoutInMilliseconds is { } timeout)
                ? Task.Delay(
                    cancellationToken: settled.Token,
                    delay: TimeSpan.FromMilliseconds(value: timeout),
                    timeProvider: Time
                )
                : null
            );

            AnnounceRead();

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
                } else if (expiry == await Task.WhenAny(
                    task1: arrival,
                    task2: expiry
                )) {
                    // Propagates cancellation the way a timed delay does.
                    await expiry;

                    return 0;
                }
            }
        } finally {
            settled.Cancel();
            _ = Interlocked.Decrement(location: ref m_activeReads);
        }
    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        DisposedWhileReading = (Volatile.Read(location: ref m_activeReads) != 0);
        m_disposed = true;
        _ = m_disposedSignal.TrySetResult();
    }
    public void EnqueueReport(params byte[] report) {
        m_reports.Enqueue(item: report);
        // Publish the arrival after the enqueue: a reader that snapshotted the previous pulse before probing the
        // queue either dequeues this report or is woken by this completion, never neither.
        _ = Interlocked.Exchange(
            location1: ref m_reportArrived,
            value: new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously)
        ).TrySetResult();
    }
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        ReadCoreAsync(
            buffer: buffer,
            cancellationToken: cancellationToken,
            timeoutInMilliseconds: null
        );
    public ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        bool throwOnTimeout = false,
        int timeoutInMilliseconds = 120,
        CancellationToken cancellationToken = default
    ) => ReadCoreAsync(
        buffer: buffer,
        cancellationToken: cancellationToken,
        timeoutInMilliseconds: timeoutInMilliseconds
    );
    public bool TryGetFeatureReport(Span<byte> buffer) => false;
    public bool TrySetFeatureReport(ReadOnlySpan<byte> buffer) {
        lock (FeatureWrites) {
            FeatureWrites.Add(item: buffer.ToArray());
        }

        return true;
    }
    /// <summary>Completes once the one-based <paramref name="ordinal"/>-th read has begun and armed its expiry.</summary>
    public Task WhenReadAsync(int ordinal, CancellationToken cancellationToken) {
        TaskCompletionSource signal;

        lock (m_readGate) {
            if (m_readsEntered >= ordinal) {
                return Task.CompletedTask;
            }

            signal = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
            m_readWaiters.Add(item: (ordinal, signal));
        }

        return signal.Task.WaitAsync(cancellationToken: cancellationToken);
    }
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
        lock (Writes) {
            Writes.Add(item: buffer.ToArray());
        }

        return ValueTask.CompletedTask;
    }
}
internal sealed class TestParser : IGamepadParser, IRumbleParser, ITriggerEffectParser, IWirelessSlotParser, IGamepadStreamReset, IDisposable {
    private readonly Lock m_gate = new();

    public List<(float Low, float High)> RumbleWrites { get; } = [];
    public List<(TriggerEffectSpec Left, TriggerEffectSpec Right)> TriggerWrites { get; } = [];

    public int DisposeCount { get; private set; }
    public int InitializeCount { get; private set; }
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.None;
    public int ResetCount { get; private set; }
    public GamepadType Type => GamepadType.Unknown;

    public WirelessSlotEvent ClassifySlotEvent(ReadOnlySpan<byte> report) =>
        ((!report.IsEmpty && (report[0] == 2))
            ? WirelessSlotEvent.Connected
            : ((!report.IsEmpty && (report[0] == 3))
                ? WirelessSlotEvent.Disconnected
                : WirelessSlotEvent.None
        ));
    public void Dispose() {
        lock (m_gate) {
            ++DisposeCount;
        }
    }
    public ValueTask InitializeAsync(int playerIndex, CancellationToken cancellationToken = default) {
        lock (m_gate) {
            ++InitializeCount;
        }

        return ValueTask.CompletedTask;
    }
    public void ResetStreamState() {
        lock (m_gate) {
            ++ResetCount;
        }
    }
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
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state) {
        if (
            !report.IsEmpty &&
            (report[0] == 1)
        ) {
            state = GamepadState.Neutral with {
                Buttons = ((report.Length > 1)
                ? (GamepadButtons)report[1]
                : GamepadButtons.None),
            };

            return true;
        }

        state = GamepadState.Neutral;

        return false;
    }
}
