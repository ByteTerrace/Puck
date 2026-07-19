using Microsoft.Extensions.Hosting;
using Puck.Platform.Audio;
using Puck.World.Client;

namespace Puck.World.Audio;

/// <summary>
/// The world speaker device — the <see cref="IHostedService"/> that turns the pure mixer into
/// sound. It owns one mixer instance and one governor thread: the governor opens the platform's default render
/// endpoint through the factory seam, attaches the mixer to the director on success, and watches the stream; the
/// endpoint's own pump thread then pulls each ≤256-frame quantum through <see cref="WorldAudioDirector.TryMixBlock"/>
/// (snapshot hold + <see cref="WorldAudioMixer.MixBlock"/>, zero steady-state allocation, silence while nothing is
/// published). Failure posture is "plays silent, never crashes": a declined open or a mid-stream fault (device
/// invalidation included) detaches the mixer, counts a rebind attempt, and retries the default endpoint every
/// <see cref="RebindPeriodMilliseconds"/> until stop. A null factory (non-Windows) parks the service as
/// <c>unsupported</c> — it never starts a thread. <see cref="StopAsync"/> is a deterministic bounded join —
/// one dedicated bounded-join worker owns the device lifecycle, so a stalled device cannot wedge shutdown:
/// stop signal → governor drains (device dispose joins ITS pump thread
/// bounded, then the mixer detaches) → join.
/// </summary>
internal sealed class WorldAudioRenderService : IHostedService {
    /// <summary>The default-endpoint rebind cadence (~1 s) — a contract invariant, not a tunable: fast
    /// enough that plugging headphones in feels immediate, slow enough that a machine with no endpoint idles cheap.</summary>
    public const int RebindPeriodMilliseconds = 1000;
    /// <summary>How often the governor polls a healthy device for a parked fault — comfortably inside the rebind
    /// cadence; the pump itself never waits on this (faults park device-side immediately).</summary>
    private const int FaultPollMilliseconds = 250;

    private readonly WorldAudioDirector m_director;
    private readonly IAudioRenderDeviceFactory? m_factory;
    private readonly WorldAudioMixer m_mixer = new();
    private readonly int m_rebindPeriodMilliseconds;
    private readonly ManualResetEventSlim m_stop = new(initialState: false);
    private IAudioRenderDevice? m_device;
    private string? m_fault;
    private long m_fillFaults;
    private long m_framesDelivered;
    private int m_rebindAttempts;
    private string m_state = "stopped";
    private Thread? m_thread;

