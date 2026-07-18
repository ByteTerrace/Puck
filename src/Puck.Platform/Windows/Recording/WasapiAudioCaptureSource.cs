using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Recording;
using Puck.Platform.Recording;

namespace Puck.Platform.Windows.Recording;

// A WASAPI IAudioCaptureSource. One dedicated thread owns all COM (like the camera grabber): it initializes the audio
// client in shared mode at the device's native mix format (float), then captures into a lock-free drop-oldest ring the
// session drains through Read. The microphone endpoint runs event-driven (SetEventHandle + wait); the render endpoint's
// loopback mode cannot use the event callback, so it polls on a short cadence. Timestamps are QPC-derived on the shared
// session clock, anchored to the first packet's QPC position and advanced by the device sample rate so drops never
// distort the timeline. Overflow drops OLDEST and is counted; the device thread never blocks on the consumer.
[SupportedOSPlatform("windows")]
internal sealed class WasapiAudioCaptureSource : IAudioCaptureSource {
    private const int PollIntervalMilliseconds = 5;

    private readonly RecordingSessionClock m_clock;
    private readonly string? m_deviceId;
    private readonly ManualResetEventSlim m_initDone = new(initialState: false);
    private readonly bool m_loopback;
    private readonly ManualResetEventSlim m_startSignal = new(initialState: false);
    private readonly Thread m_thread;
    private long m_baseSampleNanoseconds;
    private volatile bool m_baseStamped;
    private int m_channels;
    private bool m_disposed;
    private nint m_eventHandle;
    private string? m_initError;
    private int m_initHResult;
    private bool m_initOk;
    private bool m_isFloat;
    private AudioSampleRing? m_ring;
    private int m_sampleRate;
    private volatile bool m_stop;

    public WasapiAudioCaptureSource(RecordingSessionClock clock, bool loopback, string? deviceId) {
        m_clock = clock;
        m_loopback = loopback;
        m_deviceId = deviceId;
        m_thread = new Thread(start: CaptureThread) {
            IsBackground = true,
            Name = (loopback ? "wasapi-loopback" : "wasapi-microphone"),
        };
        m_thread.SetApartmentState(state: ApartmentState.MTA);
        m_thread.Start();
        m_initDone.Wait();

        if (!m_initOk) {
            m_stop = true;
            m_startSignal.Set();
            m_thread.Join(millisecondsTimeout: 2000);

            throw new COMException(message: (m_initError ?? "the audio device failed to initialize"), errorCode: m_initHResult);
        }
    }

    /// <inheritdoc/>
    public int SampleRate => m_sampleRate;

    /// <inheritdoc/>
    public int Channels => m_channels;

    /// <inheritdoc/>
    public long DroppedSampleCount => (m_ring?.DroppedSampleCount ?? 0);

    /// <inheritdoc/>
    public void Start() {
        if (m_disposed) {
            return;
        }

        m_startSignal.Set();
    }

    /// <inheritdoc/>
    public void Stop() => m_stop = true;

    /// <inheritdoc/>
    public int Read(Span<float> interleaved, out long firstSampleTimestampNanoseconds) {
        firstSampleTimestampNanoseconds = 0;

        if ((m_ring is null) || (m_channels == 0)) {
            return 0;
        }

        var (count, firstSampleIndex) = m_ring.Read(destination: interleaved);

        if (count == 0) {
            return 0;
        }

        // Whole interleaved frames only: never split a frame across reads.
        count -= (count % m_channels);

        if (count == 0) {
            return 0;
        }

        var frameOffset = (firstSampleIndex / m_channels);

        firstSampleTimestampNanoseconds = (Volatile.Read(location: ref m_baseSampleNanoseconds) + ((frameOffset * 1_000_000_000L) / m_sampleRate));

        return count;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_stop = true;
        m_startSignal.Set();
        m_thread.Join(millisecondsTimeout: 2000);
        m_initDone.Dispose();
        m_startSignal.Dispose();

        if (m_eventHandle != 0) {
            _ = CloseHandle(hObject: m_eventHandle);
        }
    }

