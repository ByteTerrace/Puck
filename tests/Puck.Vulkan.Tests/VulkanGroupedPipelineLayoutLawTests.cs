using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Testing;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Holds the pipeline layout <see cref="VulkanPipelineLayouts"/> creates for a pipeline created from a
/// <see cref="GpuPipelineLayoutDescription"/> to <see cref="VulkanGroupLayouts.Plan"/>'s tables, over a command table
/// whose layout entry points record what they are handed instead of reaching a driver: one set layout per planned set in
/// set order, each binding at its number, type, count and stage flags, a pipeline layout over those set layouts in set
/// order, and the planned push range. A failed set layout leaves nothing created alive.</summary>
public sealed unsafe class VulkanGroupedPipelineLayoutLawTests {
    private const nint DeviceHandle = 0x0D00;
    private const nint FirstSetLayout = 0x1000;
    private const nint PipelineLayout = 0x7000;

    [ThreadStatic]
    private static Recording? Recorded;

    public static TheoryData<string> Tables => new(values: ["film grain", "film grain, pushing", "pixelate, pushing", "arrays"]);

    private static GpuPipelineLayoutDescription TableNamed(string name) => name switch {
        "film grain" => GpuGroupLayoutTables.FilmGrain(pushesIndex: false),
        "film grain, pushing" => GpuGroupLayoutTables.FilmGrain(pushesIndex: true),
        "pixelate, pushing" => GpuGroupLayoutTables.Pixelate(pushesIndex: true),
        "arrays" => GpuGroupLayoutTables.Arrays(),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(name)),
    };
    private static string Describe(IEnumerable<IEnumerable<(uint Binding, uint Type, uint Count, uint Stages)>> sets) =>
        string.Join(
            separator: " | ",
            values: sets.Select(selector: static (set, index) => $"set {index} [{string.Join(separator: ", ", values: set.Select(selector: static binding => $"{binding.Binding}:{binding.Type}x{binding.Count} s{binding.Stages:X}"))}]")
        );
    private static Recording Create(GpuPipelineLayoutDescription description, int failSetLayout, out VkResult result, out VulkanGroupPipelineLayout? layouts) {
        var recording = new Recording(FailSetLayout: failSetLayout);

        Recorded = recording;

        try {
            var device = new VulkanDeviceCommands(
                deviceHandle: DeviceHandle,
                memory: null,
                procedures: new VulkanProcResolver(
                    getDeviceProcAddr: &Resolve,
                    getInstanceProcAddr: &Resolve
                )
            );

            result = VulkanPipelineLayouts.Create(
                allocator: new HeapAllocator(),
                device: device,
                groups: VulkanGroupLayouts.Plan(description: description),
                layouts: out layouts
            );

            if (layouts is not null) {
                VulkanPipelineLayouts.Destroy(
                    device: device,
                    layouts: layouts
                );
            }
        } finally {
            Recorded = null;
        }

        return recording;
    }

    [MemberData(memberName: nameof(Tables))]
    [Theory]
    public void The_created_set_layouts_and_pipeline_layout_equal_the_plans_tables(string name) {
        var description = TableNamed(name: name);
        var plan = VulkanGroupLayouts.Plan(description: description);
        var recording = Create(
            description: description,
            failSetLayout: -1,
            layouts: out var layouts,
            result: out var result
        );

        Assert.Equal(
            actual: result,
            expected: VkResult.Success
        );
        Assert.Equal(
            actual: Describe(sets: recording.SetLayouts),
            expected: Describe(sets: plan.Sets.Select(selector: static set => set.Bindings.Select(selector: static binding => (binding.Binding, binding.DescriptorType, binding.Count, binding.StageFlags))))
        );

        var created = Enumerable.Range(start: 0, count: plan.Sets.Count).Select(selector: static index => (FirstSetLayout + index)).ToArray();

        // The pipeline layout lists the set layouts in set order, since a pipeline layout places a set by position.
        Assert.Equal(
            actual: recording.PipelineLayoutSets,
            expected: created
        );
        Assert.Equal(
            actual: recording.PushRange,
            expected: ((plan.PushRangeBytes == 0)
                ? null
                : (0U, plan.PushRangeBytes, plan.PushRangeStageFlags))
        );
        Assert.Equal(
            actual: layouts!.PipelineLayoutHandle,
            expected: PipelineLayout
        );
        Assert.Equal(
            actual: layouts.SetLayoutHandles,
            expected: created
        );
        // Destroying the layouts releases the pipeline layout and every set layout it was created over.
        Assert.Equal(
            actual: recording.Destroyed.Order(),
            expected: created.Append(element: PipelineLayout).Order()
        );
    }
    [Fact]
    public void Film_grain_takes_vertex_and_fragment_stage_flags_and_pixelate_compute() {
        var graphics = Create(
            description: GpuGroupLayoutTables.FilmGrain(pushesIndex: true),
            failSetLayout: -1,
            layouts: out _,
            result: out _
        );
        var compute = Create(
            description: GpuGroupLayoutTables.Pixelate(pushesIndex: true),
            failSetLayout: -1,
            layouts: out _,
            result: out _
        );

        // VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT on every binding and the push range, and
        // VK_SHADER_STAGE_COMPUTE_BIT.
        Assert.All(
            action: static binding => Assert.Equal(actual: binding.Stages, expected: 0x11U),
            collection: graphics.SetLayouts.SelectMany(selector: static set => set)
        );
        Assert.Equal(
            actual: graphics.PushRange,
            expected: (0U, 4U, 0x11U)
        );
        Assert.All(
            action: static binding => Assert.Equal(actual: binding.Stages, expected: 0x20U),
            collection: compute.SetLayouts.SelectMany(selector: static set => set)
        );
        Assert.Equal(
            actual: compute.PushRange,
            expected: (0U, 4U, 0x20U)
        );
    }
    [Fact]
    public void A_failed_set_layout_leaves_nothing_created_alive() {
        var recording = Create(
            description: GpuGroupLayoutTables.FilmGrain(pushesIndex: true),
            failSetLayout: 2,
            layouts: out var layouts,
            result: out var result
        );

        Assert.Equal(
            actual: (result, layouts),
            expected: (VkResult.ErrorOutOfDeviceMemory, ((VulkanGroupPipelineLayout?)null))
        );
        Assert.Null(@object: recording.PipelineLayoutSets);
        Assert.Equal(
            actual: recording.Destroyed.Order(),
            expected: [FirstSetLayout, (FirstSetLayout + 1)]
        );
    }

    // A vkGetDeviceProcAddr stand-in: the layout entry points record, and every other entry point resolves to a trap
    // no layout creation reaches.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Resolve(nint handle, byte* name) =>
        Encoding.UTF8.GetString(bytes: MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value: name)) switch {
            "vkCreateDescriptorSetLayout" => ((nint)((delegate* unmanaged[Cdecl]<nint, VkDescriptorSetLayoutCreateInfo*, nint, nint*, VkResult>)&CreateSetLayout)),
            "vkCreatePipelineLayout" => ((nint)((delegate* unmanaged[Cdecl]<nint, VkPipelineLayoutCreateInfo*, nint, nint*, VkResult>)&CreatePipelineLayout)),
            "vkDestroyDescriptorSetLayout" or "vkDestroyPipelineLayout" => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Destroy)),
            _ => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&Trap)),
        };
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult CreateSetLayout(nint device, VkDescriptorSetLayoutCreateInfo* info, nint allocator, nint* handle) {
        var recording = Recorded!;

        if (recording.SetLayouts.Count == recording.FailSetLayout) {
            *handle = 0;

            return VkResult.ErrorOutOfDeviceMemory;
        }

        var bindings = new List<(uint Binding, uint Type, uint Count, uint Stages)>();
        var source = ((VkDescriptorSetLayoutBinding*)info->PBindings);

        for (var index = 0; (index < info->BindingCount); index++) {
            bindings.Add(item: (source[index].Binding, source[index].DescriptorType, source[index].DescriptorCount, source[index].StageFlags));
        }

        *handle = (FirstSetLayout + recording.SetLayouts.Count);
        recording.SetLayouts.Add(item: bindings);

        return VkResult.Success;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult CreatePipelineLayout(nint device, VkPipelineLayoutCreateInfo* info, nint allocator, nint* handle) {
        var recording = Recorded!;
        var sets = ((nint*)info->PSetLayouts);

        recording.PipelineLayoutSets = Enumerable.Range(count: ((int)info->SetLayoutCount), start: 0).Select(selector: index => sets[index]).ToArray();

        if (info->PushConstantRangeCount != 0) {
            var range = *((VkPushConstantRange*)info->PPushConstantRanges);

            recording.PushRange = (range.Offset, range.Size, range.StageFlags);
        }

        *handle = PipelineLayout;

        return VkResult.Success;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destroy(nint device, nint handle, nint allocator) =>
        Recorded!.Destroyed.Add(item: handle);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Trap(nint first, nint second, nint third, nint fourth) => 0;

    private sealed class HeapAllocator : IAllocator {
        public void* Allocate(nuint size, nuint alignment = 0) => NativeMemory.Alloc(byteCount: Math.Max(
            val1: size,
            val2: 1
        ));
        public void Free(void* ptr) => NativeMemory.Free(ptr: ptr);
        public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => NativeMemory.Realloc(
            byteCount: Math.Max(
                val1: newSize,
                val2: 1
            ),
            ptr: ptr
        );
    }
    private sealed record Recording(int FailSetLayout) {
        public List<nint> Destroyed { get; } = [];
        public nint[]? PipelineLayoutSets { get; set; }
        public (uint Offset, uint Size, uint Stages)? PushRange { get; set; }
        public List<List<(uint Binding, uint Type, uint Count, uint Stages)>> SetLayouts { get; } = [];
    }
}
