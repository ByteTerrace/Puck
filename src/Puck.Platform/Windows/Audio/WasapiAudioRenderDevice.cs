using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Platform.Audio;
using Puck.Platform.Windows.Recording;

namespace Puck.Platform.Windows.Audio;

/// <summary>
/// The Windows <see cref="IAudioRenderDeviceFactory"/>: opens the default WASAPI render endpoint as an event-driven
/// shared-mode stereo s16 stream. A missing endpoint or a failed initialize surfaces as a decline reason (never a
/// throw) — the consumer's rebind loop retries above this seam.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiAudioRenderDeviceFactory : IAudioRenderDeviceFactory {
    /// <inheritdoc/>
    public IAudioRenderDevice? TryOpen(int sampleRate, int maxQuantumFrames, AudioRenderFill fill, out string reason) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: maxQuantumFrames);
        ArgumentNullException.ThrowIfNull(argument: fill);

        try {
            reason = "";

            return new WasapiAudioRenderDevice(sampleRate: sampleRate, maxQuantumFrames: maxQuantumFrames, fill: fill);
        } catch (COMException exception) {
            reason = $"render endpoint could not be opened: 0x{exception.HResult:X8} {exception.Message}";

            return null;
        } catch (Exception exception) {
            reason = $"render endpoint could not be opened: {exception.Message}";

            return null;
        }
    }
}

// The WASAPI render device. Cloned from WasapiAudioCaptureSource (the proven template): one dedicated MTA COM thread
// owns enumerator→Activate→Initialize(Shared, event-driven)→GetService(IAudioRenderClient), with a
// ManualResetEventSlim init handshake and a bounded Join on dispose. The stream requests OUR s16/stereo format at the
// caller's rate with AUTOCONVERTPCM|SRC_DEFAULT_QUALITY (audio plan A1: 48000 is the shared-mode native rate on real
// endpoints, so the engine's convert is the trivial s16→float widen; the SRC flags are the exotic-endpoint net, never
// the design point). Per event wake the pump reads GetCurrentPadding and fills the buffer's free space through the
// fill callback in ≤maxQuantumFrames chunks, writing DIRECTLY into GetBuffer's mapping — zero copies, zero
// steady-state allocation. Any failing HRESULT parks the pump and surfaces on Fault; the owner disposes and reopens.
[SupportedOSPlatform("windows")]
internal sealed class WasapiAudioRenderDevice : IAudioRenderDevice {
    // The event wait's watchdog (the capture template's value): the pump re-checks the stop flag at least this often
    // even if the endpoint stops signaling, bounding both dispose latency and a wedged-driver hang.
    private const uint EventTimeoutMilliseconds = 100;

    private readonly AudioRenderFill m_fill;
    private readonly ManualResetEventSlim m_initDone = new(initialState: false);
    private readonly int m_maxQuantumFrames;
    private readonly Thread m_thread;
    private int m_bufferFrames;
    private bool m_disposed;
    private nint m_eventHandle;
    private string? m_fault;
    private long m_fillFaults;
    private long m_framesDelivered;
    private string? m_initError;
    private int m_initHResult;
    private bool m_initOk;
    private volatile bool m_stop;

    public WasapiAudioRenderDevice(int sampleRate, int maxQuantumFrames, AudioRenderFill fill) {
        SampleRate = sampleRate;
        m_maxQuantumFrames = maxQuantumFrames;
        m_fill = fill;
        m_thread = new Thread(start: RenderThread) {
            IsBackground = true,
            Name = "wasapi-render",
        };
        m_thread.SetApartmentState(state: ApartmentState.MTA);
        m_thread.Start();
        m_initDone.Wait();

        if (!m_initOk) {
            m_stop = true;
            m_thread.Join(millisecondsTimeout: 2000);

            throw new COMException(message: (m_initError ?? "the render endpoint failed to initialize"), errorCode: m_initHResult);
        }
    }

    /// <inheritdoc/>
    public int SampleRate { get; }

