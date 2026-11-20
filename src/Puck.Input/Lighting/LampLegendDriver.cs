using System.Diagnostics;
using Puck.Abstractions.Lighting;

namespace Puck.Input.Lighting;

/// <summary>The lamp arrays a <see cref="LampLegendDriver"/> is driving, for a host's read-back.</summary>
/// <param name="Arrays">The lamp arrays of the driver's kind the platform reports present.</param>
/// <param name="Taken">How many of them the driver holds from their firmware.</param>
/// <param name="FramesWritten">The lamp updates written since the driver started.</param>
public readonly record struct LampLegendStatus(int Arrays, int Taken, ulong FramesWritten);
/// <summary>
/// The device-lighting loop: paints the legend a host publishes onto every present lamp array of one kind, on a
/// thread of its own so a device write or an enumeration never runs on the host's frame. The host publishes a
/// <see cref="LightLegendState"/> whenever it has a new one (<see cref="Publish"/>) and withdraws it when it has none
/// (<see cref="Withdraw"/>); the loop composes it through one <see cref="LightLegendComposer"/> per array, at the pace
/// each array's own minimum update interval allows, and writes only a frame that differs from the one shown.
/// </summary>
/// <remarks>
/// While nothing is published the loop sleeps on a wait handle and touches no device, so a host that never lights a
/// key pays for a parked thread and nothing else. On the first published legend it enumerates the platform's arrays,
/// takes each from its firmware, and enumerates again every <see cref="RescanInterval"/>, so a keyboard plugged in
/// later lights. Withdrawing the legend, and disposing the driver, hand every taken array back to its firmware. The
/// loop opens the platform's source itself, on its own thread at the first published legend, and disposes it.
/// </remarks>
public sealed class LampLegendDriver : IDisposable {
    /// <summary>How often the platform's lamp arrays are enumerated again while a legend is published.</summary>
    public static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(value: 3d);
    /// <summary>The shortest time between two frames of one array, whatever shorter interval the array allows: a
    /// legend's fastest change is a flash fading over a fraction of a second.</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(value: 33d);

    private readonly Dictionary<string, ArrayState> m_arrays = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly Lock m_gate = new();
    private readonly LampArrayKind m_kind;
    private readonly LightLegendState m_pending = new();
    private readonly Func<ILampArrayDeviceSource> m_open;
    private readonly Thread m_thread;
    private readonly AutoResetEvent m_wake = new(initialState: false);
    private readonly LightLegendState m_working = new();

    private readonly List<string> m_gone = [];

    private bool m_disposed;
    private float m_flashSeconds;
    private ulong m_framesWritten;
    private LightLegendPalette? m_palette;
    private int m_paletteRevision;
    private int m_pass;
    private bool m_published;
    private ILampArrayDeviceSource? m_source;
    private int m_presentArrays;
    private int m_takenArrays;

    /// <summary>Initializes a new instance of the <see cref="LampLegendDriver"/> class and starts its loop.</summary>
    /// <param name="open">Opens the platform's lamp arrays; called once, on the loop's thread, and the driver disposes
    /// what it returns.</param>
    /// <param name="kind">The kind of array the legend is painted on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="open"/> is <see langword="null"/>.</exception>
    public LampLegendDriver(Func<ILampArrayDeviceSource> open, LampArrayKind kind = LampArrayKind.Keyboard) {
        ArgumentNullException.ThrowIfNull(argument: open);

        m_open = open;
        m_kind = kind;
        m_thread = new Thread(start: Run) {
            IsBackground = true,
            Name = "puck.lamp-legend",
            Priority = ThreadPriority.BelowNormal,
        };
        m_thread.Start();
    }

    /// <summary>Gets the arrays the loop is driving.</summary>
    public LampLegendStatus Status {
        get {
            lock (m_gate) {
                return new LampLegendStatus(
                    Arrays: m_presentArrays,
                    FramesWritten: m_framesWritten,
                    Taken: m_takenArrays
                );
            }
        }
    }

    /// <summary>Publishes the legend the arrays show from now on. The flashes it carries are kept until the loop has
    /// painted them, so one published between two of the loop's frames is not lost.</summary>
    /// <param name="legend">The legend; copied, so the caller may refill it at once.</param>
    /// <param name="palette">The palette that colors the legend's layers.</param>
    /// <param name="flashSeconds">The seconds a flash takes to fade.</param>
    /// <exception cref="ArgumentNullException"><paramref name="legend"/> or <paramref name="palette"/> is
    /// <see langword="null"/>.</exception>
    public void Publish(LightLegendState legend, LightLegendPalette palette, float flashSeconds) {
        ArgumentNullException.ThrowIfNull(argument: legend);
        ArgumentNullException.ThrowIfNull(argument: palette);

        lock (m_gate) {
            legend.CopyTo(destination: m_pending, keepFlashes: true);
            if (!ReferenceEquals(objA: m_palette, objB: palette)) {
                m_palette = palette;
                m_paletteRevision++;
            }
            m_flashSeconds = flashSeconds;
            if (m_published) {
                return;
            }
            m_published = true;
        }

        _ = m_wake.Set();
    }
    /// <summary>Withdraws the published legend: the loop hands every array back to its firmware and sleeps.</summary>
    public void Withdraw() {
        lock (m_gate) {
            if (!m_published) {
                return;
            }
            m_published = false;
            m_pending.Clear();
        }

        _ = m_wake.Set();
    }
    /// <summary>Stops the loop, hands every taken array back to its firmware and disposes the source.</summary>
    public void Dispose() {
        lock (m_gate) {
            if (m_disposed) {
                return;
            }
            m_disposed = true;
        }

        _ = m_wake.Set();
        m_thread.Join();
        m_wake.Dispose();
        m_source?.Dispose();
    }

