using System.Runtime.Versioning;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Testing;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
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
        using var context = DirectXTestDevices.Warp(memory: null);
        var calls = new FaultingCommandCalls(real: DirectXDeviceCommandCalls.Of(deviceContext: context));
        using IGpuSurfaceUpload upload = new DirectXSurfaceUpload(
            calls: calls,
            deviceContext: context
        );
        var pixels = new byte[((4 * 4) * 4)];

        _ = upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4);
        calls.FailClose = true;

        var failure = Assert.Throws<DirectXException>(testCode: () => upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4));

        calls.FailClose = false;

        Assert.Equal(
            actual: (failure.Operation, failure.Result),
            expected: ("ID3D12GraphicsCommandList::Close", InvalidArgument)
        );
        Assert.Null(@object: Record.Exception(testCode: () => upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4)));
        Assert.Null(@object: Record.Exception(testCode: () => upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: pixels, width: 4)));
        Assert.Equal(
            actual: calls.Closes,
            expected: 6
        );
    }
    [InlineData(D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D)]
    [InlineData(D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER)]
    [Theory]
    public void AFailedRebuildKeepsTheCurrentViewResolvingAndTheNextUploadWorks(D3D12_RESOURCE_DIMENSION failing) {
        var memory = new GpuDeviceMemoryWork(backend: "directx");
        using var context = DirectXTestDevices.Warp(memory: memory);
        var calls = new FaultingCommandCalls(real: DirectXDeviceCommandCalls.Of(deviceContext: context));
        using IGpuSurfaceUpload upload = new DirectXSurfaceUpload(
            calls: calls,
            deviceContext: context
        );
        var first = upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: new byte[((4 * 4) * 4)], width: 4);
        var firstView = ViewOf(handle: first);
        var held = memory.Held;

        // The rebuild creates a texture and then its staging buffer: refusing either leaves the texture and view it
        // would have replaced as they were, and the texture the first created is released.
        calls.Failing = failing;

        var failure = Assert.Throws<DirectXException>(testCode: () => upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 8, pixels: new byte[((8 * 8) * 4)], width: 8));

        calls.Failing = null;

        Assert.Equal(
            actual: (failure.Operation, failure.Result),
            expected: ("ID3D12Device::CreateCommittedResource", OutOfMemory)
        );
        Assert.Equal(
            actual: (DirectXImageViews.Resolve(handle: first), memory.Held),
            expected: (firstView, held)
        );
        Assert.Equal(
            actual: upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: new byte[((4 * 4) * 4)], width: 4),
            expected: first
        );
        Assert.Null(@object: Record.Exception(testCode: () => upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 8, pixels: new byte[((8 * 8) * 4)], width: 8)));
    }
    [Fact]
    public void ASuccessfulRebuildRetiresTheViewOfTheTextureItReplaced() {
        using var context = DirectXTestDevices.Warp(memory: null);
        using var upload = context.Services.SurfaceTransferFactory.CreateUpload();
        var first = upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: new byte[((4 * 4) * 4)], width: 4);

        Assert.NotNull(@object: DirectXImageViews.Resolve(handle: first));

        var second = upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 8, pixels: new byte[((8 * 8) * 4)], width: 8);

        Assert.Null(@object: DirectXImageViews.Resolve(handle: first));
        Assert.NotNull(@object: DirectXImageViews.Resolve(handle: second));
        upload.Dispose();
        upload.Dispose();
        Assert.Null(@object: DirectXImageViews.Resolve(handle: second));
    }

    // Answers every call as the device does, except the creation of a committed resource of the dimension a law arms, and
    // the list close it arms, to fail. A failed close answers without closing, so the list stays open exactly as a close the driver refused leaves it.
    private sealed class FaultingCommandCalls(DirectXDeviceCommandCalls real) : IDirectXCommandCalls {
        public int Closes { get; private set; }
        public bool FailClose { get; set; }
        public D3D12_RESOURCE_DIMENSION? Failing { get; set; }

        public HRESULT CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE type, ID3D12CommandAllocator** allocator) => real.CreateCommandAllocator(allocator: allocator, type: type);
        public HRESULT CreateCommandList(D3D12_COMMAND_LIST_TYPE type, ID3D12CommandAllocator* allocator, ID3D12GraphicsCommandList** commandList) => real.CreateCommandList(allocator: allocator, commandList: commandList, type: type);
        public HRESULT CreateCommittedResource(D3D12_HEAP_PROPERTIES* heapProperties, D3D12_HEAP_FLAGS heapFlags, D3D12_RESOURCE_DESC* description, D3D12_RESOURCE_STATES initialState, D3D12_CLEAR_VALUE* clearValue, ID3D12Resource** resource) =>
            ((Failing == description->Dimension)
                ? new HRESULT(value: OutOfMemory)
                : real.CreateCommittedResource(clearValue: clearValue, description: description, heapFlags: heapFlags, heapProperties: heapProperties, initialState: initialState, resource: resource));
        public HRESULT CreateQueryHeap(D3D12_QUERY_HEAP_DESC* description, ID3D12QueryHeap** heap) => real.CreateQueryHeap(description: description, heap: heap);
        public HRESULT Map(ID3D12Resource* resource, void** data) => real.Map(data: data, resource: resource);
        public HRESULT ResetAllocator(ID3D12CommandAllocator* allocator) => real.ResetAllocator(allocator: allocator);
        public HRESULT ResetList(ID3D12GraphicsCommandList* commandList, ID3D12CommandAllocator* allocator) => real.ResetList(allocator: allocator, commandList: commandList);
        public HRESULT Close(ID3D12GraphicsCommandList* commandList) {
            Closes++;

            return (FailClose
                ? new HRESULT(value: InvalidArgument)
                : real.Close(commandList: commandList));
        }
        public HRESULT Signal(ID3D12CommandQueue* queue, ID3D12Fence* fence, ulong value) => real.Signal(fence: fence, queue: queue, value: value);
        public HRESULT QueueWait(ID3D12CommandQueue* queue, ID3D12Fence* fence, ulong value) => real.QueueWait(fence: fence, queue: queue, value: value);
        public ulong CompletedValue(ID3D12Fence* fence) => real.CompletedValue(fence: fence);
        public HRESULT SetEventOnCompletion(ID3D12Fence* fence, ulong value, HANDLE fenceEvent) => real.SetEventOnCompletion(fence: fence, fenceEvent: fenceEvent, value: value);
        public HRESULT TimestampFrequency(ID3D12CommandQueue* queue, ulong* frequency) => real.TimestampFrequency(frequency: frequency, queue: queue);
        public HRESULT DeviceRemovedReason() => real.DeviceRemovedReason();
    }
}
