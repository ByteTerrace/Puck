using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Recording;

namespace Puck.Platform.Windows.Recording;

// A WASAPI IAudioCaptureSource. One dedicated thread owns all COM (like the camera grabber): it initializes the audio
// client in shared mode at the device's native mix format (float), then captures into a lock-free drop-oldest ring the
// session drains through Read, and sets DataAvailable after each write so the session waits on it instead of sleeping.
// The endpoint runs event-driven (SetEventHandle + wait); a render endpoint's loopback runs so where the system allows
// the event callback with the loopback flag, and otherwise polls on a short cadence. Timestamps are QPC-derived on the shared
// session clock, anchored to the first packet's QPC position and advanced by the device sample rate so drops never
// distort the timeline. Overflow drops OLDEST and is counted; the device thread never blocks on the consumer.
[SupportedOSPlatform("windows")]
internal sealed class WasapiAudioCaptureSource : IAudioCaptureSource {
    private const int PollIntervalMilliseconds = 5;

    private readonly RecordingSessionClock m_clock;
    private readonly string? m_deviceId;

    private readonly AutoResetEvent m_dataAvailable = new(initialState: false);

    private readonly ManualResetEventSlim m_initDone = new(initialState: false);

    private readonly bool m_loopback;

    private readonly ManualResetEventSlim m_startSignal = new(initialState: false);

    private readonly Thread m_thread;