    private void Run() {
        var lastFrame = Stopwatch.GetTimestamp();
        var nextRescan = 0L;

        while (true) {
            bool published;
            LightLegendPalette? palette;
            int paletteRevision;
            float flashSeconds;

            lock (m_gate) {
                if (m_disposed) {
                    break;
                }
                published = m_published;
                palette = m_palette;
                paletteRevision = m_paletteRevision;
                flashSeconds = m_flashSeconds;
                if (published) {
                    m_pending.CopyTo(destination: m_working, keepFlashes: false);
                    m_pending.ClearFlashes();
                }
            }

            if (!published || (palette is null)) {
                ReleaseAll();
                _ = m_wake.WaitOne();
                lastFrame = Stopwatch.GetTimestamp();
                nextRescan = 0L;

                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var elapsedSeconds = ((float)Stopwatch.GetElapsedTime(startingTimestamp: lastFrame, endingTimestamp: now).TotalSeconds);

            lastFrame = now;
            if (m_source is null) {
                m_source = m_open();
                nextRescan = (now + ((long)(RescanInterval.TotalSeconds * Stopwatch.Frequency)));
            } else if (now >= nextRescan) {
                nextRescan = (now + ((long)(RescanInterval.TotalSeconds * Stopwatch.Frequency)));
                m_source.Rescan();
            }

            Paint(
                flashDecay: ((flashSeconds > 0f) ? (elapsedSeconds / flashSeconds) : 1f),
                now: now,
                palette: palette,
                paletteRevision: paletteRevision
            );
            _ = m_wake.WaitOne(timeout: FrameInterval);
        }

        ReleaseAll();
    }
    private void Paint(LightLegendPalette palette, int paletteRevision, float flashDecay, long now) {
        var present = 0;
        var taken = 0;
        var written = 0UL;

        m_pass++;

        foreach (var device in m_source!.Devices) {
            if (device.Kind != m_kind) {
                continue;
            }

            present++;
            if (!m_arrays.TryGetValue(key: device.DeviceId, value: out var state) || !ReferenceEquals(objA: state.Device, objB: device)) {
                state = new ArrayState(device: device);
                m_arrays[device.DeviceId] = state;
            }
            state.Pass = m_pass;
            if (!state.Taken) {
                state.Taken = device.TrySetAutonomousMode(enabled: false);
                state.HasShown = false;
                if (!state.Taken) {
                    continue;
                }
            }

            taken++;
            if ((state.Composer is null) || (state.PaletteRevision != paletteRevision)) {
                state.Composer = new LightLegendComposer(palette: palette);
                state.PaletteRevision = paletteRevision;
            }

            // The flash decays on every pass, written or not, so a slow array shows the fade where it has reached.
            state.Composer.Compose(
                destination: state.Next,
                device: device,
                flashDecay: flashDecay,
                state: m_working
            );
            if (
                (now < state.NextWrite) ||
                (state.HasShown && state.Next.AsSpan().SequenceEqual(other: state.Shown))
            ) {
                continue;
            }

            device.UpdateLamps(colors: state.Next, lampIds: state.LampIds);
            state.Next.CopyTo(array: state.Shown, index: 0);
            state.HasShown = true;
            state.NextWrite = (now + ((long)((Math.Max(val1: 1, val2: device.MinUpdateIntervalInMilliseconds) / 1000d) * Stopwatch.Frequency)));
            written++;
        }

        // An array the source no longer lists was disposed by it: forget it without touching the handle.
        foreach (var (id, state) in m_arrays) {
            if (state.Pass != m_pass) {
                m_gone.Add(item: id);
            }
        }
        foreach (var id in m_gone) {
            _ = m_arrays.Remove(key: id);
        }
        m_gone.Clear();

        lock (m_gate) {
            m_presentArrays = present;
            m_takenArrays = taken;
            m_framesWritten += written;
        }
    }
    private void ReleaseAll() {
        foreach (var state in m_arrays.Values) {
            if (state.Taken) {
                _ = state.Device.TrySetAutonomousMode(enabled: true);
                state.Taken = false;
            }
        }

        m_arrays.Clear();
        lock (m_gate) {
            m_takenArrays = 0;
        }
    }

    private sealed class ArrayState {
        public readonly ILampArrayDevice Device;
        public readonly int[] LampIds;
        public readonly LampColor[] Next;
        public readonly LampColor[] Shown;
        public LightLegendComposer? Composer;
        public bool HasShown;
        public long NextWrite;
        public int Pass;
        public int PaletteRevision;
        public bool Taken;

        public ArrayState(ILampArrayDevice device) {
            Device = device;
            LampIds = new int[device.LampCount];
            Next = new LampColor[device.LampCount];
            Shown = new LampColor[device.LampCount];
            for (var lamp = 0; (lamp < LampIds.Length); lamp++) {
                LampIds[lamp] = lamp;
            }
        }
    }
}
