using System.Collections;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

public sealed partial class WorldDocumentWalker {
    // Every member is walked: a share reads the whole value, not one consumer's surface.
    private static readonly WorldDocumentWalker Everything = new(isSurface: static _ => true);

    /// <summary>Returns <paramref name="value"/> with every member and keyed row that <paramref name="patch"/> leaves
    /// unchanged replaced by <paramref name="basis"/>'s own instance, so a reference-keyed product derived from the basis
    /// stays valid for whatever the patch did not touch. The values on the paths to a change are copied as
    /// <see cref="Rewrite{T}"/> copies them; everything off those paths is the basis's.</summary>
    /// <remarks>The trees are the canonical JSON of the two values (<c>WorldDefinitionSerialization</c>) and the patch is
    /// <see cref="WorldDocumentBasis.Diff"/> of them, with <paramref name="value"/> deserialized from the patch's merge.
    /// Sharing is decided from the patch alone and never compares typed values, so the caller proves the result's
    /// canonical bytes equal the target's. A serializer-described object shares member by member and a keyed list row by
    /// row; any other value the patch touches (a converter-backed object, a dictionary, a reordered or unkeyed list, a
    /// replaced row) is kept whole from <paramref name="value"/>.</remarks>
    /// <typeparam name="T">The root's type.</typeparam>
    /// <param name="value">The value deserialized from the merged tree.</param>
    /// <param name="basis">The value the patch was diffed against.</param>
    /// <param name="valueTree">The merged tree.</param>
    /// <param name="basisTree">The basis's tree.</param>
    /// <param name="patch">The patch from the basis's tree to the merged tree.</param>
    /// <returns>The shared value, or <paramref name="value"/> itself when nothing could be shared.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">A value on a path to a change cannot be copied; the message names its
    /// document path.</exception>
    public static T Share<T>(T value, T basis, JsonNode valueTree, JsonNode basisTree, JsonNode patch) where T : class {
        ArgumentNullException.ThrowIfNull(argument: value);
        ArgumentNullException.ThrowIfNull(argument: basis);
        ArgumentNullException.ThrowIfNull(argument: valueTree);
        ArgumentNullException.ThrowIfNull(argument: basisTree);
        ArgumentNullException.ThrowIfNull(argument: patch);

        return (T)new Cursor(
            rewriter: null!,
            tables: Everything.CurrentTables(),
            visitor: null!
        ).Share(
            basis: basis,
            basisTree: basisTree,
            patch: patch,
            value: value,
            valueTree: valueTree
        )!;
    }

    internal sealed partial class Cursor {
        // The value a site holds once shared: the basis's own where the patch names nothing below it, a copy of the
        // value with its unchanged members and rows shared where it names something, and the value itself otherwise.
        // An object shares only with one of its own type (a polymorphic arm the patch replaced keeps the value's); a
        // list shares rows with a basis list of any collection type, since an install may hold its rows in another
        // collection than the serializer reads one into.
        public object? Share(object? value, object? basis, JsonNode? valueTree, JsonNode? basisTree, JsonNode? patch) {
            if (
                (value is null) ||
                (basis is null) ||
                (value is string) ||
                value.GetType().IsValueType ||
                (tables.ShapeOf(type: value.GetType()) is not { } shape)
            ) {
                return value;
            }

            switch (shape.Kind) {
                case JsonTypeInfoKind.Object when shape.Described && (value.GetType() == basis.GetType()) && (patch is JsonObject delta) && !WorldDocumentBasis.IsReplaceMarker(node: delta) &&
                    (valueTree is JsonObject valueObject) && (basisTree is JsonObject basisObject):
                    return ShareMembers(basis: basis, basisObject: basisObject, delta: delta, shape: shape, value: value, valueObject: valueObject);
                case JsonTypeInfoKind.Enumerable when (patch is JsonArray rows) && ((rows.Count == 0) || !WorldDocumentBasis.IsReplaceMarker(node: rows[0])) &&
                    (valueTree is JsonArray valueRows) && (basisTree is JsonArray basisRows) && (value is IEnumerable items) && (basis is IEnumerable basisItems):
                    return ShareRows(basisItems: basisItems, basisRows: basisRows, rows: rows, value: value, items: items, valueRows: valueRows);
                default:
                    return value;
            }
        }

        private object ShareMembers(object value, object basis, JsonObject valueObject, JsonObject basisObject, JsonObject delta, WorldModelType shape) {
            object? copy = null;

            foreach (var member in tables.MembersOf(shape: shape)) {
                var held = member.Read(arg: value);
                var basisHeld = member.Read(arg: basis);

                m_path.Add(item: new Segment(Index: 0, Key: null, Name: member.Name));
                var next = (delta.TryGetPropertyValue(propertyName: member.Name, jsonNode: out var changed)
                    ? Share(
                        basis: basisHeld,
                        basisTree: basisObject[propertyName: member.Name],
                        patch: changed,
                        value: held,
                        valueTree: valueObject[propertyName: member.Name]
                    )
                    : basisHeld);

                if (!ReferenceEquals(objA: held, objB: next)) {
                    copy ??= Copy(value: value);
                    Set(copy: copy, member: member, value: next);
                }
                m_path.RemoveAt(index: (m_path.Count - 1));
            }

            return (copy ?? value);
        }
        // A keyed list's merge keeps the basis's surviving rows and appends new ones; a row the patch names no change for
        // is the basis's row of the same key, and a refined row shares what its own delta leaves alone.
        private object ShareRows(object value, IEnumerable items, IEnumerable basisItems, JsonArray valueRows, JsonArray basisRows, JsonArray rows) {
            var held = items.Cast<object?>().ToList();
            var basisHeld = basisItems.Cast<object?>().ToList();

            if (
                (held.Count != valueRows.Count) ||
                (basisHeld.Count != basisRows.Count) ||
                !WorldDocumentBasis.TryFindRowKey(ambiguity: out _, basis: basisRows, key: out var key, overlay: valueRows)
            ) {
                return value;
            }

            var basisIndex = WorldDocumentBasis.IndexRows(list: basisRows, key: key, duplicate: out _);
            var patchIndex = WorldDocumentBasis.IndexRows(list: rows, key: key, duplicate: out _);
            List<object?>? shared = null;

            for (var index = 0; (index < held.Count); index++) {
                var identity = WorldDocumentBasis.RowIdentity(key: key, row: valueRows[index]);
                var next = held[index];

                if ((identity is not null) && basisIndex.TryGetValue(key: identity, value: out var basisAt)) {
                    if (!patchIndex.TryGetValue(key: identity, value: out var patchAt)) {
                        next = basisHeld[basisAt];
                    } else {
                        m_path.Add(item: new Segment(Index: index, Key: null, Name: null));
                        next = Share(
                            basis: basisHeld[basisAt],
                            basisTree: basisRows[basisAt],
                            patch: rows[patchAt],
                            value: held[index],
                            valueTree: valueRows[index]
                        );
                        m_path.RemoveAt(index: (m_path.Count - 1));
                    }
                }
                if ((shared is null) && !ReferenceEquals(objA: next, objB: held[index])) {
                    shared = held.GetRange(index: 0, count: index);
                }
                shared?.Add(item: next);
            }

            return ((shared is null)
                ? value
                : Rebuild(
                    dictionary: false,
                    keys: [],
                    original: value,
                    values: shared
                ));
        }
    }
}