    /// <summary>Initializes the service over the director and the platform factory seam.</summary>
    /// <param name="director">The audio director the mixer attaches to.</param>
    /// <param name="factory">The platform render-device factory, or <see langword="null"/> when the platform has no
    /// render backend (the service then reports <c>unsupported</c> and never starts).</param>
    /// <param name="rebindPeriodMilliseconds">The rebind cadence — <see cref="RebindPeriodMilliseconds"/> in
    /// production; the failure-path smoke shortens it to keep the proof fast.</param>
    public WorldAudioRenderService(WorldAudioDirector director, IAudioRenderDeviceFactory? factory, int rebindPeriodMilliseconds = RebindPeriodMilliseconds) {
        ArgumentNullException.ThrowIfNull(argument: director);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: rebindPeriodMilliseconds);
        m_director = director;
        m_factory = factory;
        m_rebindPeriodMilliseconds = rebindPeriodMilliseconds;
    }

    /// <summary>Gets the mixer this service owns (the <c>audio.state</c> verb's meter source).</summary>
    public WorldAudioMixer Mixer => m_mixer;

    /// <summary>Gets the device state token: <c>playing</c>, <c>silent</c> (no endpoint), <c>rebinding</c> (lost
    /// mid-stream), <c>unsupported</c> (no platform backend), or <c>stopped</c>.</summary>
    public string StateToken => Volatile.Read(location: ref m_state);

    /// <summary>Gets the most recent open/stream fault, or <see langword="null"/> while healthy.</summary>
    public string? Fault => Volatile.Read(location: ref m_fault);

    /// <summary>Gets the total frames delivered to the endpoint across every device generation.</summary>
    public long FramesDelivered => (Interlocked.Read(location: ref m_framesDelivered) + (Volatile.Read(location: ref m_device)?.FramesDelivered ?? 0));

    /// <summary>Gets the count of fill callbacks that faulted (each rendered its quantum silent).</summary>
    public long FillFaults => (Interlocked.Read(location: ref m_fillFaults) + (Volatile.Read(location: ref m_device)?.FillFaults ?? 0));

    /// <summary>Gets how many times the service scheduled a device retry (a declined open or a mid-stream loss).</summary>
    public int RebindAttempts => Volatile.Read(location: ref m_rebindAttempts);

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) {
        if (m_factory is null) {
            Volatile.Write(location: ref m_state, value: "unsupported");

            return Task.CompletedTask;
        }

        m_thread = new Thread(start: Govern) {
            IsBackground = true,
            Name = "world-audio",
        };
        m_thread.Start();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) {
        m_stop.Set();
        m_thread?.Join(millisecondsTimeout: 5000);
        m_stop.Dispose();
        Volatile.Write(location: ref m_state, value: "stopped");

        return Task.CompletedTask;
    }

    // The governor: open → attach → watch → (fault) detach → rebind, until stop. Runs on its own thread so a slow
    // endpoint open (or a wedged driver's bounded join) never touches the window pump.
    private void Govern() {
        while (!m_stop.IsSet) {
            var device = m_factory!.TryOpen(sampleRate: WorldAudioMixer.SampleRate, maxQuantumFrames: WorldAudioMixer.MaxBlockFrames, fill: Fill, reason: out var reason);

            if (device is null) {
                Volatile.Write(location: ref m_fault, value: reason);
                Volatile.Write(location: ref m_state, value: "silent");
                _ = Interlocked.Increment(location: ref m_rebindAttempts);

                if (m_stop.Wait(millisecondsTimeout: m_rebindPeriodMilliseconds)) {
                    break;
                }

                continue;
            }

            Volatile.Write(location: ref m_device, value: device);
            Volatile.Write(location: ref m_fault, value: null);
            Volatile.Write(location: ref m_state, value: "playing");
            m_director.AttachMixer(mixer: m_mixer);

            while (!m_stop.Wait(millisecondsTimeout: FaultPollMilliseconds)) {
                if (device.Fault is not null) {
                    break;
                }
            }

            // This generation ends (stop or stream fault): detach FIRST so the dying pump renders silence, fold the
            // device's counters into the accumulated totals, then dispose (a bounded join of the pump thread).
            m_director.DetachMixer();
            Volatile.Write(location: ref m_fault, value: device.Fault);
            _ = Interlocked.Add(location1: ref m_framesDelivered, value: device.FramesDelivered);
            _ = Interlocked.Add(location1: ref m_fillFaults, value: device.FillFaults);
            Volatile.Write(location: ref m_device, value: null);
            device.Dispose();

            if (m_stop.IsSet) {
                break;
            }

            Volatile.Write(location: ref m_state, value: "rebinding");
            _ = Interlocked.Increment(location: ref m_rebindAttempts);

            if (m_stop.Wait(millisecondsTimeout: m_rebindPeriodMilliseconds)) {
                break;
            }
        }
    }

    // The per-quantum fill, on the DEVICE's pump thread: latest-snapshot hold + MixBlock under the director's gate;
    // silence while nothing is published or the mixer is between attaches. The platform pump catches any escaped
    // exception (silent quantum + a counted fill fault), so a mixer defect can never kill the stream.
    private void Fill(Span<short> interleavedStereo) {
        if (!m_director.TryMixBlock(stereoInterleaved: interleavedStereo)) {
            interleavedStereo.Clear();
        }
    }
}