    private void CaptureThread() {
        _ = CoInitializeEx(pvReserved: 0, dwCoInit: 0); // COINIT_MULTITHREADED

        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;

        try {
            (audioClient, captureClient) = Initialize();
            m_initOk = true;
        } catch (COMException exception) {
            m_initError = exception.Message;
            m_initHResult = exception.HResult;
            m_initOk = false;
        } catch (Exception exception) {
            m_initError = exception.Message;
            m_initHResult = exception.HResult;
            m_initOk = false;
        } finally {
            m_initDone.Set();
        }

        if (!m_initOk || (audioClient is null) || (captureClient is null)) {
            CoUninitialize();

            return;
        }

        m_startSignal.Wait();

        if (!m_stop) {
            RunCaptureLoop(audioClient: audioClient, captureClient: captureClient);
        }

        _ = Marshal.ReleaseComObject(o: captureClient);
        _ = Marshal.ReleaseComObject(o: audioClient);
        CoUninitialize();
    }

    private (IAudioClient AudioClient, IAudioCaptureClient CaptureClient) Initialize() {
        var enumeratorClsid = Wasapi.CLSID_MMDeviceEnumerator;
        var enumeratorIid = Wasapi.IID_IMMDeviceEnumerator;

        Wasapi.Check(hr: Wasapi.CoCreateInstance(rclsid: ref enumeratorClsid, pUnkOuter: 0, dwClsContext: Wasapi.ClsCtxAll, riid: ref enumeratorIid, ppv: out var enumeratorObject));

        var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pUnk: enumeratorObject);

        _ = Marshal.Release(pUnk: enumeratorObject);

        // Loopback captures the render endpoint; the microphone captures a capture endpoint (named or default).
        IMMDevice device;

        if (m_deviceId is not null) {
            Wasapi.Check(hr: enumerator.GetDevice(pwstrId: m_deviceId, ppDevice: out device));
        } else {
            var dataFlow = (m_loopback ? Wasapi.DataFlowRender : Wasapi.DataFlowCapture);

            Wasapi.Check(hr: enumerator.GetDefaultAudioEndpoint(dataFlow: dataFlow, role: Wasapi.RoleConsole, ppDevice: out device));
        }

        var audioClientIid = Wasapi.IID_IAudioClient;

        Wasapi.Check(hr: device.Activate(iid: ref audioClientIid, dwClsCtx: Wasapi.ClsCtxAll, pActivationParams: 0, ppInterface: out var audioClientObject));

        var audioClient = (IAudioClient)audioClientObject;

        Wasapi.Check(hr: audioClient.GetMixFormat(ppDeviceFormat: out var formatPointer));

