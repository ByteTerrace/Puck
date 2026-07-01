using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.Platform.Windows;

/// <summary>
/// The Media Foundation GPU-tier session (<see cref="ICameraSharedCaptureSession"/>): frames stay GPU-resident end to
/// end. A dedicated MTA grabber thread owns all state — Media Foundation startup, a Direct3D 11 video device on the
/// consumer's adapter (<see cref="Win32D3D11VideoDevice"/>), the DXGI device manager, the source reader configured with
/// <c>MF_SOURCE_READER_D3D_MANAGER</c> + advanced (GPU) video processing, and the ReadSample loop. The reader's DXVA
/// video processor converts each captured frame to ARGB32 <em>on the GPU</em>; the loop pulls the sample's
/// <c>ID3D11Texture2D</c> through <see cref="IMFDXGIBuffer"/>, copies it into the next consumer-provisioned shared
/// target (round-robin), drains the copy with a CPU fence (grabber thread, camera cadence), and only then publishes the
/// slot — so a published slot is always complete for the consumer's render device to sample. No frame ever visits host
/// memory.
/// <para>Two-phase: the constructor negotiates (device + reader + output size); <see cref="Start"/> hands over the
/// shared targets and begins streaming.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class Win32MediaFoundationSharedCameraSession : ICameraSharedCaptureSession {
    private readonly long m_adapterLuid;
    private readonly ManualResetEventSlim m_initDone = new(initialState: false);
    private readonly int m_requestedHeight;
    private readonly int m_requestedWidth;
    private readonly ManualResetEventSlim m_startSignal = new(initialState: false);
    private readonly Thread m_thread;

    private bool m_disposed;
    private volatile bool m_ended;
    private int m_height;
    private string? m_initError;
    private bool m_initOk;
    private long m_lastTimestamp;
    private volatile int m_latestSlot = -1;
    private string m_name = "camera";
    private nint[] m_targetHandles = [];
    private long m_version;
    private volatile bool m_stop;
    private int m_width;

    public Win32MediaFoundationSharedCameraSession(long adapterLuid, int requestedWidth, int requestedHeight) {
        m_adapterLuid = adapterLuid;
        m_requestedHeight = requestedHeight;
        m_requestedWidth = requestedWidth;
        m_thread = new Thread(start: GrabberLoop) {
            IsBackground = true,
            Name = "camera-gpu-grabber",
        };
        m_thread.SetApartmentState(state: ApartmentState.MTA);
        m_thread.Start();
        m_initDone.Wait();

        if (!m_initOk) {
            m_stop = true;
            m_startSignal.Set();
            m_thread.Join(millisecondsTimeout: 2000);

            throw new InvalidOperationException(message: (m_initError ?? "the camera failed to initialize on the GPU tier"));
        }
    }

    /// <inheritdoc/>
    public long FrameVersion => Interlocked.Read(location: ref m_version);
    /// <inheritdoc/>
    public bool IsEnded => m_ended;
    /// <inheritdoc/>
    public long LastFrameTimestamp => Interlocked.Read(location: ref m_lastTimestamp);
    /// <inheritdoc/>
    public int Height => m_height;
    /// <inheritdoc/>
    public int LatestSlot => m_latestSlot;
    /// <inheritdoc/>
    public string Name => m_name;
    /// <inheritdoc/>
    public int Width => m_width;

    /// <inheritdoc/>
    public void Start(IReadOnlyList<nint> sharedTargetHandles) {
        ArgumentNullException.ThrowIfNull(sharedTargetHandles);
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        if (0 == sharedTargetHandles.Count) {
            throw new ArgumentException(message: "At least one shared target is required.", paramName: nameof(sharedTargetHandles));
        }

        if (0 != m_targetHandles.Length) {
            throw new InvalidOperationException(message: "the session has already been started");
        }

        m_targetHandles = [.. sharedTargetHandles];
        m_startSignal.Set();
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_stop = true;
        m_initDone.Set();
        m_startSignal.Set();
        m_thread.Join(millisecondsTimeout: 2000);
        m_initDone.Dispose();
        m_startSignal.Dispose();
    }

    // The whole GPU-capture lifetime lives on this one MTA thread: initialize (device + manager + reader + negotiate,
    // signalling success/failure back to the ctor), wait for Start's targets, open them, then loop ReadSample copying
    // each GPU frame into the next slot, then tear everything down.
    private void GrabberLoop() {
        Win32D3D11VideoDevice? device = null;
        IMFDXGIDeviceManager? manager = null;
        IMFSourceReader? reader = null;
        var started = false;
        var targets = Array.Empty<nint>();

        try {
            try {
                Check(hr: MfInterop.MFStartup(Version: MfInterop.MfVersion, dwFlags: 0));

                started = true;
                device = new Win32D3D11VideoDevice(adapterLuid: m_adapterLuid);

                Check(hr: MfInterop.MFCreateDXGIDeviceManager(pResetToken: out var resetToken, ppDeviceManager: out manager));
                Check(hr: manager.ResetDevice(pUnkDevice: device.DevicePointer, resetToken: resetToken));

                reader = OpenReader(manager: manager);
                m_initOk = true;
            } catch (Exception exception) {
                m_initError = exception.Message;
                m_initOk = false;
            } finally {
                m_initDone.Set();
            }

            if (!m_initOk) {
                return;
            }

            m_startSignal.Wait();

            if (m_stop) {
                return;
            }

            // Open the consumer-provisioned shared targets on this decode device; they are the only textures the loop
            // ever writes, so their pointers live for the whole session.
            targets = new nint[m_targetHandles.Length];

            try {
                for (var index = 0; (index < m_targetHandles.Length); index++) {
                    targets[index] = device!.OpenSharedTexture(sharedHandle: m_targetHandles[index]);
                }
            } catch (Exception exception) {
                Console.Error.WriteLine(value: $"[camera] '{m_name}' failed to open a shared target on the decode device: {exception.Message}");

                return;
            }

            ReadLoop(device: device!, reader: reader!, targets: targets);
        } finally {
            // Whatever ended the grabber (unplug, end of stream, a failed target open, stop), the feed will never
            // publish again — the consumer's tear-down/re-open signal.
            m_ended = true;

            foreach (var target in targets) {
                Win32D3D11VideoDevice.ReleaseTexture(texture: target);
            }

            if (reader is not null) {
                _ = Marshal.ReleaseComObject(o: reader);
            }

            if (manager is not null) {
                _ = Marshal.ReleaseComObject(o: manager);
            }

            device?.Dispose();

            if (started) {
                _ = MfInterop.MFShutdown();
            }
        }
    }

    private IMFSourceReader OpenReader(IMFDXGIDeviceManager manager) {
        // Enumerate video capture devices, pick the first (mirrors the CPU session).
        Check(hr: MfInterop.MFCreateAttributes(ppMFAttributes: out var enumConfig, cInitialSize: 1));

        var sourceTypeKey = MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
        var vidcap = MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP;

        Check(hr: enumConfig.SetGUID(guidKey: ref sourceTypeKey, guidValue: ref vidcap));
        Check(hr: MfInterop.MFEnumDeviceSources(pAttributes: enumConfig, pppSourceActivate: out var devices, pcSourceActivate: out var count));

        if ((0 == count) || (0 == devices)) {
            throw new InvalidOperationException(message: "no video capture devices were found");
        }

        var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pUnk: Marshal.ReadIntPtr(ptr: devices));

        for (var index = 0; (index < count); index++) {
            _ = Marshal.Release(pUnk: Marshal.ReadIntPtr(ptr: devices, ofs: (index * IntPtr.Size)));
        }

        Marshal.FreeCoTaskMem(ptr: devices);

        var nameKey = MfInterop.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME;

        if (activate.GetAllocatedString(guidKey: ref nameKey, ppwszValue: out var deviceName, pcchLength: out _) >= 0) {
            m_name = deviceName;
        }

        var sourceIid = MfInterop.IID_IMFMediaSource;

        Check(hr: activate.ActivateObject(riid: ref sourceIid, ppv: out var mediaSource));

        // The GPU-tier reader: the D3D manager makes samples GPU textures on our device; ADVANCED video processing
        // enables the DXVA VideoProcessor, which performs the NV12/YUY2 -> ARGB32 conversion (and any scaling) on-GPU.
        Check(hr: MfInterop.MFCreateAttributes(ppMFAttributes: out var readerConfig, cInitialSize: 2));

        var managerKey = MfInterop.MF_SOURCE_READER_D3D_MANAGER;

        Check(hr: readerConfig.SetUnknown(guidKey: ref managerKey, punkValue: manager));

        var advancedProcessing = MfInterop.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING;

        Check(hr: readerConfig.SetUINT32(guidKey: ref advancedProcessing, unValue: 1));
        Check(hr: MfInterop.MFCreateSourceReaderFromMediaSource(pMediaSource: mediaSource, pAttributes: readerConfig, ppSourceReader: out var reader));
        Check(hr: reader.SetStreamSelection(dwStreamIndex: MfInterop.FirstVideoStream, fSelected: true));

        // Ask for ARGB32 (DXGI B8G8R8A8) at the requested size — the processor scales; if the size is refused, accept
        // the device's own and let the consumer size its targets from the negotiated result.
        if (TrySetOutputType(reader: reader, width: m_requestedWidth, height: m_requestedHeight) < 0) {
            Check(hr: TrySetOutputType(reader: reader, width: 0, height: 0));
        }

        // Read back the negotiated frame size (the target/ring size the consumer must provision).
        Check(hr: reader.GetCurrentMediaType(dwStreamIndex: MfInterop.FirstVideoStream, ppMediaType: out var currentType));

        var frameSizeKey = MfInterop.MF_MT_FRAME_SIZE;

        Check(hr: currentType.GetUINT64(guidKey: ref frameSizeKey, punValue: out var packedSize));

        m_width = (int)(packedSize >> 32);
        m_height = (int)(packedSize & 0xffffffff);

        if ((m_width <= 0) || (m_height <= 0)) {
            throw new InvalidOperationException(message: $"the camera reported an invalid frame size ({m_width}x{m_height})");
        }

        return reader;
    }

    // Sets the ARGB32 output type, optionally with an explicit frame size (zero omits it). Returns the raw HRESULT so
    // the caller can fall back from an explicit size to the device's own.
    private static int TrySetOutputType(IMFSourceReader reader, int width, int height) {
        Check(hr: MfInterop.MFCreateMediaType(ppMFType: out var outputType));

        var majorTypeKey = MfInterop.MF_MT_MAJOR_TYPE;
        var video = MfInterop.MFMediaType_Video;

        Check(hr: outputType.SetGUID(guidKey: ref majorTypeKey, guidValue: ref video));

        var subTypeKey = MfInterop.MF_MT_SUBTYPE;
        var argb32 = MfInterop.MFVideoFormat_ARGB32;

        Check(hr: outputType.SetGUID(guidKey: ref subTypeKey, guidValue: ref argb32));

        if ((width > 0) && (height > 0)) {
            var frameSizeKey = MfInterop.MF_MT_FRAME_SIZE;

            Check(hr: outputType.SetUINT64(guidKey: ref frameSizeKey, unValue: ((ulong)(uint)width << 32) | (uint)height));
        }

        return reader.SetCurrentMediaType(dwStreamIndex: MfInterop.FirstVideoStream, pdwReserved: IntPtr.Zero, pMediaType: outputType);
    }

    private void ReadLoop(Win32D3D11VideoDevice device, IMFSourceReader reader, nint[] targets) {
        var next = 0;
        var texture2dIid = global::Windows.Win32.Graphics.Direct3D11.ID3D11Texture2D.IID_Guid;

        while (!m_stop) {
            var hr = reader.ReadSample(
                dwStreamIndex: MfInterop.FirstVideoStream,
                dwControlFlags: 0,
                pdwActualStreamIndex: out _,
                pdwStreamFlags: out var flags,
                pllTimestamp: out _,
                ppSample: out var sample
            );

            if (hr < 0) {
                Console.Error.WriteLine(value: $"[camera] '{m_name}' GPU read loop stopped (0x{hr:X8}); the device may have been disconnected.");

                break;
            }

            if ((flags & MfInterop.EndOfStream) != 0) {
                Console.Error.WriteLine(value: $"[camera] '{m_name}' reported end of stream; the live feed has stopped.");

                break;
            }

            if (sample is null) {
                continue;
            }

            try {
                if (sample.GetBufferByIndex(dwIndex: 0, ppBuffer: out var buffer) < 0) {
                    continue;
                }

                try {
                    // The DXGI view of the buffer: the GPU texture the processor wrote, plus which array slice.
                    if (buffer is not IMFDXGIBuffer dxgiBuffer) {
                        Console.Error.WriteLine(value: $"[camera] '{m_name}' produced a non-DXGI sample on the GPU tier; stopping (the D3D manager was not honored).");

                        break;
                    }

                    if ((dxgiBuffer.GetResource(riid: ref texture2dIid, ppvObject: out var frameTexture) < 0) || (0 == frameTexture)) {
                        continue;
                    }

                    try {
                        _ = dxgiBuffer.GetSubresourceIndex(puSubresource: out var subresource);

                        // Copy GPU->GPU into the next slot and drain the copy (CPU fence at camera cadence, on this
                        // thread); only a COMPLETE slot is ever published.
                        device.CopyToTarget(targetTexture: targets[next], sourceTexture: frameTexture, sourceSubresource: subresource);

                        m_latestSlot = next;
                        _ = Interlocked.Exchange(location1: ref m_lastTimestamp, value: System.Diagnostics.Stopwatch.GetTimestamp());
                        _ = Interlocked.Increment(location: ref m_version);
                        next = ((next + 1) % targets.Length);
                    } finally {
                        Win32D3D11VideoDevice.ReleaseTexture(texture: frameTexture);
                    }
                } finally {
                    _ = Marshal.ReleaseComObject(o: buffer);
                }
            } catch (Exception exception) {
                Console.Error.WriteLine(value: $"[camera] '{m_name}' GPU frame copy failed: {exception.Message}; stopping the feed.");

                break;
            } finally {
                _ = Marshal.ReleaseComObject(o: sample);
            }
        }
    }

    private static void Check(int hr) {
        if (hr < 0) {
            throw new COMException(message: "a Media Foundation call failed", errorCode: hr);
        }
    }
}
