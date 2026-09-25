using Puck.Abstractions.Gpu;

namespace Puck.Testing;

/// <summary>The neutral pipeline layouts both backends' planners are held to. The two spike layouts are the groups the
/// gate spike's film grain and pixelate interfaces lay out, which <c>ShaderInterfaceSpikeTests</c> holds both bytecode
/// readers to and <c>GpuGroupLayoutTableLawTests</c> holds the interfaces to. The refusals are descriptions no backend
/// may plan, each with the text its refusal names.</summary>
internal static class GpuGroupLayoutTables {
    /// <summary>Film grain: the frame block at set 0, and the pass block, a sampled image and a sampler at set 3. It is
    /// a fullscreen pass, so its stages are vertex and fragment.</summary>
    internal static GpuPipelineLayoutDescription FilmGrain(bool pushesIndex) =>
        new(
            groups: [
                new GpuGroupLayoutDescription(
                    bindings: [new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ConstantBuffer)],
                    ordinal: 0
                ),
                new GpuGroupLayoutDescription(
                    bindings: [
                        new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ConstantBuffer),
                        new GpuGroupBinding(binding: 1, kind: GpuBindingKind.SampledImage),
                        new GpuGroupBinding(binding: 2, kind: GpuBindingKind.Sampler),
                    ],
                    ordinal: 3
                ),
            ],
            pushesIndex: pushesIndex,
            stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment
        );
    /// <summary>Pixelate: the frame block at set 0, and the pass block and two storage images at set 3. It is a compute
    /// pass.</summary>
    internal static GpuPipelineLayoutDescription Pixelate(bool pushesIndex) =>
        new(
            groups: [
                new GpuGroupLayoutDescription(
                    bindings: [new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ConstantBuffer)],
                    ordinal: 0
                ),
                new GpuGroupLayoutDescription(
                    bindings: [
                        new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ConstantBuffer),
                        new GpuGroupBinding(binding: 1, kind: GpuBindingKind.StorageImage),
                        new GpuGroupBinding(binding: 2, kind: GpuBindingKind.StorageImage),
                    ],
                    ordinal: 3
                ),
            ],
            pushesIndex: pushesIndex,
            stages: GpuShaderStage.Compute
        );
    /// <summary>Every kind at once, arrays included, declared out of order: a world group of a read-only buffer array, a
    /// sampler array and a read-write buffer, and an instance group of samplers alone, read by a fragment stage alone,
    /// so one stage narrows every parameter's visibility.</summary>
    internal static GpuPipelineLayoutDescription Arrays() =>
        new(
            groups: [
                new GpuGroupLayoutDescription(
                    bindings: [new GpuGroupBinding(binding: 0, count: 2, kind: GpuBindingKind.Sampler)],
                    ordinal: 2
                ),
                new GpuGroupLayoutDescription(
                    bindings: [
                        new GpuGroupBinding(binding: 5, kind: GpuBindingKind.ReadWriteBuffer),
                        new GpuGroupBinding(binding: 0, count: 3, kind: GpuBindingKind.ReadOnlyBuffer),
                        new GpuGroupBinding(binding: 3, count: 2, kind: GpuBindingKind.Sampler),
                    ],
                    ordinal: 1
                ),
            ],
            pushesIndex: true,
            stages: GpuShaderStage.Fragment
        );

    /// <summary>Gets the descriptions no backend may plan, each with the text its refusal carries.</summary>
    internal static IReadOnlyList<(string Name, Func<GpuPipelineLayoutDescription> Build, string Refusal)> Refused { get; } = [
        ("the push index's space as a group", static () => Single(ordinal: 4, bindings: [new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ConstantBuffer)]), "Group 4 is not a frequency group"),
        ("a group with no binding", static () => Single(bindings: [], ordinal: 3), "Group 3 holds no binding"),
        ("one binding declared twice", static () => Single(ordinal: 3, bindings: [new GpuGroupBinding(binding: 1, kind: GpuBindingKind.SampledImage), new GpuGroupBinding(binding: 1, kind: GpuBindingKind.Sampler)]), "Group 3 declares binding 1 twice"),
        ("a binding inside an array", static () => Single(ordinal: 1, bindings: [new GpuGroupBinding(binding: 0, count: 3, kind: GpuBindingKind.SampledImage), new GpuGroupBinding(binding: 2, kind: GpuBindingKind.Sampler)]), "Group 1 binding 2 lies inside binding 0's array of 3"),
        ("a default binding, which holds no descriptor", static () => Single(bindings: [default], ordinal: 0), "Group 0 binding 0 holds an undefined kind or no descriptor"),
        ("one group declared twice", static () => new GpuPipelineLayoutDescription(groups: [Group(ordinal: 3), Group(ordinal: 3)], pushesIndex: false, stages: GpuShaderStage.Compute), "Group 3 is declared twice"),
        ("no stage", static () => new GpuPipelineLayoutDescription(groups: [Group(ordinal: 3)], pushesIndex: false, stages: GpuShaderStage.None), "'None' is neither"),
        ("compute beside a graphics stage", static () => new GpuPipelineLayoutDescription(groups: [Group(ordinal: 3)], pushesIndex: false, stages: GpuShaderStage.Compute | GpuShaderStage.Fragment), "'Fragment, Compute' is neither"),
        ("a stage that is not defined", static () => new GpuPipelineLayoutDescription(groups: [Group(ordinal: 3)], pushesIndex: false, stages: ((GpuShaderStage)0x2)), "'2' is neither"),
    ];

    private static GpuGroupLayoutDescription Group(uint ordinal) =>
        new(
            bindings: [new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ConstantBuffer)],
            ordinal: ordinal
        );
    private static GpuPipelineLayoutDescription Single(uint ordinal, GpuGroupBinding[] bindings) =>
        new(
            groups: [new GpuGroupLayoutDescription(bindings: bindings, ordinal: ordinal)],
            pushesIndex: false,
            stages: GpuShaderStage.Compute
        );
}
