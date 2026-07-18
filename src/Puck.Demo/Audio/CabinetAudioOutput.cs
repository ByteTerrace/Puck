using System.Runtime.InteropServices;
using Puck.Abstractions.Machines;

namespace Puck.Demo.Audio;

/// <summary>
/// One booted cabinet's speaker path: an OS waveform-audio output stream that drains a machine's neutral
/// <see cref="IAudioMachine"/> capability once per produced frame and queues the mixed stereo frames for playback.
/// One path for every core — <see cref="HumbleAudioMachine"/> and <see cref="AdvancedAudioMachine"/> adapt the SM83
/// and native ARM7TDMI cores' own sinks onto it, so this class never names a concrete machine. Each cabinet owns its
/// own stream and the OS audio engine mixes the open streams — the simplest good multi-cabinet default (all booted
/// cabinets are audible at once, like a real arcade room). Strictly OUTPUT-ONLY: it reads the machine's bounded ring
/// (host-facing plumbing the emulator's determinism contract already excludes) and writes to the OS; nothing flows
/// back toward the simulation. The queue is self-regulating — at most <see cref="MaxQueuedHeaders"/> buffers in
/// flight (a latency bound of roughly a quarter second worst-case; typically one or two ~17 ms buffers), and when the
/// device is saturated the pump simply leaves samples buffered, which itself keeps only the newest emulated second.
/// A short silence cushion is queued ahead of the first real samples so frame-pacing jitter does not click. Any API
/// failure downgrades to silence rather than disturbing the demo.
/// </summary>
internal sealed unsafe class CabinetAudioOutput : IDisposable {
    /// <summary>The stream rate: 2¹⁵ frames per emulated second — an exact divisor of the 4194304 Hz mixer clock, so
    /// the emulator's rational resampler emits perfectly even spacing (any rate is exact; this one is also tidy).</summary>
    public const int SampleRate = 32_768;

    private const int BufferSampleCapacity = (MaxFramesPerHeader * 2);
    private const int HeaderCount = 8;
    private const int MaxFramesPerHeader = 8_192;   // 250 ms per buffer, worst case.
    private const int MaxQueuedHeaders = 6;         // The latency/backlog bound.
    private const int PrimeSilenceSamples = (2_048 * 2); // A ~62 ms cushion ahead of the first real samples.

    private readonly nint[] m_buffers;
    private readonly nint m_device;
    private readonly nint[] m_headers;
    private readonly bool[] m_written;
    private bool m_disposed;
    private bool m_failed;
    private bool m_primed;

    private CabinetAudioOutput(nint device) {
        m_device = device;
        m_buffers = new nint[HeaderCount];
        m_headers = new nint[HeaderCount];
        m_written = new bool[HeaderCount];

        for (var index = 0; (index < HeaderCount); index++) {
            m_buffers[index] = ((nint)NativeMemory.AllocZeroed(byteCount: (BufferSampleCapacity * sizeof(short))));
            m_headers[index] = ((nint)NativeMemory.AllocZeroed(byteCount: (nuint)sizeof(WaveHeader)));
        }
    }

    /// <summary>Opens a stream on the default output device, or returns <see langword="null"/> where the API is
    /// unavailable (non-Windows hosts, no audio device) — the caller simply runs silent.</summary>
    /// <returns>The stream, or <see langword="null"/>.</returns>
    public static CabinetAudioOutput? TryOpen() {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        var format = new WaveFormat {
            AverageBytesPerSecond = (SampleRate * 4),
            BitsPerSample = 16,
            BlockAlign = 4,
            ChannelCount = 2,
            ExtraByteCount = 0,
            FormatTag = WaveOut.FormatPcm,
            SamplesPerSecond = SampleRate,
        };
        var result = WaveOut.Open(handle: out var device, deviceId: WaveOut.DeviceMapper, format: in format, callback: 0, instance: 0, flags: 0);

        if (result != WaveOut.NoError) {
            Console.Error.WriteLine(value: $"[audio] waveOutOpen failed ({result}) — this cabinet plays silent.");

            return null;
        }

        return new CabinetAudioOutput(device: device);
    }

