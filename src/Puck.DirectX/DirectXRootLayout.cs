using System.Collections.ObjectModel;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>What a root parameter of a <see cref="DirectXRootLayout"/> binds.</summary>
public enum DirectXRootParameterKind {
    /// <summary>A descriptor table of one group's constant buffers, shader resource views and unordered access
    /// views.</summary>
    ViewTable,
    /// <summary>A descriptor table of one group's samplers.</summary>
    SamplerTable,
    /// <summary>The pushed index: one 32-bit root constant at register <c>b0</c> in space
    /// <see cref="GpuPipelineLayoutDescription.PushIndexSpace"/>.</summary>
    PushIndex,
}
/// <summary>One descriptor range of a root-signature table, in the fields of <c>D3D12_DESCRIPTOR_RANGE</c>.</summary>
/// <param name="Type">The range type: CBV, SRV, UAV or sampler.</param>
/// <param name="Count">The number of descriptors, which is also the number of registers the range takes.</param>
/// <param name="BaseRegister">The first shader register, equal to the binding number.</param>
/// <param name="Space">The register space, equal to the group's ordinal.</param>
/// <param name="TableOffset">The range's first descriptor, counted from the start of its table.</param>
public readonly record struct DirectXDescriptorRange(
    D3D12_DESCRIPTOR_RANGE_TYPE Type,
    uint Count,
    uint BaseRegister,
    uint Space,
    uint TableOffset
);
/// <summary>One root parameter of a <see cref="DirectXRootLayout"/>, visible to every stage.</summary>
/// <param name="Index">The root parameter index.</param>
/// <param name="Kind">What the parameter binds.</param>
/// <param name="Space">The group's ordinal for a table, or <see cref="GpuPipelineLayoutDescription.PushIndexSpace"/>
/// for the pushed index.</param>
/// <param name="Ranges">A table's ranges in binding order; empty for the pushed index.</param>
public sealed record DirectXRootParameter(
    uint Index,
    DirectXRootParameterKind Kind,
    uint Space,
    IReadOnlyList<DirectXDescriptorRange> Ranges
) {
    /// <summary>Gets the number of descriptors a table holds, or zero for the pushed index.</summary>
    public uint DescriptorCount =>
        ((uint)Ranges.Sum(selector: static range => range.Count));
}
/// <summary>
/// A Direct3D 12 root signature planned from a <see cref="GpuPipelineLayoutDescription"/>, with no device call.
/// <para>Root parameter indices are dense. Groups come in ordinal order, and each takes a view table for its
/// constant buffers, shader resource views and unordered access views, then a sampler table when it holds a sampler,
/// because a descriptor table cannot mix samplers with other views. The pushed index comes last. Each binding is one
/// range at register space equal to its group's ordinal and base register equal to its binding number, placed at the
/// next free descriptor of its table in binding order, so no register is remapped.</para>
/// </summary>
public sealed class DirectXRootLayout {
    private DirectXRootLayout(IReadOnlyList<DirectXRootParameter> parameters) {
        Parameters = parameters;
    }

    /// <summary>Gets the root parameters in index order.</summary>
    public IReadOnlyList<DirectXRootParameter> Parameters { get; }
    /// <summary>Gets the pushed index's root parameter, or <see langword="null"/> when the pipeline pushes none.</summary>
    public DirectXRootParameter? PushIndex =>
        Parameters.FirstOrDefault(predicate: static parameter => (parameter.Kind == DirectXRootParameterKind.PushIndex));

    private static D3D12_DESCRIPTOR_RANGE_TYPE RangeType(GpuBindingKind kind) =>
        kind switch {
            GpuBindingKind.ConstantBuffer => D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_CBV,
            GpuBindingKind.ReadOnlyBuffer or GpuBindingKind.SampledImage => D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
            GpuBindingKind.ReadWriteBuffer or GpuBindingKind.StorageImage => D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_UAV,
            GpuBindingKind.Sampler => D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SAMPLER,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: kind,
                message: "The value is not a binding kind.",
                paramName: nameof(kind)
            ),
        };
    private static IReadOnlyList<DirectXDescriptorRange> Ranges(GpuGroupLayoutDescription group, bool samplers) {
        var ranges = new List<DirectXDescriptorRange>();
        var offset = 0u;

        foreach (var binding in group.Bindings) {
            if ((binding.Kind == GpuBindingKind.Sampler) != samplers) {
                continue;
            }

            ranges.Add(item: new DirectXDescriptorRange(
                BaseRegister: binding.Binding,
                Count: binding.Count,
                Space: group.Ordinal,
                TableOffset: offset,
                Type: RangeType(kind: binding.Kind)
            ));
            offset = checked((offset + binding.Count));
        }

        return ranges.AsReadOnly();
    }

    /// <summary>Plans the root signature a pipeline layout needs.</summary>
    /// <param name="description">The neutral pipeline layout.</param>
    /// <returns>The planned root layout.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    public static DirectXRootLayout Plan(GpuPipelineLayoutDescription description) {
        ArgumentNullException.ThrowIfNull(argument: description);

        var parameters = new List<DirectXRootParameter>();

        foreach (var group in description.Groups) {
            if (group.HoldsViews) {
                parameters.Add(item: new DirectXRootParameter(
                    Index: ((uint)parameters.Count),
                    Kind: DirectXRootParameterKind.ViewTable,
                    Ranges: Ranges(
                        group: group,
                        samplers: false
                    ),
                    Space: group.Ordinal
                ));
            }
            if (group.HoldsSamplers) {
                parameters.Add(item: new DirectXRootParameter(
                    Index: ((uint)parameters.Count),
                    Kind: DirectXRootParameterKind.SamplerTable,
                    Ranges: Ranges(
                        group: group,
                        samplers: true
                    ),
                    Space: group.Ordinal
                ));
            }
        }

        if (description.PushesIndex) {
            parameters.Add(item: new DirectXRootParameter(
                Index: ((uint)parameters.Count),
                Kind: DirectXRootParameterKind.PushIndex,
                Ranges: [],
                Space: GpuPipelineLayoutDescription.PushIndexSpace
            ));
        }

        return new DirectXRootLayout(parameters: new ReadOnlyCollection<DirectXRootParameter>(list: parameters));
    }
    /// <summary>Returns the root parameter of one group's table.</summary>
    /// <param name="ordinal">The group's ordinal.</param>
    /// <param name="kind">The table: <see cref="DirectXRootParameterKind.ViewTable"/> or
    /// <see cref="DirectXRootParameterKind.SamplerTable"/>.</param>
    /// <returns>The table's root parameter, or <see langword="null"/> when the layout has no such table.</returns>
    public DirectXRootParameter? TableOf(uint ordinal, DirectXRootParameterKind kind) =>
        Parameters.FirstOrDefault(predicate: parameter => (
            (parameter.Kind == kind) &&
            (parameter.Space == ordinal)
        ));
}
