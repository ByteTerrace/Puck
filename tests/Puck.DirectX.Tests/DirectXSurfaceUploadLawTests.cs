using System.Runtime.Versioning;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Testing;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Laws for the device's surface upload (<see cref="IGpuSurfaceUpload"/> on Direct3D 12): uploads of an
/// unchanged extent and format reuse the texture and hand back the same image view, allocating nothing once warm, and
/// an upload that rebuilds the texture hands back a new view naming it. An extent past Direct3D 12's two-dimensional
/// texture limit is refused by name before the current texture is touched, and an upload whose recording fails leaves
/// the instance able to upload again. Each law runs on a software (WARP) device without the debug layer and skips when
/// the host has none that meets the device floor.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXSurfaceUploadLawTests {
    private const int OutOfMemory = unchecked((int)0x8007000E);
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const uint TextureLimit = 16384U;

    private static DirectXImageView ViewOf(nint handle) =>
        DirectXImageViews.Resolve(handle: handle)!;

    [Fact]
    public void ASteadyUploadReusesItsViewAndAllocatesNothing() {
        using var context = DirectXTestDevices.Warp(memory: null);
        using var upload = context.Services.SurfaceTransferFactory.CreateUpload();
        ReadOnlyMemory<byte> pixels = new byte[((8 * 8) * 4)];

        nint Steady() => upload.Upload(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: 8U,
            pixels: pixels,
            width: 8U
        );

        var first = Steady();

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => _ = Steady()),
            expected: 0L
        );
        Assert.Equal(
            actual: Steady(),
            expected: first
        );
    }
    [Fact]
    public void AnUploadThatRebuildsTheTextureHandsBackAViewOfIt() {
        using var context = DirectXTestDevices.Warp(memory: null);
        using var upload = context.Services.SurfaceTransferFactory.CreateUpload();
        var small = upload.Upload(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: 4U,
            pixels: new byte[((4 * 4) * 4)],
            width: 4U
        );
        var smallFormat = ViewOf(handle: small).Format;
        var large = upload.Upload(
            format: GpuPixelFormat.B8G8R8A8Unorm,
            height: 16U,
            pixels: new byte[((16 * 16) * 4)],
            width: 16U
        );
        var largeView = ViewOf(handle: large);

        Assert.Equal(
            actual: (smallFormat, largeView.Format),
            expected: (DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: GpuPixelFormat.R8G8B8A8Unorm), DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: GpuPixelFormat.B8G8R8A8Unorm))
        );
        Assert.NotEqual(
            actual: largeView.ResourceHandle,
            expected: 0
        );
    }
    [Fact]
    public void AnOversizedUploadIsRefusedByNameAndLeavesTheCurrentViewResolving() {
        using var context = DirectXTestDevices.Warp(memory: null);
        using var upload = context.Services.SurfaceTransferFactory.CreateUpload();
        var first = upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: new byte[((4 * 4) * 4)], width: 4);

        var wide = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 1, pixels: new byte[((TextureLimit + 1) * 4)], width: (TextureLimit + 1)));
        var tall = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: (TextureLimit + 1), pixels: new byte[((TextureLimit + 1) * 4)], width: 1));

        Assert.Equal(
            actual: (wide.ParamName, tall.ParamName),
            expected: ("width", "height")
        );
        Assert.Contains(
            actualString: wide.Message,
            expectedSubstring: $"{TextureLimit} texels"
        );
        Assert.Contains(
            actualString: tall.Message,
            expectedSubstring: $"{TextureLimit} texels"
        );
        Assert.NotNull(@object: DirectXImageViews.Resolve(handle: first));
        Assert.Equal(
            actual: upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: new byte[((4 * 4) * 4)], width: 4),
            expected: first
        );
    }
    [Fact]
    public void AnUploadAfterAFailedCloseRecordsAgainOnAFreshList() {
        using var rig = new Rig(memory: null);
        var pixels = new byte[((4 * 4) * 4)];

        _ = rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4);
        rig.Calls.FailClose = true;

        var failure = Assert.Throws<DirectXException>(testCode: () => rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4));

        rig.Calls.FailClose = false;

        Assert.Equal(
            actual: (failure.Operation, failure.Result),
            expected: ("ID3D12GraphicsCommandList::Close", InvalidArgument)
        );
        Assert.Null(@object: Record.Exception(testCode: () => rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4)));
        Assert.Null(@object: Record.Exception(testCode: () => rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4)));
        Assert.Equal(
            actual: rig.Calls.Closes,
            expected: 6
        );
        rig.Settled = true;
    }
    [Fact]
    public void AnUploadSettlesASubmissionItsWaitNeverConfirmedBeforeItReusesOrReplacesAnythingTheGpuHolds() {
        using var rig = new Rig(memory: null);
        var pixels = new byte[((4 * 4) * 4)];

        // The first upload's copy waits behind a fence the law holds, and the wait's event cannot be armed: the
        // submission is outstanding. The second upload's recording fails before it resets its allocator, which leaves the
        // list to be replaced by the third.
        rig.Calls.HoldNextSubmission = true;
        rig.Calls.FailNextEventArm = true;

        var arm = Assert.Throws<DirectXException>(testCode: () => rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4));

        rig.Calls.FailNextAllocatorReset = true;

        var reset = Assert.Throws<DirectXException>(testCode: () => rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4));
        var third = Record.Exception(testCode: () => rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4));

        Assert.Equal(
            actual: (arm.Operation, arm.Result, reset.Operation),
            expected: ("ID3D12Fence::SetEventOnCompletion", OutOfMemory, "ID3D12CommandAllocator::Reset")
        );
        Assert.Null(@object: third);
        Assert.False(
            condition: rig.Calls.ReplacedAllocatorWhilePending,
            userMessage: "A command allocator was replaced while the queue still held its submission."
        );
        Assert.True(
            condition: rig.Calls.HoldReleasedBySettling,
            userMessage: "The second upload did not wait for the outstanding submission."
        );
        rig.Settled = true;
    }
    [InlineData(D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D)]
    [InlineData(D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER)]
    [Theory]
    public void AFailedRebuildKeepsTheCurrentViewResolvingAndTheNextUploadWorks(D3D12_RESOURCE_DIMENSION failing) {
        var memory = new GpuDeviceMemoryWork(backend: "directx");

        using var rig = new Rig(memory: memory);
        var first = rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: new byte[((4 * 4) * 4)], width: 4);
        var firstView = ViewOf(handle: first);
        var held = memory.Held;

        // The rebuild creates a texture and then its staging buffer: refusing either leaves the texture and view it
        // would have replaced as they were, and releases whatever the rebuild created before the refusal.
        rig.Calls.Failing = failing;

        var failure = Assert.Throws<DirectXException>(testCode: () => rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 8, pixels: new byte[((8 * 8) * 4)], width: 8));

        rig.Calls.Failing = null;

        Assert.Equal(
            actual: (failure.Operation, failure.Result),
            expected: ("ID3D12Device::CreateCommittedResource", OutOfMemory)
        );
        Assert.Equal(
            actual: (DirectXImageViews.Resolve(handle: first), memory.Held),
            expected: (firstView, held)
        );
        Assert.Equal(
            actual: rig.Calls.Created.Select(selector: created => rig.Calls.ReferencesTo(resource: created.Resource)),
            expected: rig.Calls.Created.Select(selector: static (created, index) => ((index < 2)
                ? 2
                : 1))
        );
        Assert.Equal(
            actual: rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: new byte[((4 * 4) * 4)], width: 4),
            expected: first
        );
        Assert.Null(@object: Record.Exception(testCode: () => rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 8, pixels: new byte[((8 * 8) * 4)], width: 8)));
        rig.Settled = true;
    }
    [Fact]
    public void ASuccessfulRebuildRetiresTheViewAndEveryObjectOfTheTextureItReplaced() {
        using var rig = new Rig(memory: null);
        var first = rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: new byte[((4 * 4) * 4)], width: 4);

        Assert.NotNull(@object: DirectXImageViews.Resolve(handle: first));

        var second = rig.Upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 8, pixels: new byte[((8 * 8) * 4)], width: 8);

        Assert.Null(@object: DirectXImageViews.Resolve(handle: first));
        Assert.NotNull(@object: DirectXImageViews.Resolve(handle: second));
        Assert.Equal(
            actual: rig.Calls.Created.Select(selector: created => rig.Calls.ReferencesTo(resource: created.Resource)),
            expected: new[] { 1, 1, 2, 2 }
        );
        rig.Upload.Dispose();
        rig.Upload.Dispose();
        Assert.Null(@object: DirectXImageViews.Resolve(handle: second));
        Assert.Equal(
            actual: rig.Calls.Created.Select(selector: created => rig.Calls.ReferencesTo(resource: created.Resource)),
            expected: new[] { 1, 1, 1, 1 }
        );
        rig.Settled = true;
    }

    // A software device whose upload asks a FaultingCommandCalls, torn down so that a leak the body already asserted on
    // is never replaced by the teardown's report of it.
    private sealed class Rig : IDisposable {
        public FaultingCommandCalls Calls { get; }
        public DirectXDeviceContext Context { get; }
        public bool Settled { get; set; }
        public IGpuSurfaceUpload Upload { get; }

        public Rig(GpuDeviceMemoryWork? memory) {
            Context = DirectXTestDevices.Warp(memory: memory);
            Calls = new FaultingCommandCalls(context: Context);
            Upload = new DirectXSurfaceUpload(
                calls: Calls,
                deviceContext: Context
            );
        }

        public void Dispose() {
            try {
                Upload.Dispose();
                Calls.ReleaseHolds();
                Context.Dispose();
            } catch (Exception) when (!Settled) {
                // The body failed first; its failure is the verdict, and a leak it left would only replace it.
            }
        }
    }
    // Answers every call as the device does, except the creation of a committed resource of the dimension a law arms,
    // and the list close it arms, to fail. It holds one reference to every resource the upload creates, so a law reads
    // how many references the upload still owns, and it can hold the queue behind a fence the law releases. A failed
    // close answers without closing, so the list stays open exactly as a close the driver refused leaves it.
    private sealed class FaultingCommandCalls : IDirectXCommandCalls {
        private readonly DirectXDeviceCommandCalls m_real;
        private readonly DirectXDeviceContext m_context;

        private nint m_hold;
        private nint m_lastFence;
        private ulong m_lastSignalled;
        private bool m_released;

        public int Closes { get; private set; }
        public List<(D3D12_RESOURCE_DIMENSION Dimension, nint Resource)> Created { get; } = [];
        public bool FailClose { get; set; }
        public bool FailNextAllocatorReset { get; set; }
        public bool FailNextEventArm { get; set; }
        public D3D12_RESOURCE_DIMENSION? Failing { get; set; }
        public bool HoldNextSubmission { get; set; }
        public bool HoldReleasedBySettling { get; private set; }
        public bool ReplacedAllocatorWhilePending { get; private set; }

        public FaultingCommandCalls(DirectXDeviceContext context) {
            m_context = context;
            m_real = DirectXDeviceCommandCalls.Of(deviceContext: context);
        }

        public int ReferencesTo(nint resource) {
            var count = ((IUnknown*)resource)->AddRef();

            _ = ((IUnknown*)resource)->Release();

            return (((int)count) - 1);
        }
        public void ReleaseHolds() {
            ReleaseHold();

            foreach (var (_, resource) in Created) {
                _ = ((IUnknown*)resource)->Release();
            }

            Created.Clear();

            if (0 != m_hold) {
                _ = ((IUnknown*)m_hold)->Release();
                m_hold = 0;
            }
        }

        private void ReleaseHold() {
            if (
                (0 != m_hold) &&
                !m_released
            ) {
                m_released = true;
                ((ID3D12Fence*)m_hold)->Signal(Value: 1UL);
            }
        }

        public HRESULT CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE type, ID3D12CommandAllocator** allocator) {
            if (
                (0 != m_lastFence) &&
                (m_real.CompletedValue(fence: ((ID3D12Fence*)m_lastFence)) < m_lastSignalled)
            ) {
                ReplacedAllocatorWhilePending = true;
            }

            ReleaseHold();

            return m_real.CreateCommandAllocator(allocator: allocator, type: type);
        }
        public HRESULT CreateCommandList(D3D12_COMMAND_LIST_TYPE type, ID3D12CommandAllocator* allocator, ID3D12GraphicsCommandList** commandList) => m_real.CreateCommandList(allocator: allocator, commandList: commandList, type: type);
        public HRESULT CreateCommittedResource(D3D12_HEAP_PROPERTIES* heapProperties, D3D12_HEAP_FLAGS heapFlags, D3D12_RESOURCE_DESC* description, D3D12_RESOURCE_STATES initialState, D3D12_CLEAR_VALUE* clearValue, ID3D12Resource** resource) {
            if (Failing == description->Dimension) {
                return new HRESULT(value: OutOfMemory);
            }

            var result = m_real.CreateCommittedResource(clearValue: clearValue, description: description, heapFlags: heapFlags, heapProperties: heapProperties, initialState: initialState, resource: resource);

            if (result.Succeeded) {
                _ = ((IUnknown*)(*resource))->AddRef();
                Created.Add(item: (description->Dimension, ((nint)(*resource))));
            }

            return result;
        }
        public HRESULT CreateQueryHeap(D3D12_QUERY_HEAP_DESC* description, ID3D12QueryHeap** heap) => m_real.CreateQueryHeap(description: description, heap: heap);
        public HRESULT Map(ID3D12Resource* resource, void** data) => m_real.Map(data: data, resource: resource);
        public HRESULT ResetAllocator(ID3D12CommandAllocator* allocator) {
            if (FailNextAllocatorReset) {
                FailNextAllocatorReset = false;

                return new HRESULT(value: unchecked((int)0x80004005));
            }

            return m_real.ResetAllocator(allocator: allocator);
        }
        public HRESULT ResetList(ID3D12GraphicsCommandList* commandList, ID3D12CommandAllocator* allocator) => m_real.ResetList(allocator: allocator, commandList: commandList);
        public HRESULT Close(ID3D12GraphicsCommandList* commandList) {
            Closes++;

            if (FailClose) {
                return new HRESULT(value: InvalidArgument);
            }

            var result = m_real.Close(commandList: commandList);

            if (
                HoldNextSubmission &&
                result.Succeeded
            ) {
                HoldNextSubmission = false;

                var device = ((ID3D12Device*)m_context.Device.Handle);

                device->CreateFence(
                    Flags: default,
                    InitialValue: 0,
                    ppFence: out var created,
                    riid: ID3D12Fence.IID_Guid
                );
                var hold = ((ID3D12Fence*)created);

                m_hold = ((nint)hold);
                m_released = false;
                _ = m_real.QueueWait(
                    fence: hold,
                    queue: ((ID3D12CommandQueue*)m_context.CommandQueueHandle),
                    value: 1UL
                );
            }

            return result;
        }
        public HRESULT Signal(ID3D12CommandQueue* queue, ID3D12Fence* fence, ulong value) {
            m_lastFence = ((nint)fence);
            m_lastSignalled = value;

            return m_real.Signal(fence: fence, queue: queue, value: value);
        }
        public HRESULT QueueWait(ID3D12CommandQueue* queue, ID3D12Fence* fence, ulong value) => m_real.QueueWait(fence: fence, queue: queue, value: value);
        public ulong CompletedValue(ID3D12Fence* fence) => m_real.CompletedValue(fence: fence);
        public HRESULT SetEventOnCompletion(ID3D12Fence* fence, ulong value, HANDLE fenceEvent) {
            if (FailNextEventArm) {
                FailNextEventArm = false;

                return new HRESULT(value: OutOfMemory);
            }

            if (
                (0 != m_hold) &&
                !m_released
            ) {
                HoldReleasedBySettling = true;
                ReleaseHold();
            }

            return m_real.SetEventOnCompletion(fence: fence, fenceEvent: fenceEvent, value: value);
        }
        public HRESULT TimestampFrequency(ID3D12CommandQueue* queue, ulong* frequency) => m_real.TimestampFrequency(frequency: frequency, queue: queue);
        public HRESULT DeviceRemovedReason() => m_real.DeviceRemovedReason();
    }
}
