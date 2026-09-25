using System.Collections.Concurrent;

namespace Puck.Vulkan;

/// <summary>
/// The group each descriptor set of one logical device belongs to, which a <c>VkDescriptorSet</c> handle cannot carry
/// itself. A pipeline created from a <see cref="GpuPipelineLayoutDescription"/> records its group set layouts here, each
/// under its set number, for as long as it lives; <see cref="VulkanGpuBindings.AllocateSet"/> records a set allocated
/// against one of them under that group and its pool, and <see cref="VulkanGpuBindings.DestroyPool"/> forgets the pool's
/// sets. <see cref="VulkanGpuRecorder.BindDescriptorSet"/> reads a set's group to refuse a bind at any other. A set of
/// any other layout is never recorded and belongs to group 0. Safe to use from several threads: pipelines are created
/// on the thread pool while frames record.
/// </summary>
public sealed class VulkanDescriptorSetGroups {
    private readonly ConcurrentDictionary<nint, uint> m_layouts = new();
    private readonly ConcurrentDictionary<nint, List<nint>> m_poolSets = new();
    private readonly ConcurrentDictionary<nint, uint> m_sets = new();

    /// <summary>Gets the group sets recorded now, across every pool.</summary>
    public int LiveSets => m_sets.Count;

    /// <summary>Records a pipeline's group set layouts, each under its set number.</summary>
    /// <param name="setLayoutHandles">The native <c>VkDescriptorSetLayout</c> handle of each set, indexed by set
    /// number.</param>
    /// <exception cref="ArgumentNullException"><paramref name="setLayoutHandles"/> is <see langword="null"/>.</exception>
    public void AddLayouts(IReadOnlyList<nint> setLayoutHandles) {
        ArgumentNullException.ThrowIfNull(argument: setLayoutHandles);

        for (var set = 0; (set < setLayoutHandles.Count); set++) {
            if (0 != setLayoutHandles[set]) {
                m_layouts[setLayoutHandles[set]] = ((uint)set);
            }
        }
    }
    /// <summary>Records a set just allocated from a pool, under its layout's group when the layout is a group's.</summary>
    /// <param name="poolHandle">The native <c>VkDescriptorPool</c> handle the set was allocated from.</param>
    /// <param name="setLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle it was allocated against.</param>
    /// <param name="setHandle">The native <c>VkDescriptorSet</c> handle.</param>
    public void AddSet(nint poolHandle, nint setLayoutHandle, nint setHandle) {
        if (!m_layouts.TryGetValue(
            key: setLayoutHandle,
            value: out var group
        )) {
            return;
        }

        var sets = m_poolSets.GetOrAdd(
            key: poolHandle,
            valueFactory: static _ => []
        );

        lock (sets) {
            sets.Add(item: setHandle);
        }

        m_sets[setHandle] = group;
    }
    /// <summary>Returns the group a set belongs to: the group of the layout it was allocated against, or 0.</summary>
    /// <param name="setHandle">The native <c>VkDescriptorSet</c> handle.</param>
    /// <returns>The set's group.</returns>
    public uint GroupOf(nint setHandle) => (m_sets.TryGetValue(
        key: setHandle,
        value: out var group
    )
        ? group
        : 0U);
    /// <summary>Forgets a pipeline's group set layouts, before they are destroyed.</summary>
    /// <param name="setLayoutHandles">The handles <see cref="AddLayouts"/> recorded.</param>
    /// <exception cref="ArgumentNullException"><paramref name="setLayoutHandles"/> is <see langword="null"/>.</exception>
    public void RemoveLayouts(IReadOnlyList<nint> setLayoutHandles) {
        ArgumentNullException.ThrowIfNull(argument: setLayoutHandles);

        foreach (var handle in setLayoutHandles) {
            _ = m_layouts.TryRemove(
                key: handle,
                value: out _
            );
        }
    }
    /// <summary>Forgets every set allocated from a pool, which its destruction frees.</summary>
    /// <param name="poolHandle">The native <c>VkDescriptorPool</c> handle.</param>
    public void RemovePool(nint poolHandle) {
        if (!m_poolSets.TryRemove(
            key: poolHandle,
            value: out var sets
        )) {
            return;
        }

        lock (sets) {
            foreach (var set in sets) {
                _ = m_sets.TryRemove(
                    key: set,
                    value: out _
                );
            }
        }
    }
}
