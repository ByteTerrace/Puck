namespace Puck.Forge.Authoring;

/// <summary>One compiled authored-part mapping.</summary>
/// <param name="PartId">The ordinal, case-sensitive authored identifier.</param>
/// <param name="TransformSlot">The transform slot relative to the compiling look's own slot range.</param>
public readonly record struct AuthoredPartSlot(string PartId, int TransformSlot);
/// <summary>An immutable authored <c>PartId</c>-to-transform-slot table published by a compiled look.</summary>
public sealed class AuthoredPartTable {
    private readonly AuthoredPartSlot[] m_slots;

    /// <summary>Initializes a table from authored mappings in stable compilation order.</summary>
    /// <param name="slots">The unique mappings.</param>
    public AuthoredPartTable(IEnumerable<AuthoredPartSlot> slots) {
        ArgumentNullException.ThrowIfNull(slots);

        m_slots = slots.ToArray();
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var slot in m_slots) {
            ArgumentException.ThrowIfNullOrWhiteSpace(argument: slot.PartId, paramName: nameof(slots));
            ArgumentOutOfRangeException.ThrowIfNegative(value: slot.TransformSlot, paramName: nameof(slots));

            if (!ids.Add(item: slot.PartId)) {
                throw new ArgumentException(message: $"Part id '{slot.PartId}' is duplicated.", paramName: nameof(slots));
            }
        }
    }

    /// <summary>The mappings in stable compilation order.</summary>
    public IReadOnlyList<AuthoredPartSlot> Slots => m_slots;

    /// <summary>Resolves an authored part identifier to its relative transform slot.</summary>
    /// <param name="partId">The ordinal, case-sensitive authored identifier.</param>
    /// <param name="transformSlot">The relative transform slot, or -1 when unresolved.</param>
    /// <returns><see langword="true"/> when the look publishes <paramref name="partId"/>.</returns>
    public bool TryResolve(string partId, out int transformSlot) {
        foreach (var slot in m_slots) {
            if (string.Equals(a: slot.PartId, b: partId, comparisonType: StringComparison.Ordinal)) {
                transformSlot = slot.TransformSlot;

                return true;
            }
        }

        transformSlot = -1;

        return false;
    }
}
/// <summary>Compiles a creation's authored part declarations against its stable shape-slot order.</summary>
public static class CreationPartCompiler {
    /// <summary>Publishes each part id as the zero-based dynamic shape slot its referenced shape occupies.</summary>
    /// <param name="document">A validated creation document.</param>
    /// <returns>The creation's immutable authored-part table.</returns>
    public static AuthoredPartTable Compile(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var shapeSlots = new Dictionary<int, int>();
        var shapes = (document.Shapes ?? []);

        for (var index = 0; (index < shapes.Count); index++) {
            shapeSlots.Add(key: shapes[index].Id, value: index);
        }

        var slots = new List<AuthoredPartSlot>(capacity: (document.Parts?.Count ?? 0));

        foreach (var part in (document.Parts ?? [])) {
            if (!shapeSlots.TryGetValue(key: part.ShapeId, value: out var shapeSlot)) {
                throw new ArgumentException(message: $"Part '{part.Id}' references missing shape id {part.ShapeId}.", paramName: nameof(document));
            }

            slots.Add(item: new AuthoredPartSlot(PartId: part.Id, TransformSlot: shapeSlot));
        }

        return new AuthoredPartTable(slots: slots);
    }
}
