using System.Collections.ObjectModel;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Everything a pipeline binds, graphics and compute alike: the frequency groups it uses and whether it pushes an
/// index. Each backend plans its own layout from this one description, and no backend numbers a register or a binding
/// of its own.
/// <para>A pushed index is one 4-byte value, visible to every stage: Vulkan push constants at offset 0, and Direct3D
/// 12 root constants at register <c>b0</c> in space <see cref="PushIndexSpace"/>, outside every group's space. Nothing
/// else is pushed.</para>
/// </summary>
public sealed class GpuPipelineLayoutDescription {
    /// <summary>The number of frequency groups: frame, world, pipeline instance and pass, whose ordinals are 0 through
    /// 3.</summary>
    public const uint GroupCount = 4;
    /// <summary>The size in bytes of the pushed index.</summary>
    public const uint PushIndexBytes = 4;
    /// <summary>The Direct3D 12 constant-buffer register of the pushed index, <c>b0</c>, in space
    /// <see cref="PushIndexSpace"/>.</summary>
    public const uint PushIndexRegister = 0;
    /// <summary>The Direct3D 12 register space of the pushed index, which sits at register <c>b0</c>.</summary>
    public const uint PushIndexSpace = GroupCount;

    /// <summary>Initializes a new instance of the <see cref="GpuPipelineLayoutDescription"/> class.</summary>
    /// <param name="groups">The groups the pipeline binds, in any order; none for a pipeline that binds nothing.</param>
    /// <param name="pushesIndex">Whether the pipeline pushes a 4-byte index.</param>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> or one of its groups is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Two groups share an ordinal.</exception>
    public GpuPipelineLayoutDescription(IReadOnlyList<GpuGroupLayoutDescription> groups, bool pushesIndex) {
        ArgumentNullException.ThrowIfNull(argument: groups);

        var sorted = new GpuGroupLayoutDescription[groups.Count];

        for (var index = 0; (index < groups.Count); index++) {
            sorted[index] = (groups[index] ?? throw new ArgumentNullException(
                message: $"Group entry {index} is null.",
                paramName: nameof(groups)
            ));
        }

        Array.Sort(
            array: sorted,
            comparison: static (left, right) => left.Ordinal.CompareTo(value: right.Ordinal)
        );

        for (var index = 1; (index < sorted.Length); index++) {
            if (sorted[index].Ordinal == sorted[(index - 1)].Ordinal) {
                throw new ArgumentException(
                    message: $"Group {sorted[index].Ordinal} is declared twice.",
                    paramName: nameof(groups)
                );
            }
        }

        Groups = new ReadOnlyCollection<GpuGroupLayoutDescription>(list: sorted);
        PushesIndex = pushesIndex;
    }

    /// <summary>Gets the groups in ordinal order.</summary>
    public IReadOnlyList<GpuGroupLayoutDescription> Groups { get; }
    /// <summary>Gets whether the pipeline pushes a 4-byte index.</summary>
    public bool PushesIndex { get; }
}
