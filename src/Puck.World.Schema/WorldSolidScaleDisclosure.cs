using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Filters sampled root sizes through the live disclosure of their source cells.</summary>
public sealed class WorldSolidScaleDisclosure {
    private IReadOnlyList<WorldPlacement>? m_placements;
    private IReadOnlyList<WorldPrototype>? m_creations;

    private readonly Dictionary<string, (StateBinding? Binding, string? Key)> m_bindings = new(comparer: StringComparer.Ordinal);
    private WorldSolidScale[] m_scratch = [];

    /// <summary>Returns only samples whose source cells the recipient may read. Scratch belongs to this delivery.</summary>
    public ReadOnlyMemory<WorldSolidScale> Filter(WorldRecipientView view, ReadOnlyMemory<WorldSolidScale> scales) {
        var definition = view.Source;

        if (!ReferenceEquals(objA: m_placements, objB: definition.Placements) || !ReferenceEquals(objA: m_creations, objB: definition.Creations)) {
            m_bindings.Clear();
            foreach (var placement in definition.Placements) {
                if (placement.Solid is null) { continue; }
                var creation = WorldDefinitionRows.FindCreation(creations: definition.Creations, id: placement.ShownPrototypeId);

                if (StateBinding.Parse(token: creation?.Document.SizeBy) is not { } binding) {
                    m_bindings.Add(key: placement.Id.ToString(), value: (null, null));
                    continue;
                }
                if (!StateBinding.TryResolveBodyKey(binding.Key, (placement.Attach?.BodyIndex ?? -1), out var key)) {
                    throw new InvalidOperationException(message: "An admitted solid size binding has no body.");
                }
                m_bindings.Add(key: placement.Id.ToString(), value: (binding, key));
            }
            m_placements = definition.Placements; m_creations = definition.Creations;
        }
        var span = scales.Span;

        if (m_scratch.Length < span.Length) { m_scratch = new WorldSolidScale[span.Length]; }
        var count = 0;

        foreach (ref readonly var scale in span) {
            if (m_bindings.TryGetValue(key: scale.Placement, value: out var source) && ((source.Binding is not { } binding) || view.Discloses(binding.Row, source.Key))) {
                m_scratch[count++] = scale;
            }
        }
        return ((count == span.Length) ? scales : m_scratch.AsMemory(length: count, start: 0));
    }
}
