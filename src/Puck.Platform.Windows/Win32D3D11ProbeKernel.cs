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
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.Platform.Windows;

/// <summary>One compiled kernel-class probe on a camera graph's own Direct3D 11 device, addressed by raw
/// <c>ID3D11Device*</c>/<c>ID3D11DeviceContext*</c>/<c>ID3D11Device1*</c> as <see cref="nint"/>. The graph's worker calls
/// <see cref="TryRun"/> after converting a trigger frame, holding the device's critical section; the kernel binds the
/// converted frames it was asked for, dispatches accumulate and finalize, writes its output slot, reads the channels
/// back, and publishes the reading before returning.
/// <para>Kernel ABI (<c>puck.probe.v1</c>): sockets bind <c>t</c> registers in declaration order — a
/// <see cref="ProbeKernelInput.StrobePair"/> socket spans two consecutive registers (lit, then unlit); every other
/// socket spans one; <c>cbuffer ProbeConfig : register(b0)</c> is the kind's packed config; <c>cbuffer ProbeFrame :
/// register(b1)</c> is <c>{ float time; float deltaTime; uint frame; uint boundMask; }</c> — bit <c>i</c> set when
/// socket <c>i</c> is bound; <c>RWStructuredBuffer&lt;uint&gt; Accumulate : register(u0)</c> is cleared per cycle;
/// <c>RWStructuredBuffer&lt;float&gt; Channels : register(u1)</c> carries the channels then confidence; a declared
/// output is <c>RWTexture2D&lt;float4&gt; Output : register(u2)</c>.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class Win32D3D11ProbeKernel : IDisposable {
    private const int AccumulateElementCount = 16;
    private const int FrameConstantsBytes = 16;

    private readonly ID3D11Buffer* m_accumulateBuffer;
    private readonly ID3D11ComputeShader* m_accumulateShader;
    private readonly ID3D11UnorderedAccessView* m_accumulateUav;
    private readonly int m_channelCount;
    private readonly ID3D11Buffer* m_channelsBuffer;
    private readonly ID3D11Buffer* m_channelsStaging;
    private readonly ID3D11UnorderedAccessView* m_channelsUav;
    private readonly ID3D11Buffer* m_constantBuffer;
    private readonly int m_constantsLength;
    private readonly ID3D11DeviceContext* m_context;
    private readonly int m_dispatchHeight;
    private readonly int m_dispatchWidth;
    private readonly ID3D11ComputeShader* m_finalizeShader;
    private readonly ID3D11Buffer* m_frameBuffer;
    private readonly ID3D11Texture2D* m_output;
    private readonly ID3D11Texture2D*[] m_outputTargets;
    private readonly ID3D11UnorderedAccessView* m_outputUav;
    private readonly long m_periodTicks;
    private readonly ID3D11Query* m_query;
    private readonly int m_registerCount;
    private readonly ProbeReadingRing m_ring;
    private readonly LatestSlotPublication? m_slots;
    private readonly long m_startTimestamp = Stopwatch.GetTimestamp();

    private long m_cycles;
    private bool m_disposed;
    private long m_drops;
    private long m_lastRunTimestamp;
    private byte[]? m_pendingConstants;
    private long m_sequence;

    public Win32D3D11ProbeKernel(nint device, nint context, nint device1, in ProbeKernelRequest request, ProbeReadingRing ring, int triggerWidth, int triggerHeight)
        : this(device: ((ID3D11Device*)device), context: ((ID3D11DeviceContext*)context), device1: ((ID3D11Device1*)device1), request: in request, ring: ring, triggerWidth: triggerWidth, triggerHeight: triggerHeight) {
    }

    private Win32D3D11ProbeKernel(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Device1* device1, in ProbeKernelRequest request, ProbeReadingRing ring, int triggerWidth, int triggerHeight) {
        ArgumentNullException.ThrowIfNull(ring);

        if (string.IsNullOrEmpty(value: request.KernelSource) || string.IsNullOrEmpty(value: request.AccumulateEntry) || string.IsNullOrEmpty(value: request.FinalizeEntry)) {
            throw new ArgumentException(message: "a probe kernel request needs kernel source and both entry points.", paramName: nameof(request));
        }
        if ((request.ChannelCount <= 0) || (request.ChannelCount > ProbeReadingLimits.MaxChannels)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(request), message: $"a probe kernel's channel count must be between 1 and {ProbeReadingLimits.MaxChannels}.");
        }
        if (request.RateHz == 0) {
            throw new ArgumentOutOfRangeException(paramName: nameof(request), message: "a probe kernel's rate ceiling must be positive.");
        }
        if (request.Inputs.Count == 0) {
            throw new ArgumentException(message: "a probe kernel reads at least one input.", paramName: nameof(request));
        }

        m_channelCount = request.ChannelCount;
        m_context = context;
        m_periodTicks = Math.Max(val1: 1L, val2: (Stopwatch.Frequency / request.RateHz));
        m_registerCount = ProbeKernelInput.RegisterCount(inputs: request.Inputs);
        m_ring = ring;
        m_lastRunTimestamp = (m_startTimestamp - m_periodTicks);

        ID3D11Buffer* constantBuffer = null;
        ID3D11Buffer* frameBuffer = null;
        ID3D11Buffer* accumulateBuffer = null;
        ID3D11UnorderedAccessView* accumulateUav = null;
        ID3D11Buffer* channelsBuffer = null;
        ID3D11UnorderedAccessView* channelsUav = null;
        ID3D11Buffer* channelsStaging = null;
        ID3D11ComputeShader* accumulateShader = null;
        ID3D11ComputeShader* finalizeShader = null;
        ID3D11Query* query = null;
        ID3D11Texture2D* output = null;
        ID3D11UnorderedAccessView* outputUav = null;
        var outputTargets = new ID3D11Texture2D*[0];

        try {
            var constants = request.Constants.Span;

            m_constantsLength = constants.Length;

            if (!constants.IsEmpty) {
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

            var frameDescription = new D3D11_BUFFER_DESC {
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                ByteWidth = FrameConstantsBytes,
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            };

            device->CreateBuffer(pDesc: &frameDescription, pInitialData: null, ppBuffer: &frameBuffer);

            var accumulateDescription = new D3D11_BUFFER_DESC {
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
                ByteWidth = checked((((uint)AccumulateElementCount) * ((uint)sizeof(uint)))),
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
            device->CreateUnorderedAccessView(pDesc: &accumulateUavDescription, pResource: ((ID3D11Resource*)accumulateBuffer), ppUAView: &accumulateUav);

            var channelsElementCount = checked((uint)(m_channelCount + 1));
            var channelsDescription = new D3D11_BUFFER_DESC {
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
                ByteWidth = checked((channelsElementCount * ((uint)sizeof(float)))),
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
            device->CreateUnorderedAccessView(pDesc: &channelsUavDescription, pResource: ((ID3D11Resource*)channelsBuffer), ppUAView: &channelsUav);

            var stagingDescription = new D3D11_BUFFER_DESC {
                ByteWidth = channelsDescription.ByteWidth,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            };

            device->CreateBuffer(pDesc: &stagingDescription, pInitialData: null, ppBuffer: &channelsStaging);

            if (request.Output is { } declaredOutput) {
                ArgumentNullException.ThrowIfNull(declaredOutput.Slots);

                if (declaredOutput.SharedTargetHandles.Count < 2) {
                    throw new ArgumentException(message: "a probe kernel output ring needs at least two shared targets.", paramName: nameof(request));
                }

                var format = ToDxgiFormat(format: declaredOutput.TargetFormat);
                var outputDescription = new D3D11_TEXTURE2D_DESC {
                    Width = checked((uint)declaredOutput.Width),
                    Height = checked((uint)declaredOutput.Height),
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = format,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
                };

                device->CreateTexture2D(pDesc: &outputDescription, pInitialData: null, ppTexture2D: &output);
                device->CreateUnorderedAccessView(pDesc: null, pResource: ((ID3D11Resource*)output), ppUAView: &outputUav);

                outputTargets = new ID3D11Texture2D*[declaredOutput.SharedTargetHandles.Count];

                for (var index = 0; (index < outputTargets.Length); index++) {
                    using var handle = new SafeFileHandle(ownsHandle: false, preexistingHandle: declaredOutput.SharedTargetHandles[index]);

                    device1->OpenSharedResource1(hResource: handle, ppResource: out var opened, returnedInterface: ID3D11Texture2D.IID_Guid);
                    outputTargets[index] = ((ID3D11Texture2D*)opened);

                    var description = default(D3D11_TEXTURE2D_DESC);

                    outputTargets[index]->GetDesc(pDesc: &description);

                    if ((description.Width != declaredOutput.Width) || (description.Height != declaredOutput.Height) || (description.Format != format)) {
                        throw new NotSupportedException(message: $"the shared probe output target is {description.Width}x{description.Height} {description.Format}; expected {declaredOutput.Width}x{declaredOutput.Height} {format}");
                    }
                }

                m_dispatchWidth = declaredOutput.Width;
                m_dispatchHeight = declaredOutput.Height;
                m_slots = declaredOutput.Slots;
            } else {
                m_dispatchWidth = triggerWidth;
                m_dispatchHeight = triggerHeight;
            }

            accumulateShader = CompileShader(device: device, entry: request.AccumulateEntry, source: request.KernelSource);
            finalizeShader = CompileShader(device: device, entry: request.FinalizeEntry, source: request.KernelSource);

            var queryDescription = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };

            device->CreateQuery(pQueryDesc: &queryDescription, ppQuery: &query);
        } catch {
            Release(value: query);
            Release(value: finalizeShader);
            Release(value: accumulateShader);
            Release(values: outputTargets);
            Release(value: outputUav);
            Release(value: output);
            Release(value: channelsStaging);
            Release(value: channelsUav);
            Release(value: channelsBuffer);
            Release(value: accumulateUav);
            Release(value: accumulateBuffer);
            Release(value: frameBuffer);
            Release(value: constantBuffer);
            throw;
        }

        m_accumulateBuffer = accumulateBuffer;
        m_accumulateShader = accumulateShader;
        m_accumulateUav = accumulateUav;
        m_channelsBuffer = channelsBuffer;
        m_channelsStaging = channelsStaging;
        m_channelsUav = channelsUav;
        m_constantBuffer = constantBuffer;
        m_finalizeShader = finalizeShader;
        m_frameBuffer = frameBuffer;
        m_output = output;
        m_outputTargets = outputTargets;
        m_outputUav = outputUav;
        m_query = query;
    }

    public long Cycles => Interlocked.Read(location: ref m_cycles);
    public long Drops => Interlocked.Read(location: ref m_drops);

    /// <summary>Compiles a kernel entry point without creating a shader — the manifest validation check.</summary>
    public static void Compile(string source, string entry) {
        Release(value: CompileShaderBytecode(entry: entry, source: source));
    }
    public void SetConstants(ReadOnlyMemory<byte> constants) {
        if (constants.Length != m_constantsLength) {
            throw new ArgumentException(message: $"the constants block is {constants.Length} bytes; the kernel was created with {m_constantsLength}.", paramName: nameof(constants));
        }

        _ = Interlocked.Exchange(location1: ref m_pendingConstants, value: constants.ToArray());
    }
    /// <summary>Runs one cycle against the bound registers (<c>ID3D11ShaderResourceView*</c> as <see cref="nint"/>,
    /// flattened in socket order — a zero entry binds a null SRV, which is legal). The caller holds the device's
    /// critical section. A cycle arriving inside the rate period is skipped; a cycle with no writable output slot is
    /// dropped.</summary>
    /// <param name="views">The flattened register list; a <see cref="ProbeKernelInput.StrobePair"/> socket
    /// contributes two entries, every other socket one.</param>
    /// <param name="boundMask">Bit <c>i</c> set when socket <c>i</c> is bound, written into the frame constants.</param>
    /// <param name="captureTimestamp">The trigger frame's capture timestamp, published on the reading.</param>
    /// <returns><see langword="true"/> when a reading was published.</returns>
    public bool TryRun(ReadOnlySpan<nint> views, uint boundMask, long captureTimestamp) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        if (views.Length != m_registerCount) {
            throw new ArgumentException(message: $"the kernel binds {m_registerCount} registers; {views.Length} were given.", paramName: nameof(views));
        }

        var now = Stopwatch.GetTimestamp();

        if ((now - m_lastRunTimestamp) < m_periodTicks) {
            return false;
        }

        var slot = -1;

        if ((m_slots is { } slots) && !slots.TryReserveWriteSlot(slot: out slot)) {
            _ = Interlocked.Increment(location: ref m_drops);

            return false;
        }

        var deltaSeconds = ((float)((now - m_lastRunTimestamp) / ((double)Stopwatch.Frequency)));

        m_lastRunTimestamp = now;
        UpdateConstants(boundMask: boundMask, deltaSeconds: deltaSeconds, now: now);
        Dispatch(channels: out var channels, confidence: out var confidence, inputViews: views, slot: slot);

        if (m_slots is { } publication) {
            publication.Publish(slot: slot);
        }

        _ = Interlocked.Increment(location: ref m_cycles);
        m_ring.Publish(reading: new ProbeReading(
            sequence: m_sequence++,
            captureTimestamp: captureTimestamp,
            completionTimestamp: Stopwatch.GetTimestamp(),
            confidence: confidence,
            channelCount: m_channelCount,
            channels: channels,
            outputSlot: slot
        ));

        return true;
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        Release(value: m_query);
        Release(value: m_finalizeShader);
        Release(value: m_accumulateShader);
        Release(values: m_outputTargets);
        Release(value: m_outputUav);
        Release(value: m_output);
        Release(value: m_channelsStaging);
        Release(value: m_channelsUav);
        Release(value: m_channelsBuffer);
        Release(value: m_accumulateUav);
        Release(value: m_accumulateBuffer);
        Release(value: m_frameBuffer);
        Release(value: m_constantBuffer);
    }

    private void Dispatch(ReadOnlySpan<nint> inputViews, int slot, out ProbeChannelValues channels, out FixedQ4816 confidence) {
        Span<uint> zero = [0u, 0u, 0u, 0u];

        m_context->ClearUnorderedAccessViewUint(Values: zero, pUnorderedAccessView: m_accumulateUav);

        var views = stackalloc ID3D11ShaderResourceView*[inputViews.Length];

        for (var index = 0; (index < inputViews.Length); index++) {
            views[index] = ((ID3D11ShaderResourceView*)inputViews[index]);
        }

        var viewCount = ((uint)inputViews.Length);
        var constantBuffers = stackalloc ID3D11Buffer*[2] { m_constantBuffer, m_frameBuffer };
        var uavs = stackalloc ID3D11UnorderedAccessView*[3] { m_accumulateUav, m_channelsUav, m_outputUav };
        var uavCount = ((m_outputUav is null) ? 2u : 3u);

        m_context->CSSetShaderResources(NumViews: viewCount, StartSlot: 0, ppShaderResourceViews: views);
        m_context->CSSetConstantBuffers(NumBuffers: 2, StartSlot: 0, ppConstantBuffers: constantBuffers);
        m_context->CSSetUnorderedAccessViews(NumUAVs: uavCount, StartSlot: 0, pUAVInitialCounts: null, ppUnorderedAccessViews: uavs);
        m_context->CSSetShader(NumClassInstances: 0, pComputeShader: m_accumulateShader, ppClassInstances: null);
        m_context->Dispatch(
            ThreadGroupCountX: checked((uint)((m_dispatchWidth + 7) / 8)),
            ThreadGroupCountY: checked((uint)((m_dispatchHeight + 7) / 8)),
            ThreadGroupCountZ: 1
        );
        m_context->CSSetShader(NumClassInstances: 0, pComputeShader: m_finalizeShader, ppClassInstances: null);
        m_context->Dispatch(ThreadGroupCountX: 1, ThreadGroupCountY: 1, ThreadGroupCountZ: 1);

        var noViews = stackalloc ID3D11ShaderResourceView*[inputViews.Length];
        var noUavs = stackalloc ID3D11UnorderedAccessView*[3] { null, null, null };
        var noConstantBuffers = stackalloc ID3D11Buffer*[2] { null, null };

        for (var index = 0; (index < inputViews.Length); index++) {
            noViews[index] = null;
        }

        m_context->CSSetShaderResources(NumViews: viewCount, StartSlot: 0, ppShaderResourceViews: noViews);
        m_context->CSSetUnorderedAccessViews(NumUAVs: uavCount, StartSlot: 0, pUAVInitialCounts: null, ppUnorderedAccessViews: noUavs);
        m_context->CSSetConstantBuffers(NumBuffers: 2, StartSlot: 0, ppConstantBuffers: noConstantBuffers);
        m_context->CSSetShader(NumClassInstances: 0, pComputeShader: null, ppClassInstances: null);

        if (slot >= 0) {
            m_context->CopySubresourceRegion(
                DstSubresource: 0,
                DstX: 0,
                DstY: 0,
                DstZ: 0,
                SrcSubresource: 0,
                pDstResource: ((ID3D11Resource*)m_outputTargets[slot]),
                pSrcBox: null,
                pSrcResource: ((ID3D11Resource*)m_output)
            );
        }

        m_context->CopyResource(pDstResource: ((ID3D11Resource*)m_channelsStaging), pSrcResource: ((ID3D11Resource*)m_channelsBuffer));
        Win32D3D11.WaitForCompletion(context: m_context, query: m_query);

        var mapped = default(D3D11_MAPPED_SUBRESOURCE);

        m_context->Map(MapFlags: 0, MapType: D3D11_MAP.D3D11_MAP_READ, Subresource: 0, pMappedResource: &mapped, pResource: ((ID3D11Resource*)m_channelsStaging));

        try {
            var floats = new ReadOnlySpan<float>(length: (m_channelCount + 1), pointer: mapped.pData);
            var values = default(ProbeChannelValues);

            for (var channel = 0; (channel < m_channelCount); channel++) {
                values[channel] = FixedQ4816.FromDouble(value: floats[channel]);
            }

            channels = values;
            confidence = FixedQ4816.FromDouble(value: floats[m_channelCount]);
        } finally {
            m_context->Unmap(Subresource: 0, pResource: ((ID3D11Resource*)m_channelsStaging));
        }
    }
    private void UpdateConstants(long now, float deltaSeconds, uint boundMask) {
        var time = ((float)((now - m_startTimestamp) / ((double)Stopwatch.Frequency)));
        var frame = stackalloc float[4];

        frame[0] = time;
        frame[1] = deltaSeconds;
        ((uint*)frame)[2] = unchecked((uint)m_cycles);
        ((uint*)frame)[3] = boundMask;
        m_context->UpdateSubresource(DstSubresource: 0, SrcDepthPitch: 0, SrcRowPitch: 0, pDstBox: null, pDstResource: ((ID3D11Resource*)m_frameBuffer), pSrcData: frame);

        if ((m_constantBuffer is null) || (Interlocked.Exchange(location1: ref m_pendingConstants, value: null) is not { } pending)) {
            return;
        }

        fixed (byte* data = pending) {
            m_context->UpdateSubresource(DstSubresource: 0, SrcDepthPitch: 0, SrcRowPitch: 0, pDstBox: null, pDstResource: ((ID3D11Resource*)m_constantBuffer), pSrcData: data);
        }
    }
    private static ID3D11ComputeShader* CompileShader(ID3D11Device* device, string entry, string source) {
        var code = CompileShaderBytecode(entry: entry, source: source);

        try {
            ID3D11ComputeShader* shader = null;

            device->CreateComputeShader(pShaderBytecode: code->GetBufferPointer(), BytecodeLength: code->GetBufferSize(), pClassLinkage: null, ppComputeShader: &shader);

            return ((shader is null)
                ? throw new InvalidOperationException(message: $"D3D11 probe kernel '{entry}' creation returned no shader")
                : shader);
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

                    throw new COMException(errorCode: result.Value, message: $"probe kernel '{entry}' failed to compile: {message}");
                }
            }

            var compiled = code;

            code = null;

            return compiled;
        } finally {
            Release(value: errors);
            Release(value: code);
        }
    }
    private static DXGI_FORMAT ToDxgiFormat(SurfaceFormat format) => (format switch {
        SurfaceFormat.R8G8B8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        SurfaceFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        _ => throw new NotSupportedException(message: $"probe output format {format} is unsupported"),
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