    private long m_baseSampleNanoseconds;
    private volatile bool m_baseStamped;
    private int m_channels;
    private bool m_disposed;
    private bool m_eventDriven;
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
            Name = (loopback
            ? "wasapi-loopback"
            : "wasapi-microphone"),
        };
        m_thread.SetApartmentState(state: ApartmentState.MTA);
        m_thread.Start();
        m_initDone.Wait();

        if (!m_initOk) {
            m_stop = true;
            m_startSignal.Set();
            m_thread.Join(millisecondsTimeout: 2000);

            throw new COMException(
                errorCode: m_initHResult,
                message: (m_initError ?? "the audio device failed to initialize")
            );
        }
    }

    /// <inheritdoc/>
    public int SampleRate => m_sampleRate;
    /// <inheritdoc/>
    public int Channels => m_channels;
    /// <inheritdoc/>
    public long DroppedSampleCount => (m_ring?.DroppedSampleCount ?? 0);
    /// <inheritdoc/>
    public WaitHandle DataAvailable => m_dataAvailable;

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

        if (
            (m_ring is null) ||
            (m_channels == 0)
        ) {
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
        m_dataAvailable.Dispose();

        if (m_eventHandle != 0) {
            _ = Wasapi.CloseHandle(hObject: m_eventHandle);
        }
    }

    private void CaptureThread() {
        _ = Wasapi.CoInitializeEx(
            dwCoInit: 0,
            pvReserved: 0
        ); // COINIT_MULTITHREADED

        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;

        try {
            (audioClient, captureClient) = Open();
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

        if (
            !m_initOk ||
            (audioClient is null) ||
            (captureClient is null)
        ) {
            Wasapi.CoUninitialize();

            return;
        }

        m_startSignal.Wait();

        if (!m_stop) {
            RunCaptureLoop(
                audioClient: audioClient,
                captureClient: captureClient
            );
        }

        _ = Marshal.ReleaseComObject(o: captureClient);
        _ = Marshal.ReleaseComObject(o: audioClient);
        Wasapi.CoUninitialize();
    }
    // The microphone is always event-driven. A loopback is too where the system supports the event callback on a render
    // endpoint's loopback (Windows 10 build 20348 and later, per IAudioClient::Initialize's remarks); elsewhere Initialize
    // refuses the combination and a fresh client polls instead.
    private (IAudioClient AudioClient, IAudioCaptureClient CaptureClient) Open() {
        if (!m_loopback) {
            return Initialize(eventDriven: true);
        }

        try {
            return Initialize(eventDriven: true);
        } catch (COMException) {
            return Initialize(eventDriven: false);
        }
    }
    private (IAudioClient AudioClient, IAudioCaptureClient CaptureClient) Initialize(bool eventDriven) {
        // Loopback captures the render endpoint; the microphone captures a capture endpoint (named or default).
        var audioClient = Wasapi.ActivateAudioClient(
            dataFlow: (m_loopback
            ? Wasapi.DataFlowRender
            : Wasapi.DataFlowCapture),
            deviceId: m_deviceId
        );

        Wasapi.Check(hr: audioClient.GetMixFormat(ppDeviceFormat: out var formatPointer));

        try {
            var format = Marshal.PtrToStructure<Wasapi.WaveFormatEx>(ptr: formatPointer);

            m_sampleRate = ((int)format.nSamplesPerSec);
            m_channels = format.nChannels;
            m_isFloat = DetermineIsFloat(
                format: format,
                formatPointer: formatPointer
            );

            if (
                !m_isFloat &&
                (format.wBitsPerSample != 16)
            ) {
                throw new COMException(
                    errorCode: unchecked((int)0x80004005),
                    message: $"unsupported device format ({format.wBitsPerSample}-bit, tag {format.wFormatTag})"
                );
            }

            var streamFlags = (m_loopback
                ? Wasapi.StreamFlagsLoopback
                : 0u
            );
            // Event-driven capture takes a device-picked buffer (period 0); polling a loopback takes its own.
            var bufferDuration = 0L;

            if (eventDriven) {
                streamFlags |= Wasapi.StreamFlagsEventCallback;
            } else {
                bufferDuration = 2_000_000L;
            }

            Wasapi.Check(hr: audioClient.Initialize(
                audioSessionGuid: 0,
                hnsBufferDuration: bufferDuration,
                hnsPeriodicity: 0,
                pFormat: formatPointer,
                shareMode: Wasapi.ShareModeShared,
                streamFlags: streamFlags
            ));

            if (eventDriven) {
                m_eventHandle = Wasapi.CreateEventW(
                    bInitialState: false,
                    bManualReset: false,
                    lpEventAttributes: 0,
                    lpName: null
                );

                if (m_eventHandle == 0) {
                    throw new COMException(
                        message: "failed to create the capture event",
                        errorCode: Marshal.GetHRForLastWin32Error()
                    );
                }

                Wasapi.Check(hr: audioClient.SetEventHandle(eventHandle: m_eventHandle));
            }

            Wasapi.Check(hr: audioClient.GetBufferSize(pNumBufferFrames: out var bufferFrames));

            // Ring holds ~1 s of audio so a briefly-stalled consumer drops oldest rather than the device thread blocking.
            m_ring = new AudioSampleRing(capacity: Math.Max(
                val1: ((((int)bufferFrames) * m_channels) * 4),
                val2: (m_sampleRate * m_channels)
            ));

            var captureClientIid = Wasapi.IID_IAudioCaptureClient;

            Wasapi.Check(hr: audioClient.GetService(
                ppv: out var captureClientObject,
                riid: ref captureClientIid
            ));

            m_eventDriven = eventDriven;
            return (audioClient, ((IAudioCaptureClient)captureClientObject));
        } catch {
            // A refused initialization leaves a client no later call can reuse; the caller's retry activates its own.
            _ = Marshal.ReleaseComObject(o: audioClient);

            if (m_eventHandle != 0) {
                _ = Wasapi.CloseHandle(hObject: m_eventHandle);
                m_eventHandle = 0;
            }

            throw;
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
            var subFormat = Marshal.PtrToStructure<Guid>(ptr: (((formatPointer + 18) + 2) + 4));

            return (subFormat == Wasapi.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
        }

        return false;
    }
    private void RunCaptureLoop(IAudioClient audioClient, IAudioCaptureClient captureClient) {
        Wasapi.Check(hr: audioClient.Start());

        var scratch = new float[(m_sampleRate * m_channels)];

        try {
            while (!m_stop) {
                if (m_eventDriven) {
                    // The device sets the event per packet; the timeout bounds a stop request and a silent stream.
                    _ = Wasapi.WaitForSingleObject(
                        dwMilliseconds: 100,
                        hHandle: m_eventHandle
                    );
                } else {
                    Thread.Sleep(millisecondsTimeout: PollIntervalMilliseconds);
                }

                DrainPackets(
                    captureClient: captureClient,
                    scratch: scratch
                );
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

            if (captureClient.GetBuffer(
                pNumFramesToRead: out var frames,
                pdwFlags: out var flags,
                ppData: out var data,
                pu64DevicePosition: out _,
                pu64QpcPosition: out var qpcPosition
            ) < 0) {
                return;
            }

            try {
                var sampleCount = ((int)(frames * ((uint)m_channels)));

                if (sampleCount > scratch.Length) {
                    scratch = new float[sampleCount];
                }

                var span = scratch.AsSpan(
                    length: sampleCount,
                    start: 0
                );

                if ((flags & Wasapi.BufferFlagsSilent) != 0) {
                    span.Clear();
                } else if (m_isFloat) {
                    Marshal.Copy(
                        destination: scratch,
                        length: sampleCount,
                        source: data,
                        startIndex: 0
                    );
                } else {
                    ConvertInt16(
                        destination: span,
                        source: data
                    );
                }

                StampBaseIfNeeded(qpcPosition: qpcPosition);
                m_ring!.Write(samples: span);
                _ = m_dataAvailable.Set();
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
            ? m_clock.NanosecondsFromHectonanoseconds(hectonanoseconds: ((long)qpcPosition))
            : m_clock.NowNanoseconds()
        );

        Volatile.Write(
            location: ref m_baseSampleNanoseconds,
            value: baseNanoseconds
        );
        m_baseStamped = true;
    }
    private static void ConvertInt16(nint source, Span<float> destination) {
        unsafe {
            var pointer = ((short*)source);

            for (var i = 0; (i < destination.Length); i++) {
                destination[i] = (pointer[i] / 32768f);
            }
        }
    }
}
