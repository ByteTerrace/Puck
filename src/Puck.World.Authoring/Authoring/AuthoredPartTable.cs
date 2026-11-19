using System.Runtime.CompilerServices;

namespace Puck.World.Authoring;

/// <summary>One compiled authored-part mapping.</summary>
/// <param name="PartId">The ordinal, case-sensitive authored identifier.</param>
/// <param name="TransformSlot">The transform slot relative to the compiling look's own slot range.</param>
/// <param name="Shapes">The shape indices the part covers, ascending (<see cref="CreationPartDocument.Shapes"/>,
/// <see cref="CreationPartDocument.Except"/>), or <see langword="null"/> for a look that publishes only the slot.</param>
public readonly record struct AuthoredPartSlot(string PartId, int TransformSlot, IReadOnlyList<int>? Shapes = null);
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
            ArgumentException.ThrowIfNullOrWhiteSpace(
                argument: slot.PartId,
                paramName: nameof(slots)
            );
            ArgumentOutOfRangeException.ThrowIfNegative(
                value: slot.TransformSlot,
                paramName: nameof(slots)
            );

            if (!ids.Add(item: slot.PartId)) {
                throw new ArgumentException(
                    message: $"Part id '{slot.PartId}' is duplicated.",
                    paramName: nameof(slots)
                );
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
        if (TryFind(partId: partId, slot: out var slot)) {
            transformSlot = slot.TransformSlot;

            return true;
        }

        transformSlot = -1;

        return false;
    }
    /// <summary>Resolves an authored part identifier to the shapes it covers.</summary>
    /// <param name="partId">The ordinal, case-sensitive authored identifier.</param>
    /// <param name="shapes">The covered shape indices, ascending (the transform slot alone for a part compiled without a
    /// cover); empty when unresolved.</param>
    /// <returns><see langword="true"/> when the look publishes <paramref name="partId"/>.</returns>
    public bool TryCover(string partId, out IReadOnlyList<int> shapes) {
        if (TryFind(partId: partId, slot: out var slot)) {
            shapes = (slot.Shapes ?? [slot.TransformSlot]);

            return true;
        }

        shapes = [];

        return false;
    }

    private bool TryFind(string partId, out AuthoredPartSlot slot) {
        foreach (var candidate in m_slots) {
            if (string.Equals(
                a: candidate.PartId,
                b: partId,
                comparisonType: StringComparison.Ordinal
            )) {
                slot = candidate;

                return true;
            }
        }

        slot = default;

        return false;
    }
}
/// <summary>Compiles a creation's authored part declarations against its stable shape-slot order: each part's anchor
/// slot and the set of shapes it covers. This is the one place a part becomes shapes, so a mask that names a part means
/// one thing everywhere.</summary>
public static class CreationPartCompiler {
    private static readonly ConditionalWeakTable<CreationDocument, AuthoredPartTable> Compiled = new();

    /// <summary>Publishes each part id as the zero-based dynamic shape slot its referenced shape occupies, with the shapes
    /// it covers. Compiled once per document instance.</summary>
    /// <param name="document">A validated creation document.</param>
    /// <returns>The creation's immutable authored-part table.</returns>
    /// <exception cref="ArgumentException">A part is malformed (<see cref="Resolve"/> names why).</exception>
    public static AuthoredPartTable Compile(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return Compiled.GetValue(key: document, createValueCallback: static document => Resolve(document: document, refuse: static (path, message) => throw new ArgumentException(message: $"{path}: {message}", paramName: nameof(document))));
    }
    /// <summary>Resolves a creation's parts, reporting each malformed one and leaving it out: an empty or duplicated id,
    /// an anchor naming no shape id, a <see cref="CreationPartDocument.Shapes"/> entry naming no shape, and an
    /// <see cref="CreationPartDocument.Except"/> entry naming no part declared before this one.</summary>
    /// <param name="document">The creation, in either frame.</param>
    /// <param name="refuse">Called with each refusal's document path and message.</param>
    /// <returns>The table of the parts that resolved.</returns>
    public static AuthoredPartTable Resolve(CreationDocument document, Action<string, string> refuse) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(refuse);

        var shapes = (document.Shapes ?? []);
        var shapeSlots = new Dictionary<int, int>();

        for (var index = 0; (index < shapes.Count); index++) { _ = shapeSlots.TryAdd(key: shapes[index].Id, value: index); }

        var parents = new int[shapes.Count];

        CreationMotion.ResolveParents(parents: parents, shapes: shapes);

        var lookup = new CreationShapeLookup(shapes: shapes);
        var slots = new List<AuthoredPartSlot>(capacity: (document.Parts?.Count ?? 0));
        var covers = new Dictionary<string, IReadOnlyList<int>>(comparer: StringComparer.Ordinal);
        var parts = (document.Parts ?? []);
        var covered = new bool[shapes.Count];
        // Ids declared but refused: an except naming one is not refused again.
        var refused = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < parts.Count); index++) {
            var part = parts[index];
            var path = $"parts[{index}]";
            var whole = true;

            if (string.IsNullOrWhiteSpace(value: part.Id)) {
                refuse(arg1: $"{path}.id", arg2: "a part id is required.");
                whole = false;
            } else if (covers.ContainsKey(key: part.Id)) {
                refuse(arg1: $"{path}.id", arg2: $"duplicate part id '{part.Id}'.");
                whole = false;
            }
            if (!shapeSlots.TryGetValue(key: part.ShapeId, value: out var anchor)) {
                refuse(arg1: $"{path}.shapeId", arg2: $"references missing shape id {part.ShapeId}.");
                whole = false;
            }

            Array.Clear(array: covered);
            if (part.Shapes is { } named) {
                for (var entry = 0; (entry < named.Count); entry++) {
                    var shape = lookup.Find(name: named[entry]);

                    if (shape < 0) {
                        refuse(arg1: $"{path}.shapes[{entry}]", arg2: $"names no shape '{named[entry]}'.");
                        whole = false;
                    } else {
                        covered[shape] = true;
                    }
                }
            } else if (whole) {
                covered[anchor] = true;
            }
            // A parent is declared before the shapes it carries, so one pass in order carries a cover down every subtree.
            for (var shape = 0; (shape < shapes.Count); shape++) {
                if ((parents[shape] >= 0) && covered[parents[shape]]) { covered[shape] = true; }
            }
            if (part.Except is { } except) {
                for (var entry = 0; (entry < except.Count); entry++) {
                    if ((except[entry] is not { } earlier) || !covers.TryGetValue(key: earlier, value: out var removed)) {
                        if ((except[entry] is { } refusedId) && refused.Contains(item: refusedId)) {
                            whole = false;

                            continue;
                        }
                        refuse(arg1: $"{path}.except[{entry}]", arg2: $"names no part '{except[entry]}' declared before this one; a part removes only earlier parts.");
                        whole = false;

                        continue;
                    }
                    foreach (var shape in removed) { covered[shape] = false; }
                }
            }
            if (!whole) {
                if (!string.IsNullOrWhiteSpace(value: part.Id) && !covers.ContainsKey(key: part.Id)) { _ = refused.Add(item: part.Id); }

                continue;
            }

            var cover = new List<int>();

            for (var shape = 0; (shape < shapes.Count); shape++) {
                if (covered[shape]) { cover.Add(item: shape); }
            }
            covers.Add(key: part.Id, value: cover);
            slots.Add(item: new AuthoredPartSlot(
                PartId: part.Id,
                Shapes: cover,
                TransformSlot: anchor
            ));
        }

        return new AuthoredPartTable(slots: slots);
    }
}
