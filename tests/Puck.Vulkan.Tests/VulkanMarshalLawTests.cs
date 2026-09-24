using System.Runtime.InteropServices;

using Puck.Abstractions.Memory;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins the backend's native marshalling without a device: <see cref="Utf8StringArray"/> and
/// <see cref="VulkanMarshalHelpers.AllocateArray{T}"/> take every byte from the injected <see cref="IAllocator"/>, so a
/// counting allocator that refuses the k-th allocation shows a failure partway through leaves nothing allocated.</summary>
public sealed class VulkanMarshalLawTests {
    // The array first, then one allocation per string: failing index k refuses the array (k = 0) or string k - 1.
    private static readonly string[] Names = ["VK_KHR_swapchain", "VK_EXT_debug_utils", "VK_KHR_présent_wait"];

    public static TheoryData<int> EveryAllocationOfTheNames() {
        var data = new TheoryData<int>();

        for (var index = 0; (index <= Names.Length); index++) {
            data.Add(row: index);
        }

        return data;
    }
    [MemberData(nameof(EveryAllocationOfTheNames))]
    [Theory]
    public void AFailedAllocationPartwayThroughAStringArrayFreesEverythingAllocatedBeforeIt(int failingAllocation) {
        var allocator = new CountingAllocator(failingAllocation: failingAllocation);

        _ = Assert.Throws<OutOfMemoryException>(testCode: () => Utf8StringArray.Create(
            allocator: allocator,
            values: Names
        ));
        Assert.Equal(
            actual: allocator.Attempts,
            expected: (failingAllocation + 1)
        );
        Assert.Equal(
            actual: allocator.Live,
            expected: 0
        );
    }
    [Fact]
    public unsafe void AStringArrayHoldsEachNameNulTerminatedAndDisposeFreesThemAll() {
        var allocator = new CountingAllocator(failingAllocation: -1);
        var array = Utf8StringArray.Create(
            allocator: allocator,
            values: Names
        );

        Assert.Equal(
            actual: array.Count,
            expected: Names.Length
        );
        Assert.Equal(
            actual: allocator.Live,
            expected: (Names.Length + 1)
        );

        for (var index = 0; (index < Names.Length); index++) {
            Assert.Equal(
                actual: Marshal.PtrToStringUTF8(ptr: ((nint*)array.Pointer)[index]),
                expected: Names[index]
            );
        }

        array.Dispose();
        array.Dispose();
        Assert.Equal(
            actual: allocator.Live,
            expected: 0
        );
        Assert.Equal(
            actual: array.Pointer,
            expected: 0
        );
    }
    [Fact]
    public void AnEmptyStringArrayAllocatesNothing() {
        var allocator = new CountingAllocator(failingAllocation: -1);

        using var array = Utf8StringArray.Create(
            allocator: allocator,
            values: []
        );

        Assert.Equal(
            actual: array.Pointer,
            expected: 0
        );
        Assert.Equal(
            actual: allocator.Attempts,
            expected: 0
        );
    }
    [Fact]
    public unsafe void AnArrayCopiesEveryElementContiguously() {
        var allocator = new CountingAllocator(failingAllocation: -1);
        uint[] values = [7, 11, 13];
        var pointer = VulkanMarshalHelpers.AllocateArray(
            allocator: allocator,
            values: values
        );

        Assert.Equal(
            actual: new ReadOnlySpan<uint>(
                length: values.Length,
                pointer: ((void*)pointer)
            ).ToArray(),
            expected: values
        );
        allocator.Free(ptr: pointer);
        Assert.Equal(
            actual: allocator.Live,
            expected: 0
        );
        Assert.Equal(
            actual: VulkanMarshalHelpers.AllocateArray<uint>(
                allocator: allocator,
                values: []
            ),
            expected: 0
        );
    }
    [Fact]
    public void AnArrayTheAllocatorRefusesThrowsAndHoldsNothing() {
        var allocator = new CountingAllocator(failingAllocation: 0);

        _ = Assert.Throws<OutOfMemoryException>(testCode: () => VulkanMarshalHelpers.AllocateArray<uint>(
            allocator: allocator,
            values: [1, 2]
        ));
        Assert.Equal(
            actual: allocator.Live,
            expected: 0
        );
    }
    [Fact]
    public void TheEntryPointIsOneNulTerminatedMainForEveryStage() {
        Assert.Equal(
            actual: Marshal.PtrToStringUTF8(ptr: VulkanMarshalHelpers.MainEntryPoint),
            expected: "main"
        );
        Assert.Equal(
            actual: VulkanMarshalHelpers.MainEntryPoint,
            expected: VulkanMarshalHelpers.MainEntryPoint
        );
    }

    /// <summary>Counts live blocks and refuses exactly one allocation, by its zero-based index, the way
    /// <see cref="IAllocator"/> reports a failure: with <see langword="null"/>.</summary>
    private sealed unsafe class CountingAllocator(int failingAllocation) : IAllocator {
        private readonly HashSet<nint> m_live = [];

        public int Attempts { get; private set; }
        public int Live => m_live.Count;

        public void* Allocate(nuint size, nuint alignment = 0) {
            if (failingAllocation == Attempts++) {
                return null;
            }

            var block = NativeMemory.Alloc(byteCount: Math.Max(
                val1: size,
                val2: 1
            ));

            _ = m_live.Add(item: ((nint)block));
            return block;
        }
        public void Free(void* ptr) {
            if (null == ptr) {
                return;
            }

            Assert.True(condition: m_live.Remove(item: ((nint)ptr)));
            NativeMemory.Free(ptr: ptr);
        }
        public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => throw new NotSupportedException();
    }
}