    /// <inheritdoc/>
    public int BufferFrames => m_bufferFrames;

    /// <inheritdoc/>
    public long FramesDelivered => Volatile.Read(location: ref m_framesDelivered);

    /// <inheritdoc/>
    public long FillFaults => Volatile.Read(location: ref m_fillFaults);

    /// <inheritdoc/>
    public string? Fault => Volatile.Read(location: ref m_fault);

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_stop = true;
        m_thread.Join(millisecondsTimeout: 2000);
        m_initDone.Dispose();

        if (m_eventHandle != 0) {
            _ = CloseHandle(hObject: m_eventHandle);
        }
    }

    private void RenderThread() {
        _ = CoInitializeEx(pvReserved: 0, dwCoInit: 0); // COINIT_MULTITHREADED

        IAudioClient? audioClient = null;
        IAudioRenderClient? renderClient = null;

        try {
            (audioClient, renderClient) = Initialize();
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

        if (m_initOk && (audioClient is not null) && (renderClient is not null)) {
            RunRenderLoop(audioClient: audioClient, renderClient: renderClient);
        }

        if (renderClient is not null) {
            _ = Marshal.ReleaseComObject(o: renderClient);
        }

        if (audioClient is not null) {
            _ = Marshal.ReleaseComObject(o: audioClient);
        }

        CoUninitialize();
    }

    private (IAudioClient AudioClient, IAudioRenderClient RenderClient) Initialize() {
        var enumeratorClsid = Wasapi.CLSID_MMDeviceEnumerator;
        var enumeratorIid = Wasapi.IID_IMMDeviceEnumerator;

        Wasapi.Check(hr: Wasapi.CoCreateInstance(rclsid: ref enumeratorClsid, pUnkOuter: 0, dwClsContext: Wasapi.ClsCtxAll, riid: ref enumeratorIid, ppv: out var enumeratorObject));

        var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pUnk: enumeratorObject);

        _ = Marshal.Release(pUnk: enumeratorObject);
        Wasapi.Check(hr: enumerator.GetDefaultAudioEndpoint(dataFlow: Wasapi.DataFlowRender, role: Wasapi.RoleConsole, ppDevice: out var device));

        var audioClientIid = Wasapi.IID_IAudioClient;

        Wasapi.Check(hr: device.Activate(iid: ref audioClientIid, dwClsCtx: Wasapi.ClsCtxAll, pActivationParams: 0, ppInterface: out var audioClientObject));

        var audioClient = (IAudioClient)audioClientObject;

        // The endpoint's own mix format is only released, not consumed: the stream requests OUR format below and the
        // AUTOCONVERT flags absorb any difference. Calling it first also fails fast on a half-present endpoint.
        Wasapi.Check(hr: audioClient.GetMixFormat(ppDeviceFormat: out var mixFormatPointer));
        Wasapi.CoTaskMemFree(pv: mixFormatPointer);

        var format = new Wasapi.WaveFormatEx {
            wFormatTag = Wasapi.WaveFormatPcm,
            nChannels = 2,
            nSamplesPerSec = (uint)SampleRate,
            nAvgBytesPerSec = (uint)(SampleRate * 4),
            nBlockAlign = 4,
            wBitsPerSample = 16,
            cbSize = 0,
        };
        var formatPointer = Marshal.AllocHGlobal(cb: Marshal.SizeOf<Wasapi.WaveFormatEx>());

        try {
            Marshal.StructureToPtr(structure: format, ptr: formatPointer, fDeleteOld: false);
            // Buffer duration 0 = the engine's default event-driven buffer (~2 device periods, ~20 ms at 48 kHz) —
            // inside the plan's 25-35 ms end-to-end budget with the snapshot pipeline on top; no IAudioClient3 heroics.
            Wasapi.Check(hr: audioClient.Initialize(
                shareMode: Wasapi.ShareModeShared,
                streamFlags: (Wasapi.StreamFlagsEventCallback | Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality),
                hnsBufferDuration: 0,
                hnsPeriodicity: 0,
                pFormat: formatPointer,
                audioSessionGuid: 0
            ));
        } finally {
            Marshal.FreeHGlobal(hglobal: formatPointer);
        }

        m_eventHandle = CreateEventW(lpEventAttributes: 0, bManualReset: false, bInitialState: false, lpName: null);

        if (m_eventHandle == 0) {
            throw new COMException(message: "failed to create the render event", errorCode: Marshal.GetHRForLastWin32Error());
        }

        Wasapi.Check(hr: audioClient.SetEventHandle(eventHandle: m_eventHandle));
        Wasapi.Check(hr: audioClient.GetBufferSize(pNumBufferFrames: out var bufferFrames));
        m_bufferFrames = (int)bufferFrames;

        var renderClientIid = Wasapi.IID_IAudioRenderClient;

        Wasapi.Check(hr: audioClient.GetService(riid: ref renderClientIid, ppv: out var renderClientObject));
        _ = Marshal.ReleaseComObject(o: enumerator);
        _ = Marshal.ReleaseComObject(o: device);

        return (audioClient, (IAudioRenderClient)renderClientObject);
    }

    private void RunRenderLoop(IAudioClient audioClient, IAudioRenderClient renderClient) {
        // Prime the whole buffer with silence before Start so the stream's first period never underruns while the
        // first event is still in flight.
        if (!TryFillAvailable(audioClient: audioClient, renderClient: renderClient, prime: true)) {
            return;
        }

        var hr = audioClient.Start();

        if (hr < 0) {
            Park(hr: hr, where: "Start");

            return;
        }

        while (!m_stop) {
            _ = WaitForSingleObject(hHandle: m_eventHandle, dwMilliseconds: EventTimeoutMilliseconds);

            if (m_stop || !TryFillAvailable(audioClient: audioClient, renderClient: renderClient, prime: false)) {
                break;
            }
        }

        _ = audioClient.Stop();
    }

    // Fill the endpoint buffer's free space in ≤maxQuantumFrames chunks. Returns false (after parking the fault)
    // on any failing HRESULT — including AUDCLNT_E_DEVICE_INVALIDATED mid-stream, the rebind loop's trigger.
    private bool TryFillAvailable(IAudioClient audioClient, IAudioRenderClient renderClient, bool prime) {
        var hr = audioClient.GetCurrentPadding(pNumPaddingFrames: out var padding);

        if (hr < 0) {
            Park(hr: hr, where: "GetCurrentPadding");

            return false;
        }

        var available = (m_bufferFrames - (int)padding);

        while ((available > 0) && !m_stop) {
            var quantum = Math.Min(val1: available, val2: m_maxQuantumFrames);

            hr = renderClient.GetBuffer(numFramesRequested: (uint)quantum, ppData: out var data);

            if (hr < 0) {
                Park(hr: hr, where: "GetBuffer");

                return false;
            }

            var span = SampleSpan(data: data, samples: (quantum * 2));

            if (prime) {
                span.Clear();
            } else {
                try {
                    m_fill(interleavedStereo: span);
                } catch {
                    // The fill callback is world code; a defect there degrades to a silent quantum, never a dead
                    // stream ("plays silent, never crashes").
                    span.Clear();
                    Volatile.Write(location: ref m_fillFaults, value: (m_fillFaults + 1));
                }
            }

            hr = renderClient.ReleaseBuffer(numFramesWritten: (uint)quantum, dwFlags: 0);

            if (hr < 0) {
                Park(hr: hr, where: "ReleaseBuffer");

                return false;
            }

            Volatile.Write(location: ref m_framesDelivered, value: (m_framesDelivered + quantum));
            available -= quantum;
        }

        return true;
    }

    private void Park(int hr, string where) =>
        Volatile.Write(location: ref m_fault, value: $"{where} failed: 0x{hr:X8}{((hr == Wasapi.AudclntEDeviceInvalidated) ? " (device invalidated)" : "")}");

    private static unsafe Span<short> SampleSpan(nint data, int samples) => new((void*)data, samples);

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
