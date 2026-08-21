using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Puck.Maths;
using Puck.Platform.Probes;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.Platform.Windows;

/// <summary>
/// One live KERNEL probe run: a private Direct3D 11 device on the request's adapter, the consumer's shared camera
/// targets opened read-only as shader resource views, and the kind's compiled accumulate/finalize compute shaders.
/// A dedicated thread paces itself to the probe's rate ceiling: each cycle it acquires the stream's latest slot,
/// skips it if unchanged, runs the two-pass reduction, reads the finalize pass's channels back through a staging
/// buffer, and publishes one <see cref="ProbeReading"/> into the ring — latest-wins, no queue. Every native call
/// after construction runs on this thread alone.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed unsafe class Win32D3D11ProbeKernelRunner : IProbeKernelRun {
    // The kernel ABI's accumulator slot count (puck.probe.v1: RWStructuredBuffer<uint> Accumulate : register(u0)).
    private const int AccumulateElementCount = 16;
    private const int StopTimeoutMilliseconds = 2000;

    private readonly ID3D11Buffer* m_accumulateBuffer;
    private readonly ID3D11ComputeShader* m_accumulateShader;
    private readonly ID3D11UnorderedAccessView* m_accumulateUav;
    private readonly int m_channelCount;
    private readonly ID3D11Buffer* m_channelsBuffer;
    private readonly ID3D11Buffer* m_channelsStaging;
    private readonly ID3D11UnorderedAccessView* m_channelsUav;
    private readonly ID3D11Buffer* m_constantBuffer;
    private readonly ID3D11DeviceContext* m_context;
    private readonly ID3D11Device* m_device;
    private readonly ID3D11Device1* m_device1;
    private readonly ID3D11ComputeShader* m_finalizeShader;
    private readonly int m_height;
    private readonly ID3D11Query* m_query;
    private readonly uint m_rateHz;
    private readonly ProbeReadingRing m_ring;
    private readonly ManualResetEvent m_stopSignal = new(initialState: false);
    private readonly ICameraSharedStream m_stream;
    private readonly ID3D11Texture2D*[] m_targetTextures;
    private readonly ID3D11ShaderResourceView*[] m_targetViews;
    private readonly Thread m_thread;
    private readonly Win32HighResolutionWaitableTimer? m_timer;
    private readonly int m_width;

    private long m_cycles;
    private bool m_disposed;
    private long m_drops;
    private int m_ended;
    private string? m_fault;
    private int m_releaseDeferred;
    private int m_released;
    private volatile bool m_stop;

    private Win32D3D11ProbeKernelRunner(in ProbeKernelRequest request, ICameraSharedStream stream, IReadOnlyList<nint> sharedTargetHandles, ProbeReadingRing ring) {
        m_channelCount = request.ChannelCount;
        m_height = request.Height;
        m_rateHz = request.RateHz;
        m_ring = ring;
        m_stream = stream;
        m_width = request.Width;

        var adapter = Win32D3D11.FindAdapterByLuid(adapterLuid: request.AdapterLuid);

        if (adapter is null) {
            throw new NotSupportedException(message: $"no DXGI adapter was found with LUID 0x{request.AdapterLuid:X16}");
        }

        ID3D11Device* device = null;
        ID3D11Device1* device1 = null;
        ID3D11DeviceContext* context = null;
        var targetTextures = new ID3D11Texture2D*[sharedTargetHandles.Count];
        var targetViews = new ID3D11ShaderResourceView*[sharedTargetHandles.Count];
        ID3D11Buffer* constantBuffer = null;
        ID3D11Buffer* accumulateBuffer = null;
        ID3D11UnorderedAccessView* accumulateUav = null;
        ID3D11Buffer* channelsBuffer = null;
        ID3D11UnorderedAccessView* channelsUav = null;
        ID3D11Buffer* channelsStaging = null;
        ID3D11ComputeShader* accumulateShader = null;
        ID3D11ComputeShader* finalizeShader = null;
        ID3D11Query* query = null;

        try {
            Win32D3D11.CreateMultithreadedDevice(
                adapter: ((IDXGIAdapter*)adapter),
                context: out context,
                device: out device,
                driverType: D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                flags: D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT
            );

            var device1Iid = ID3D11Device1.IID_Guid;

            Win32D3D11.ThrowIfFailed(hr: ((IUnknown*)device)->QueryInterface(ppvObject: out var device1Pointer, riid: in device1Iid), operation: "QueryInterface(ID3D11Device1)");
            device1 = ((ID3D11Device1*)device1Pointer);

            var requiredFormat = ToDxgiFormat(format: request.TargetFormat);

            for (var index = 0; (index < targetTextures.Length); index++) {
                using var handle = new SafeFileHandle(ownsHandle: false, preexistingHandle: sharedTargetHandles[index]);

                device1->OpenSharedResource1(hResource: handle, ppResource: out var opened, returnedInterface: ID3D11Texture2D.IID_Guid);
                targetTextures[index] = ((ID3D11Texture2D*)opened);

                var description = default(D3D11_TEXTURE2D_DESC);

                targetTextures[index]->GetDesc(pDesc: &description);

                if (
                    (description.Width != m_width) ||
                    (description.Height != m_height) ||
                    (description.Format != requiredFormat)
                ) {
                    throw new NotSupportedException(message: $"the shared sense target is {description.Width}x{description.Height} {description.Format}; expected {m_width}x{m_height} {requiredFormat}");
                }

                ID3D11ShaderResourceView* view = null;

                device->CreateShaderResourceView(pResource: ((ID3D11Resource*)targetTextures[index]), pDesc: null, ppSRView: &view);

                if (view is null) {
                    throw new InvalidOperationException(message: $"D3D11 sense target {index} view creation returned no view");
                }

                targetViews[index] = view;
            }

            if (!request.Constants.IsEmpty) {
                var constants = request.Constants.Span;
                var constantsDescription = new D3D11_BUFFER_DESC {
                    BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                    ByteWidth = checked((uint)constants.Length),
                    Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                };

                fixed (byte* constantsData = constants) {
                    var initialData = new D3D11_SUBRESOURCE_DATA { pSysMem = constantsData };

                    device->CreateBuffer(pDesc: &constantsDescription, pInitialData: &initialData, ppBuffer: &constantBuffer);
                }
            }

            var accumulateDescription = new D3D11_BUFFER_DESC {
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
                ByteWidth = checked((uint)AccumulateElementCount * (uint)sizeof(uint)),
                MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
                StructureByteStride = ((uint)sizeof(uint)),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            };

            device->CreateBuffer(pDesc: &accumulateDescription, pInitialData: null, ppBuffer: &accumulateBuffer);

            var accumulateUavDescription = new D3D11_UNORDERED_ACCESS_VIEW_DESC {
                Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
                ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER,
            };

            accumulateUavDescription.Anonymous.Buffer.NumElements = AccumulateElementCount;

            device->CreateUnorderedAccessView(pResource: ((ID3D11Resource*)accumulateBuffer), pDesc: &accumulateUavDescription, ppUAView: &accumulateUav);

            var channelsElementCount = checked((uint)(m_channelCount + 1));
            var channelsDescription = new D3D11_BUFFER_DESC {
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
                ByteWidth = checked(channelsElementCount * (uint)sizeof(float)),
                MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
                StructureByteStride = ((uint)sizeof(float)),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            };

            device->CreateBuffer(pDesc: &channelsDescription, pInitialData: null, ppBuffer: &channelsBuffer);

            var channelsUavDescription = new D3D11_UNORDERED_ACCESS_VIEW_DESC {
                Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
                ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER,
            };

            channelsUavDescription.Anonymous.Buffer.NumElements = channelsElementCount;

            device->CreateUnorderedAccessView(pResource: ((ID3D11Resource*)channelsBuffer), pDesc: &channelsUavDescription, ppUAView: &channelsUav);

            var stagingDescription = new D3D11_BUFFER_DESC {
                ByteWidth = channelsDescription.ByteWidth,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            };

            device->CreateBuffer(pDesc: &stagingDescription, pInitialData: null, ppBuffer: &channelsStaging);

            accumulateShader = CompileShader(device: device, entry: request.AccumulateEntry, source: request.KernelSource);
            finalizeShader = CompileShader(device: device, entry: request.FinalizeEntry, source: request.KernelSource);

            var queryDescription = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };

            device->CreateQuery(pQueryDesc: &queryDescription, ppQuery: &query);
        } catch {
            Release(value: query);
            Release(value: finalizeShader);
            Release(value: accumulateShader);
            Release(value: channelsStaging);
            Release(value: channelsUav);
            Release(value: channelsBuffer);
            Release(value: accumulateUav);
            Release(value: accumulateBuffer);
            Release(value: constantBuffer);
            Release(values: targetViews);
            Release(values: targetTextures);
            Release(value: device1);
            Release(value: context);
            Release(value: device);
            throw;
        } finally {
            _ = ((IUnknown*)adapter)->Release();
        }

        m_accumulateBuffer = accumulateBuffer;
        m_accumulateShader = accumulateShader;
        m_accumulateUav = accumulateUav;
        m_channelsBuffer = channelsBuffer;
        m_channelsStaging = channelsStaging;
        m_channelsUav = channelsUav;
        m_constantBuffer = constantBuffer;
        m_context = context;
        m_device = device;
        m_device1 = device1;
        m_finalizeShader = finalizeShader;
        m_query = query;
        m_targetTextures = targetTextures;
        m_targetViews = targetViews;

        m_timer = Win32HighResolutionWaitableTimer.TryCreate();
        m_thread = new Thread(start: Worker) {
            IsBackground = true,
            Name = "puck-sense-kernel",
        };

        m_thread.SetApartmentState(state: ApartmentState.MTA);
        m_thread.Start();
    }

    /// <inheritdoc/>
    public long Cycles => Interlocked.Read(location: ref m_cycles);
    /// <inheritdoc/>
    public long Drops => Interlocked.Read(location: ref m_drops);
    /// <inheritdoc/>
    public string? Fault => Volatile.Read(location: ref m_fault);
    /// <inheritdoc/>
    public bool IsEnded => (Volatile.Read(location: ref m_ended) != 0);

    /// <summary>Validates a kernel request and starts a run on its own device and thread.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/>, <paramref name="sharedTargetHandles"/>, or
    /// <paramref name="ring"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request or the shared targets are shaped incorrectly.</exception>
    /// <exception cref="COMException">Device creation, shared-handle opening, or shader compilation failed.</exception>
    /// <exception cref="NotSupportedException">No adapter matches the request's LUID, an opened target's extent or
    /// format disagrees with the request, or the request's target format has no D3D11 equivalent.</exception>
    public static Win32D3D11ProbeKernelRunner Start(in ProbeKernelRequest request, ICameraSharedStream stream, IReadOnlyList<nint> sharedTargetHandles, ProbeReadingRing ring) {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sharedTargetHandles);
        ArgumentNullException.ThrowIfNull(ring);

        if (sharedTargetHandles.Count == 0) {
            throw new ArgumentException(message: "a sense kernel run needs at least one shared camera target.", paramName: nameof(sharedTargetHandles));
        }
        if (string.IsNullOrEmpty(value: request.KernelSource) || string.IsNullOrEmpty(value: request.AccumulateEntry) || string.IsNullOrEmpty(value: request.FinalizeEntry)) {
            throw new ArgumentException(message: "a sense kernel request needs kernel source and both entry points.", paramName: nameof(request));
        }
        if ((request.ChannelCount <= 0) || (request.ChannelCount > ProbeReadingLimits.MaxChannels)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(request), message: $"a sense kernel's channel count must be between 1 and {ProbeReadingLimits.MaxChannels}.");
        }
        if (request.RateHz == 0) {
            throw new ArgumentOutOfRangeException(paramName: nameof(request), message: "a sense kernel's rate ceiling must be positive.");
        }
        if ((request.Width <= 0) || (request.Height <= 0)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(request), message: "a sense kernel's source extent must be positive.");
        }

        return new Win32D3D11ProbeKernelRunner(request: request, ring: ring, sharedTargetHandles: sharedTargetHandles, stream: stream);
    }
    /// <summary>Compiles one entry point of a kernel source with no device — the hardware-free proof a shipped
    /// kernel is well-formed.</summary>
    /// <exception cref="COMException">Compilation failed; the message carries the compiler's own diagnostic.</exception>
    internal static void Compile(string source, string entry) {
        Release(value: CompileShaderBytecode(entry: entry, source: source));
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_stop = true;
        _ = m_stopSignal.Set();

        if (m_thread.Join(millisecondsTimeout: StopTimeoutMilliseconds)) {
            ReleaseNativeResources();

            return;
        }

        // The worker is still inside a cycle (typically a stalled GPU completion wait). The native objects stay
        // alive under it and are released by whichever side finishes last: the flag exchange is a full fence, so
        // either this thread observes the worker's exit below or the worker observes the deferral in its own
        // epilogue — never neither.
        _ = Interlocked.Exchange(location1: ref m_releaseDeferred, value: 1);

        if (IsEnded) {
            ReleaseNativeResources();
        }
    }

    // Runs one accumulate/finalize dispatch against the acquired slot's target and reads the finalize pass's
    // channels back through the staging buffer.
    private void DispatchCycle(int slot, out ProbeChannelValues channels, out FixedQ4816 confidence) {
        Span<uint> zero = [0u, 0u, 0u, 0u];

        m_context->ClearUnorderedAccessViewUint(pUnorderedAccessView: m_accumulateUav, Values: zero);

        var srv = m_targetViews[slot];

        m_context->CSSetShaderResources(StartSlot: 0, NumViews: 1, ppShaderResourceViews: &srv);

        var constantBuffer = m_constantBuffer;

        if (constantBuffer is not null) {
            m_context->CSSetConstantBuffers(StartSlot: 0, NumBuffers: 1, ppConstantBuffers: &constantBuffer);
        }

        var accumulateUav = m_accumulateUav;
        var channelsUav = m_channelsUav;
        var uavs = stackalloc ID3D11UnorderedAccessView*[2] { accumulateUav, channelsUav };

        m_context->CSSetUnorderedAccessViews(StartSlot: 0, NumUAVs: 2, ppUnorderedAccessViews: uavs, pUAVInitialCounts: null);
        m_context->CSSetShader(pComputeShader: m_accumulateShader, NumClassInstances: 0, ppClassInstances: null);
        m_context->Dispatch(
            ThreadGroupCountX: checked((uint)((m_width + 7) / 8)),
            ThreadGroupCountY: checked((uint)((m_height + 7) / 8)),
            ThreadGroupCountZ: 1
        );
        m_context->CSSetShader(pComputeShader: m_finalizeShader, NumClassInstances: 0, ppClassInstances: null);
        m_context->Dispatch(ThreadGroupCountX: 1, ThreadGroupCountY: 1, ThreadGroupCountZ: 1);

        ID3D11ShaderResourceView* noView = null;
        var noUavs = stackalloc ID3D11UnorderedAccessView*[2] { null, null };
        ID3D11Buffer* noConstantBuffer = null;

        m_context->CSSetShaderResources(StartSlot: 0, NumViews: 1, ppShaderResourceViews: &noView);
        m_context->CSSetUnorderedAccessViews(StartSlot: 0, NumUAVs: 2, ppUnorderedAccessViews: noUavs, pUAVInitialCounts: null);
        m_context->CSSetConstantBuffers(StartSlot: 0, NumBuffers: 1, ppConstantBuffers: &noConstantBuffer);
        m_context->CSSetShader(pComputeShader: null, NumClassInstances: 0, ppClassInstances: null);
        m_context->CopyResource(pDstResource: ((ID3D11Resource*)m_channelsStaging), pSrcResource: ((ID3D11Resource*)m_channelsBuffer));

        Win32D3D11.WaitForCompletion(context: m_context, query: m_query);

        var mapped = default(D3D11_MAPPED_SUBRESOURCE);

        m_context->Map(pResource: ((ID3D11Resource*)m_channelsStaging), Subresource: 0, MapType: D3D11_MAP.D3D11_MAP_READ, MapFlags: 0, pMappedResource: &mapped);

        try {
            var floats = new ReadOnlySpan<float>(mapped.pData, (m_channelCount + 1));
            var values = default(ProbeChannelValues);

            for (var channel = 0; (channel < m_channelCount); channel++) {
                values[channel] = FixedQ4816.FromDouble(value: floats[channel]);
            }

            channels = values;
            confidence = FixedQ4816.FromDouble(value: floats[m_channelCount]);
        } finally {
            m_context->Unmap(pResource: ((ID3D11Resource*)m_channelsStaging), Subresource: 0);
        }
    }
    private void RunLoop() {
        var period = TimeSpan.FromSeconds(1.0 / m_rateHz);
        var starvationTicks = checked(2L * ((long)Math.Round(Stopwatch.Frequency / (double)m_rateHz)));
        var sequence = 0L;
        var lastFreshTimestamp = Stopwatch.GetTimestamp();
        var lastProcessedVersion = -1L;

        while (!m_stop) {
            if (m_timer is { } timer) {
                _ = timer.WaitOne(cancellationWaitHandle: m_stopSignal, dueTime: period);
            } else {
                _ = m_stopSignal.WaitOne(timeout: period);
            }

            if (m_stop) {
                break;
            }

            if (m_stream.FrameVersion == lastProcessedVersion) {
                if ((Stopwatch.GetTimestamp() - lastFreshTimestamp) > starvationTicks) {
                    _ = Interlocked.Increment(location: ref m_drops);
                }

                continue;
            }

            if (!TryAcquireFresh(slot: out var slot, timestamp: out var timestamp, version: out var version)) {
                continue;
            }

            try {
                DispatchCycle(slot: slot, channels: out var channels, confidence: out var confidence);

                lastFreshTimestamp = Stopwatch.GetTimestamp();
                lastProcessedVersion = version;
                _ = Interlocked.Increment(location: ref m_cycles);
                m_ring.Publish(reading: new ProbeReading(
                    sequence: sequence++,
                    captureTimestamp: timestamp,
                    completionTimestamp: Stopwatch.GetTimestamp(),
                    confidence: confidence,
                    channelCount: m_channelCount,
                    channels: channels
                ));
            } finally {
                m_stream.Release(slot: slot);
            }
        }
    }
    // Acquires the stream's latest slot together with the version and capture timestamp of the frame it holds. The
    // stream publishes the two observational fields and the slot separately, so a frame landing between the reads
    // and the acquisition would pair the newer texture with the older stamps; re-reading the version after the
    // acquisition detects that and retries. After the retry budget the older stamps stand — conservative, since
    // the frame is at worst analyzed once more on the next cycle, never skipped.
    private bool TryAcquireFresh(out int slot, out long timestamp, out long version) {
        const int AttemptBudget = 3;

        for (var attempt = 1; ; attempt++) {
            timestamp = m_stream.LastFrameTimestamp;
            version = m_stream.FrameVersion;

            if (!m_stream.TryAcquireLatest(out slot)) {
                return false;
            }
            if ((m_stream.FrameVersion == version) || (attempt >= AttemptBudget)) {
                return true;
            }

            m_stream.Release(slot: slot);
        }
    }
    private void Worker() {
        try {
            RunLoop();
        } catch (Exception exception) {
            if (!m_stop) {
                Volatile.Write(location: ref m_fault, value: exception.Message);
            }
        } finally {
            _ = Interlocked.Exchange(location1: ref m_ended, value: 1);

            if (Volatile.Read(location: ref m_releaseDeferred) != 0) {
                ReleaseNativeResources();
            }
        }
    }
    // Releases every native object exactly once, from whichever thread finishes with them last (see Dispose).
    private void ReleaseNativeResources() {
        if (Interlocked.Exchange(location1: ref m_released, value: 1) != 0) {
            return;
        }

        Release(value: m_query);
        Release(value: m_finalizeShader);
        Release(value: m_accumulateShader);
        Release(value: m_channelsStaging);
        Release(value: m_channelsUav);
        Release(value: m_channelsBuffer);
        Release(value: m_accumulateUav);
        Release(value: m_accumulateBuffer);
        Release(value: m_constantBuffer);
        Release(values: m_targetViews);
        Release(values: m_targetTextures);
        Release(value: m_device1);
        Release(value: m_context);
        Release(value: m_device);
        m_stopSignal.Dispose();
        m_timer?.Dispose();
    }
    private static ID3D11ComputeShader* CompileShader(ID3D11Device* device, string entry, string source) {
        var code = CompileShaderBytecode(entry: entry, source: source);

        try {
            ID3D11ComputeShader* shader = null;

            device->CreateComputeShader(pShaderBytecode: code->GetBufferPointer(), BytecodeLength: code->GetBufferSize(), pClassLinkage: null, ppComputeShader: &shader);

            if (shader is null) {
                throw new InvalidOperationException(message: $"D3D11 sense kernel '{entry}' creation returned no shader");
            }

            return shader;
        } finally {
            Release(value: code);
        }
    }
    private static ID3DBlob* CompileShaderBytecode(string entry, string source) {
        var bytes = Encoding.UTF8.GetBytes(s: source);
        ID3DBlob* code = null;
        ID3DBlob* errors = null;

        try {
            fixed (byte* sourceBytes = bytes) {
                var result = PInvoke.D3DCompile(
                    pSrcData: sourceBytes,
                    SrcDataSize: ((nuint)bytes.Length),
                    pSourceName: "puck-probe.hlsl",
                    pDefines: null,
                    pInclude: null,
                    pEntrypoint: entry,
                    pTarget: "cs_5_0",
                    Flags1: 0,
                    Flags2: 0,
                    ppCode: &code,
                    ppErrorMsgs: &errors
                );

                if (result.Value < 0) {
                    var message = ((errors is null)
                        ? "unknown shader compiler error"
                        : Marshal.PtrToStringUTF8(((nint)errors->GetBufferPointer()), checked((int)errors->GetBufferSize()))
                    );

                    throw new COMException(message: $"sense kernel '{entry}' compilation failed: {message}", errorCode: result.Value);
                }
            }

            if (code is null) {
                throw new InvalidOperationException(message: "sense kernel compilation returned no bytecode");
            }

            return code;
        } catch {
            Release(value: code);
            throw;
        } finally {
            Release(value: errors);
        }
    }
    private static DXGI_FORMAT ToDxgiFormat(SurfaceFormat format) => (format switch {
        SurfaceFormat.R8G8B8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        SurfaceFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        _ => throw new NotSupportedException(message: $"no D3D11 format for sense target format '{format}'"),
    });
    private static void Release<T>(T* value) where T : unmanaged {
        if (value is not null) {
            _ = ((IUnknown*)value)->Release();
        }
    }
    private static void Release<T>(T*[] values) where T : unmanaged {
        foreach (var value in values) {
            Release(value: value);
        }
    }
}