        try {
            var format = Marshal.PtrToStructure<Wasapi.WaveFormatEx>(ptr: formatPointer);

            m_sampleRate = (int)format.nSamplesPerSec;
            m_channels = format.nChannels;
            m_isFloat = DetermineIsFloat(format: format, formatPointer: formatPointer);

            if (!m_isFloat && (format.wBitsPerSample != 16)) {
                throw new COMException(message: $"unsupported device format ({format.wBitsPerSample}-bit, tag {format.wFormatTag})", errorCode: unchecked((int)0x80004005));
            }

            var streamFlags = (m_loopback ? Wasapi.StreamFlagsLoopback : Wasapi.StreamFlagsEventCallback);
            // Loopback polls; the microphone is event-driven with a device-picked buffer (period 0).
            var bufferDuration = (m_loopback ? 2_000_000L : 0L);

            Wasapi.Check(hr: audioClient.Initialize(shareMode: Wasapi.ShareModeShared, streamFlags: streamFlags, hnsBufferDuration: bufferDuration, hnsPeriodicity: 0, pFormat: formatPointer, audioSessionGuid: 0));

            if (!m_loopback) {
                m_eventHandle = CreateEventW(lpEventAttributes: 0, bManualReset: false, bInitialState: false, lpName: null);

                if (m_eventHandle == 0) {
                    throw new COMException(message: "failed to create the capture event", errorCode: Marshal.GetHRForLastWin32Error());
                }

                Wasapi.Check(hr: audioClient.SetEventHandle(eventHandle: m_eventHandle));
            }

            Wasapi.Check(hr: audioClient.GetBufferSize(pNumBufferFrames: out var bufferFrames));

            // Ring holds ~1 s of audio so a briefly-stalled consumer drops oldest rather than the device thread blocking.
            m_ring = new AudioSampleRing(capacity: Math.Max(val1: ((int)bufferFrames * m_channels * 4), val2: (m_sampleRate * m_channels)));

            var captureClientIid = Wasapi.IID_IAudioCaptureClient;

            Wasapi.Check(hr: audioClient.GetService(riid: ref captureClientIid, ppv: out var captureClientObject));
            _ = Marshal.ReleaseComObject(o: enumerator);
            _ = Marshal.ReleaseComObject(o: device);

            return (audioClient, (IAudioCaptureClient)captureClientObject);
        } finally {
            Wasapi.CoTaskMemFree(pv: formatPointer);
        }
    }

    private static bool DetermineIsFloat(Wasapi.WaveFormatEx format, nint formatPointer) {
        if (format.wFormatTag == Wasapi.WaveFormatIeeeFloat) {
            return true;
        }

        if (format.wFormatTag == Wasapi.WaveFormatExtensible) {
            // The SubFormat GUID sits after the WAVEFORMATEX header + wValidBitsPerSample(2) + dwChannelMask(4).
            var subFormat = Marshal.PtrToStructure<Guid>(ptr: (formatPointer + 18 + 2 + 4));

            return (subFormat == Wasapi.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
        }

        return false;
    }

    private void RunCaptureLoop(IAudioClient audioClient, IAudioCaptureClient captureClient) {
        Wasapi.Check(hr: audioClient.Start());

        var scratch = new float[m_sampleRate * m_channels];

        try {
            while (!m_stop) {
                if (m_loopback) {
                    Thread.Sleep(millisecondsTimeout: PollIntervalMilliseconds);
                } else {
                    _ = WaitForSingleObject(hHandle: m_eventHandle, dwMilliseconds: 100);
                }

                DrainPackets(captureClient: captureClient, scratch: scratch);
            }
        } finally {
            _ = audioClient.Stop();
        }
    }

    private void DrainPackets(IAudioCaptureClient captureClient, float[] scratch) {
        while (captureClient.GetNextPacketSize(pNumFramesInNextPacket: out var packetFrames) >= 0) {
            if (packetFrames == 0) {
                return;
            }

            if (captureClient.GetBuffer(ppData: out var data, pNumFramesToRead: out var frames, pdwFlags: out var flags, pu64DevicePosition: out _, pu64QpcPosition: out var qpcPosition) < 0) {
                return;
            }

            try {
                var sampleCount = (int)(frames * (uint)m_channels);

                if (sampleCount > scratch.Length) {
                    scratch = new float[sampleCount];
                }

                var span = scratch.AsSpan(start: 0, length: sampleCount);

                if ((flags & Wasapi.BufferFlagsSilent) != 0) {
                    span.Clear();
                } else if (m_isFloat) {
                    Marshal.Copy(source: data, destination: scratch, startIndex: 0, length: sampleCount);
                } else {
                    ConvertInt16(source: data, destination: span);
                }

                StampBaseIfNeeded(qpcPosition: qpcPosition);
                m_ring!.Write(samples: span);
            } finally {
                _ = captureClient.ReleaseBuffer(numFramesRead: frames);
            }
        }
    }

    private void StampBaseIfNeeded(ulong qpcPosition) {
        if (m_baseStamped) {
            return;
        }

        // QPC position is in 100-ns units on the shared clock's domain; fall back to the clock's own now if the driver
        // reports zero.
        var baseNanoseconds = ((qpcPosition != 0)
            ? m_clock.NanosecondsFromHectonanoseconds(hectonanoseconds: (long)qpcPosition)
            : m_clock.NowNanoseconds());

        Volatile.Write(location: ref m_baseSampleNanoseconds, value: baseNanoseconds);
        m_baseStamped = true;
    }

    private static void ConvertInt16(nint source, Span<float> destination) {
        unsafe {
            var pointer = (short*)source;

            for (var i = 0; (i < destination.Length); i++) {
                destination[i] = (pointer[i] / 32768f);
            }
        }
    }

    [DllImport("Ole32.dll")]
    private static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);
    [DllImport("Ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern nint CreateEventW(nint lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, [MarshalAs(UnmanagedType.LPWStr)] string? lpName);
    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);
    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