    /// <summary>Drains whatever <paramref name="machine"/> has buffered into the playback queue — the one path every
    /// core's cabinet pumps through, regardless of which core produced the audio. It queries no available-count (a
    /// core-neutral capability exposes only the drain), so the pump queues its silence cushion on the first call —
    /// which is exactly when playback begins — then drains buffer-by-buffer until a drain returns nothing. Called
    /// once per produced frame after the machine stepped; a pure read of host-facing audio, so nothing flows back
    /// toward the simulation.</summary>
    /// <param name="machine">The machine's neutral audio capability.</param>
    public void Pump(IAudioMachine machine) {
        ArgumentNullException.ThrowIfNull(machine);

        if (m_disposed || m_failed) {
            return;
        }

        if (!m_primed) {
            m_primed = true;

            PumpSilenceCushion();
        }

        while (true) {
            var index = TryClaimHeader();

            if (index < 0) {
                return; // Device saturated: leave the rest buffered (the machine keeps only the newest emulated second).
            }

            var written = machine.ReadSamples(destination: new Span<short>(pointer: (void*)m_buffers[index], length: BufferSampleCapacity));

            if (written == 0) {
                return; // Nothing buffered: the claimed header was never written, so it stays free for the next pump.
            }

            Submit(index: index, sampleCount: written);
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        _ = WaveOut.Reset(handle: m_device); // Returns every queued buffer (marks it done) so the unprepare below is legal.

        for (var index = 0; (index < HeaderCount); index++) {
            if (m_written[index]) {
                _ = WaveOut.UnprepareHeader(handle: m_device, header: m_headers[index], headerByteLength: (uint)sizeof(WaveHeader));
            }

            NativeMemory.Free(ptr: (void*)m_buffers[index]);
            NativeMemory.Free(ptr: (void*)m_headers[index]);
        }

        _ = WaveOut.Close(handle: m_device);
    }

    // The start-up cushion: one buffer of silence ahead of the first real samples, so the queue is never empty
    // between the early frames while their pacing settles.
    private void PumpSilenceCushion() {
        var index = TryClaimHeader();

        if (index < 0) {
            return;
        }

        new Span<short>(pointer: (void*)m_buffers[index], length: PrimeSilenceSamples).Clear();
        Submit(index: index, sampleCount: PrimeSilenceSamples);
    }

    // Recycles finished buffers (unpreparing them) and returns a free one, or -1 when the in-flight count has hit
    // the latency bound.
    private int TryClaimHeader() {
        var free = -1;
        var inFlight = 0;

        for (var index = 0; (index < HeaderCount); index++) {
            if (!m_written[index]) {
                free = ((free < 0) ? index : free);

                continue;
            }

            var header = ((WaveHeader*)m_headers[index]);

            if ((header->Flags & WaveOut.FlagDone) != 0) {
                _ = WaveOut.UnprepareHeader(handle: m_device, header: m_headers[index], headerByteLength: (uint)sizeof(WaveHeader));
                m_written[index] = false;
                free = ((free < 0) ? index : free);
            } else {
                ++inFlight;
            }
        }

        return ((inFlight >= MaxQueuedHeaders) ? -1 : free);
    }

    // Prepares and queues one filled buffer; any failure downgrades the stream to permanent silence.
    private void Submit(int index, int sampleCount) {
        var header = ((WaveHeader*)m_headers[index]);

        header->BufferByteLength = ((uint)(sampleCount * sizeof(short)));
        header->BytesRecorded = 0;
        header->Data = m_buffers[index];
        header->Flags = 0;
        header->LoopCount = 0;
        header->Next = 0;
        header->Reserved = 0;
        header->UserData = 0;

        if (WaveOut.PrepareHeader(handle: m_device, header: m_headers[index], headerByteLength: (uint)sizeof(WaveHeader)) != WaveOut.NoError) {
            m_failed = true;

            return;
        }

        if (WaveOut.Write(handle: m_device, header: m_headers[index], headerByteLength: (uint)sizeof(WaveHeader)) != WaveOut.NoError) {
            _ = WaveOut.UnprepareHeader(handle: m_device, header: m_headers[index], headerByteLength: (uint)sizeof(WaveHeader));
            m_failed = true;

            return;
        }

        m_written[index] = true;
    }
}
