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
internal sealed class TestHidDevice : IHidDevice {
    private readonly TaskCompletionSource m_disposedSignal = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<byte[]> m_reports = new();

    private int m_activeReads;
    private bool m_disposed;

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

    public void EnqueueReport(params byte[] report) => m_reports.Enqueue(item: report);
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        ReadCoreAsync(buffer: buffer, cancellationToken: cancellationToken, timeoutInMilliseconds: null);
    public ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        bool throwOnTimeout = false,
        int timeoutInMilliseconds = 120,
        CancellationToken cancellationToken = default
    ) => ReadCoreAsync(buffer: buffer, cancellationToken: cancellationToken, timeoutInMilliseconds: timeoutInMilliseconds);

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, int? timeoutInMilliseconds, CancellationToken cancellationToken) {
        _ = Interlocked.Increment(location: ref m_activeReads);
        _ = ReadEntered.TrySetResult();

        try {
            if (BlockReadUntilDisposed) {
                await m_disposedSignal.Task;

                return 0;
            }

            if (m_reports.TryDequeue(result: out var report)) {
                report.CopyTo(destination: buffer);

                return report.Length;
            }

            if (timeoutInMilliseconds is { } timeout) {
                await Task.Delay(cancellationToken: cancellationToken, millisecondsDelay: timeout);

                return 0;
            }

            await Task.Delay(cancellationToken: cancellationToken, millisecondsDelay: Timeout.Infinite);

            return 0;
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
