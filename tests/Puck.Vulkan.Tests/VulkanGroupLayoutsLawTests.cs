using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Holds <see cref="VulkanGroupLayouts.Plan"/> to the gate spike's two-group layouts and to the placement
/// rule: every group is the set its ordinal names, every binding keeps its number, kind and count, set numbers no
/// group uses are empty layouts, and the pushed index is a 4-byte range. A description no backend may plan is refused
/// by name before it reaches the planner.</summary>
public sealed class VulkanGroupLayoutsLawTests {
    private const GpuShaderStage Graphics = GpuShaderStage.Vertex | GpuShaderStage.Fragment;

    public static TheoryData<string> RefusalNames => new(values: GpuGroupLayoutTables.Refused.Select(selector: static refusal => refusal.Name).ToArray());

    private static string Describe(IEnumerable<(uint Set, VulkanSetLayoutBinding[] Bindings)> sets) =>
        string.Join(
            separator: " | ",
            values: sets.Select(selector: static set => $"set {set.Set} [{string.Join(separator: ", ", values: set.Bindings.Select(selector: static binding => $"{binding.Binding}:{binding.DescriptorType}x{binding.Count} s{binding.StageFlags:X}"))}]")
        );
    private static string Describe(VulkanGroupLayouts layouts) =>
        Describe(sets: layouts.Sets.Select(selector: static set => (set.Set, set.Bindings.ToArray())));
    private static VulkanSetLayoutBinding Binding(uint binding, uint type, GpuShaderStage stages, uint count = 1) =>
        new(
            Binding: binding,
            Count: count,
            DescriptorType: type,
            StageFlags: ((uint)stages)
        );

    [Fact]
    public void Film_grain_plans_the_frame_set_the_pass_set_and_two_empty_sets_between() {
        var layouts = VulkanGroupLayouts.Plan(description: GpuGroupLayoutTables.FilmGrain(pushesIndex: false));

        Assert.Equal(
            actual: Describe(layouts: layouts),
            expected: Describe(sets: [
                (0, [Binding(binding: 0, type: VulkanDescriptorType.UniformBuffer, stages: Graphics)]),
                (1, []),
                (2, []),
                (3, [Binding(binding: 0, type: VulkanDescriptorType.UniformBuffer, stages: Graphics), Binding(binding: 1, type: VulkanDescriptorType.SampledImage, stages: Graphics), Binding(binding: 2, type: VulkanDescriptorType.Sampler, stages: Graphics)]),
            ])
        );
        Assert.Equal(
            actual: layouts.PushRangeBytes,
            expected: 0u
        );
    }
    [Fact]
    public void Pixelate_plans_the_frame_set_and_a_pass_set_of_two_storage_images() {
        Assert.Equal(
            actual: Describe(layouts: VulkanGroupLayouts.Plan(description: GpuGroupLayoutTables.Pixelate(pushesIndex: false))),
            expected: Describe(sets: [
                (0, [Binding(binding: 0, type: VulkanDescriptorType.UniformBuffer, stages: GpuShaderStage.Compute)]),
                (1, []),
                (2, []),
                (3, [Binding(binding: 0, type: VulkanDescriptorType.UniformBuffer, stages: GpuShaderStage.Compute), Binding(binding: 1, type: VulkanDescriptorType.StorageImage, stages: GpuShaderStage.Compute), Binding(binding: 2, type: VulkanDescriptorType.StorageImage, stages: GpuShaderStage.Compute)]),
            ])
        );
    }
    [Fact]
    public void The_pushed_index_is_one_4_byte_range_visible_to_the_pipelines_stages() {
        var graphics = VulkanGroupLayouts.Plan(description: GpuGroupLayoutTables.FilmGrain(pushesIndex: true));
        var compute = VulkanGroupLayouts.Plan(description: GpuGroupLayoutTables.Pixelate(pushesIndex: true));
        var none = VulkanGroupLayouts.Plan(description: GpuGroupLayoutTables.Pixelate(pushesIndex: false));

        // VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT, and VK_SHADER_STAGE_COMPUTE_BIT.
        Assert.Equal(
            actual: (graphics.PushRangeBytes, graphics.PushRangeStageFlags),
            expected: (4u, 0x11u)
        );
        Assert.Equal(
            actual: (compute.PushRangeBytes, compute.PushRangeStageFlags),
            expected: (4u, 0x20u)
        );
        Assert.Equal(
            actual: (none.PushRangeBytes, none.PushRangeStageFlags),
            expected: (0u, 0u)
        );
    }
    [Fact]
    public void Arrays_keep_their_counts_and_both_buffer_kinds_are_storage_buffers() {
        Assert.Equal(
            actual: Describe(layouts: VulkanGroupLayouts.Plan(description: GpuGroupLayoutTables.Arrays())),
            expected: Describe(sets: [
                (0, []),
                (1, [Binding(binding: 0, count: 3, stages: GpuShaderStage.Fragment, type: VulkanDescriptorType.StorageBuffer), Binding(binding: 3, count: 2, stages: GpuShaderStage.Fragment, type: VulkanDescriptorType.Sampler), Binding(binding: 5, type: VulkanDescriptorType.StorageBuffer, stages: GpuShaderStage.Fragment)]),
                (2, [Binding(binding: 0, count: 2, stages: GpuShaderStage.Fragment, type: VulkanDescriptorType.Sampler)]),
            ])
        );
    }
    [Fact]
    public void A_pipeline_that_binds_nothing_plans_no_set() {
        var layouts = VulkanGroupLayouts.Plan(description: new GpuPipelineLayoutDescription(
            groups: [],
            pushesIndex: false,
            stages: GpuShaderStage.Compute
        ));

        Assert.Empty(collection: layouts.Sets);
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void An_illegal_description_is_refused_by_name_before_planning(string name) {
        var (_, build, refusal) = GpuGroupLayoutTables.Refused.Single(predicate: entry => (entry.Name == name));
        var exception = Assert.Throws<ArgumentException>(testCode: () => VulkanGroupLayouts.Plan(description: build()));

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: refusal
        );
    }
}
