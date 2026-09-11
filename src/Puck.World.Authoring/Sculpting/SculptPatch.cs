using System.Text.Json.Nodes;

namespace Puck.World.Authoring.Sculpting;

/// <summary>The four operations a <see cref="SculptPatch"/> carries.</summary>
public enum SculptPatchOpKind {
    /// <summary>Upserts one row into a keyed array section.</summary>
    UpsertRow,
    /// <summary>Removes one row from a keyed array section.</summary>
    RemoveRow,
    /// <summary>Sets one member, creating intermediate objects/array elements as needed.</summary>
    SetMember,
    /// <summary>Removes one member.</summary>
    RemoveMember,
}
/// <summary>One applied operation's outcome — what a caller echoes per op, and what
/// <see cref="SculptPatchTouchedRow"/> grouping keys off.</summary>
/// <param name="Kind">The operation kind.</param>
/// <param name="Section">The row-array (or keyless section) path the operation ultimately touched — for
/// <see cref="SculptPatchOpKind.UpsertRow"/>/<see cref="SculptPatchOpKind.RemoveRow"/> this is the authored
/// <c>sectionPath</c> verbatim; for <see cref="SculptPatchOpKind.SetMember"/>/<see cref="SculptPatchOpKind.RemoveMember"/>
/// it is the dotted path up to and including the last <c>[field=value]</c>-selected segment, or the first segment
/// alone when the path carries no selector (a keyless whole-section member).</param>
/// <param name="KeyField">The row's key field name, or null for a keyless section.</param>
/// <param name="KeyValue">The row's key value, or null for a keyless section.</param>
/// <param name="Path">The full authored path, for display.</param>
/// <param name="Verdict"><c>inserted</c>, <c>replaced</c>, <c>removed</c>, <c>set</c>, or <c>unset</c> (a
/// remove/unset of something already absent).</param>
public sealed record SculptPatchResult(SculptPatchOpKind Kind, string Section, string? KeyField, string? KeyValue, string Path, string Verdict);
/// <summary>One distinct row a patch's operations touched, in first-touched order — the grouping
/// <see cref="SculptPatch.TouchedRows"/> derives from <see cref="SculptPatch.Apply"/>'s results, for a caller (the
/// in-engine console verb) that must submit ONE upsert per row however many member-level edits landed on it.</summary>
/// <param name="Section">See <see cref="SculptPatchResult.Section"/>.</param>
/// <param name="KeyField">See <see cref="SculptPatchResult.KeyField"/>.</param>
/// <param name="KeyValue">See <see cref="SculptPatchResult.KeyValue"/>.</param>
/// <param name="Removed">Whether the row's last-applied operation was a <see cref="SculptPatchOpKind.RemoveRow"/>
/// that actually removed it (so the caller should <c>world.row.remove</c> rather than re-upsert a row no longer in
/// the document). A <see cref="SculptPatchOpKind.RemoveMember"/> is a modification of the row it descends into —
/// the row stays, one member fewer — and never marks it removed; a caller re-upserts it from the patched tree exactly
/// as it would after a <see cref="SculptPatchOpKind.SetMember"/>.</param>
public sealed record SculptPatchTouchedRow(string Section, string? KeyField, string? KeyValue, bool Removed);

/// <summary>
/// An ordered list of operations over a world document held as a raw <see cref="JsonNode"/> tree
/// (<see cref="JsonObject"/>/<see cref="JsonArray"/>) — the primitive a <see cref="ICreationSculpt"/> composes to
/// upsert generated sections into an existing world document without disturbing hand-authored ones. A row is
/// addressed by an id/name FIELD, never an array index: <see cref="UpsertRow"/> replaces an existing row sharing the
/// key in place (preserving its position) or appends a new one; every other member of the document, and every other
/// row of the same array, is left byte-identical. <see cref="SetMember"/>/<see cref="RemoveMember"/> take a dotted
/// path whose segments may carry ONE <c>[field=value]</c> selector each (e.g.
/// <c>looks.rows[name=moth].motion.poses</c>) to descend through a keyed array without naming an index.
/// <para>
/// This type understands JSON only — no section is special-cased, and it never parses a
/// <c>Puck.World.Schema.WorldDefinition</c> (that project cannot be referenced from here; see the remarks on
/// <see cref="CreationBuilder"/>). A caller that must install the patched document as a
/// <c>Puck.World.Schema.WorldDefinition</c> — the offline <c>puck creation sculpt</c> verb, or the in-engine
/// <c>creation.sculpt</c> console verb — serializes rows through <c>WorldDefinitionSerialization</c>/
/// <c>WorldJsonContext</c> so casing and polymorphic <c>$type</c> spellings match the document exactly, then
/// re-parses the patched tree through that same serializer and validates it with
/// <c>WorldDefinitionValidator.TryValidateLocally</c> before trusting it.
/// </para>
/// </summary>
public sealed class SculptPatch {
    private readonly List<Op> m_ops = [];

    private abstract record Op(SculptPatchOpKind Kind);
    private sealed record UpsertRowOp(string SectionPath, string KeyField, string KeyValue, JsonNode Row) : Op(SculptPatchOpKind.UpsertRow);
    private sealed record RemoveRowOp(string SectionPath, string KeyField, string KeyValue) : Op(SculptPatchOpKind.RemoveRow);
    private sealed record SetMemberOp(string Path, JsonNode? Value) : Op(SculptPatchOpKind.SetMember);
    private sealed record RemoveMemberOp(string Path) : Op(SculptPatchOpKind.RemoveMember);

    /// <summary>Queues an upsert of one row into the keyed array at <paramref name="sectionPath"/> (a dotted member
    /// path to the array itself, e.g. <c>state.world</c>, <c>views.layouts</c>), keyed by <paramref name="keyField"/>.
    /// Applying replaces an existing row sharing the key IN PLACE, or appends when none matches.</summary>
    /// <param name="sectionPath">The dotted path to the array, from the document root.</param>
    /// <param name="keyField">The row's key field name (an id/name member — never an array index).</param>
    /// <param name="keyValue">The row's key value.</param>
    /// <param name="row">The complete row to upsert.</param>
    public SculptPatch UpsertRow(string sectionPath, string keyField, string keyValue, JsonNode row) {
        m_ops.Add(item: new UpsertRowOp(
            KeyField: keyField,
            KeyValue: keyValue,
            Row: row,
            SectionPath: sectionPath
        ));

        return this;
    }
    /// <summary>Queues removal of one row from the keyed array at <paramref name="sectionPath"/>. A no-op (verdict
    /// <c>unset</c>) when no row carries the key.</summary>
    public SculptPatch RemoveRow(string sectionPath, string keyField, string keyValue) {
        m_ops.Add(item: new RemoveRowOp(
            KeyField: keyField,
            KeyValue: keyValue,
            SectionPath: sectionPath
        ));

        return this;
    }
    /// <summary>Queues setting one member at <paramref name="path"/>, creating intermediate objects (and, for a
    /// selected segment, the array element) as needed.</summary>
    /// <param name="path">The dotted path; a segment may carry one <c>[field=value]</c> selector.</param>
    /// <param name="value">The value to set.</param>
    public SculptPatch SetMember(string path, JsonNode? value) {
        m_ops.Add(item: new SetMemberOp(
            Path: path,
            Value: value
        ));

        return this;
    }
    /// <summary>Queues removing one member at <paramref name="path"/>. A no-op (verdict <c>unset</c>) when the
    /// member, or an ancestor it descends through, is already absent.</summary>
    public SculptPatch RemoveMember(string path) {
        m_ops.Add(item: new RemoveMemberOp(Path: path));

        return this;
    }

    /// <summary>Applies every queued operation, in order, directly to <paramref name="document"/> — every untouched
    /// member, and the file's key order (an upsert replaces in place, an append lands at the array's end), is
    /// preserved.</summary>
    /// <param name="document">The document tree to mutate.</param>
    /// <returns>One result per queued operation, in order.</returns>
    public IReadOnlyList<SculptPatchResult> Apply(JsonObject document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var results = new List<SculptPatchResult>(capacity: m_ops.Count);

        foreach (var op in m_ops) {
            results.Add(item: op switch {
                UpsertRowOp upsert => ApplyUpsertRow(
                    document: document,
                    op: upsert
                ),
                RemoveRowOp remove => ApplyRemoveRow(
                    document: document,
                    op: remove
                ),
                SetMemberOp set => ApplySetMember(
                    document: document,
                    op: set
                ),
                RemoveMemberOp unset => ApplyRemoveMember(
                    document: document,
                    op: unset
                ),
                _ => throw new NotSupportedException(message: op.GetType().Name),
            });
        }

        return results;
    }
    /// <summary>Derives the distinct rows <paramref name="results"/> touched, in first-touched order — see
    /// <see cref="SculptPatchTouchedRow"/>.</summary>
    public static IReadOnlyList<SculptPatchTouchedRow> TouchedRows(IReadOnlyList<SculptPatchResult> results) {
        var order = new List<(string Section, string? KeyField, string? KeyValue)>();
        var removedByRow = new Dictionary<(string, string?, string?), bool>();

        foreach (var result in results) {
            var key = (result.Section, result.KeyField, result.KeyValue);

            if (!removedByRow.ContainsKey(key: key)) {
                order.Add(item: key);
            }

            // Only a RemoveRow that found its row retires it; a RemoveMember edits the row it descends into (the row
            // itself is still there), and an UpsertRow/SetMember after a RemoveRow brings the row back.
            removedByRow[key] = ((result.Kind is SculptPatchOpKind.RemoveRow) && (result.Verdict == "removed"));
        }

        return [.. order.Select(selector: key => new SculptPatchTouchedRow(
            KeyField: key.KeyField,
            KeyValue: key.KeyValue,
            Removed: removedByRow[key],
            Section: key.Section
        ))];
    }

    private static SculptPatchResult ApplyUpsertRow(JsonObject document, UpsertRowOp op) {
        var array = ResolveArray(
            create: true,
            document: document,
            path: op.SectionPath
        )!;
        var existingIndex = FindIndex(
            array: array,
            keyField: op.KeyField,
            keyValue: op.KeyValue
        );

        if (existingIndex >= 0) {
            array[existingIndex] = op.Row.DeepClone();
        } else {
            array.Add(value: op.Row.DeepClone());
        }

        return new SculptPatchResult(
            Kind: SculptPatchOpKind.UpsertRow,
            KeyField: op.KeyField,
            KeyValue: op.KeyValue,
            Path: $"{op.SectionPath}[{op.KeyField}={op.KeyValue}]",
            Section: op.SectionPath,
            Verdict: ((existingIndex >= 0) ? "replaced" : "inserted")
        );
    }
    private static SculptPatchResult ApplyRemoveRow(JsonObject document, RemoveRowOp op) {
        var array = ResolveArray(
            create: false,
            document: document,
            path: op.SectionPath
        );
        var existingIndex = ((array is not null)
            ? FindIndex(
                array: array,
                keyField: op.KeyField,
                keyValue: op.KeyValue
            )
            : (-1)
        );

        if (existingIndex >= 0) {
            array!.RemoveAt(index: existingIndex);
        }

        return new SculptPatchResult(
            Kind: SculptPatchOpKind.RemoveRow,
            KeyField: op.KeyField,
            KeyValue: op.KeyValue,
            Path: $"{op.SectionPath}[{op.KeyField}={op.KeyValue}]",
            Section: op.SectionPath,
            Verdict: ((existingIndex >= 0) ? "removed" : "unset")
        );
    }
    private static SculptPatchResult ApplySetMember(JsonObject document, SetMemberOp op) {
        var segments = PatchPath.Parse(path: op.Path);
        var parent = ResolveParent(
            create: true,
            document: document,
            segments: segments
        )!;
        var last = segments[^1];
        var existed = (parent.ContainsKey(propertyName: last.Name) && (parent[last.Name] is not null));

        parent[last.Name] = op.Value?.DeepClone();

        var (section, keyField, keyValue) = SectionOf(segments: segments);

        return new SculptPatchResult(
            Kind: SculptPatchOpKind.SetMember,
            KeyField: keyField,
            KeyValue: keyValue,
            Path: op.Path,
            Section: section,
            Verdict: (existed ? "replaced" : "set")
        );
    }
    private static SculptPatchResult ApplyRemoveMember(JsonObject document, RemoveMemberOp op) {
        var segments = PatchPath.Parse(path: op.Path);
        var parent = ResolveParent(
            create: false,
            document: document,
            segments: segments
        );
        var last = segments[^1];
        var removed = (parent?.Remove(propertyName: last.Name) ?? false);

        var (section, keyField, keyValue) = SectionOf(segments: segments);

        return new SculptPatchResult(
            Kind: SculptPatchOpKind.RemoveMember,
            KeyField: keyField,
            KeyValue: keyValue,
            Path: op.Path,
            Section: section,
            Verdict: (removed ? "removed" : "unset")
        );
    }

    // The Section a SetMember/RemoveMember touched, for TouchedRows grouping: the dotted path up to and including
    // the LAST selector-carrying segment, else the first segment alone (a keyless whole-section member).
    private static (string Section, string? KeyField, string? KeyValue) SectionOf(IReadOnlyList<PatchPathSegment> segments) {
        var lastSelected = -1;

        for (var index = 0; (index < segments.Count); index++) {
            if (segments[index].SelectorField is not null) {
                lastSelected = index;
            }
        }

        if (lastSelected < 0) {
            return (segments[0].Name, null, null);
        }

        var section = string.Join(separator: ".", values: segments.Take(count: (lastSelected + 1)).Select(selector: static segment => segment.Name));

        return (section, segments[lastSelected].SelectorField, segments[lastSelected].SelectorValue);
    }
    // create:true materializes every absent intermediate segment as an object and only the final segment as the array
    // itself (mirroring Descend for a selector-less path) — "state.world" on a document with no "state" member yields
    // {"state":{"world":[…]}}, never an array where the section object belongs.
    private static JsonArray? ResolveArray(JsonObject document, string path, bool create) {
        var names = path.Split(separator: '.');
        JsonNode current = document;

        for (var index = 0; (index < names.Length); index++) {
            var name = names[index];

            if (current is not JsonObject obj) {
                throw new InvalidOperationException(message: $"'{path}' descends through a non-object member.");
            }

            if (obj[name] is not { } next) {
                if (!create) {
                    return null;
                }

                next = ((index == (names.Length - 1)) ? new JsonArray() : new JsonObject());
                obj[name] = next;
            }

            current = next;
        }

        return (current as JsonArray) ?? throw new InvalidOperationException(message: $"'{path}' does not name an array.");
    }
    // create:true always returns a non-null parent (creating intermediates as needed) — used by SetMember.
    // create:false returns null the moment any intermediate is absent, rather than throwing — an ancestor missing
    // under RemoveMember means there is nothing to remove (verdict "unset"), not a caller error.
    private static JsonObject? ResolveParent(JsonObject document, IReadOnlyList<PatchPathSegment> segments, bool create) {
        var current = ((JsonObject?)document);

        for (var index = 0; ((current is not null) && (index < (segments.Count - 1))); index++) {
            current = Descend(
                create: create,
                current: current,
                path: string.Join(separator: ".", values: segments.Take(index + 1).Select(PatchPath.Render)),
                segment: segments[index]
            );
        }

        return current;
    }
    private static JsonObject? Descend(JsonObject current, PatchPathSegment segment, bool create, string path) {
        if (current[segment.Name] is not { } member) {
            if (!create) {
                return null;
            }

            member = ((segment.SelectorField is null) ? new JsonObject() : new JsonArray());
            current[segment.Name] = member;
        }

        if (segment.SelectorField is null) {
            return (member as JsonObject) ?? throw new InvalidOperationException(message: $"'{path}' is not an object.");
        }

        var array = (member as JsonArray) ?? throw new InvalidOperationException(message: $"'{path}' is not an array.");
        var existingIndex = FindIndex(
            array: array,
            keyField: segment.SelectorField,
            keyValue: segment.SelectorValue!
        );

        if (existingIndex >= 0) {
            return (array[existingIndex] as JsonObject) ?? throw new InvalidOperationException(message: $"'{path}' selects a non-object element.");
        }

        if (!create) {
            return null;
        }

        var created = new JsonObject { [segment.SelectorField] = segment.SelectorValue };

        array.Add(value: created);

        return created;
    }
    private static int FindIndex(JsonArray array, string keyField, string keyValue) {
        for (var index = 0; (index < array.Count); index++) {
            if ((array[index] is JsonObject row) && (row[keyField] is { } keyNode) && string.Equals(
                a: KeyText(node: keyNode),
                b: keyValue,
                comparisonType: StringComparison.Ordinal
            )) {
                return index;
            }
        }

        return -1;
    }
    // A row's key field is usually a string (a name/id); a few sections key by integer index. Either reads back
    // as the same text a caller compares a keyValue string against.
    private static string? KeyText(JsonNode? node) => ((node is JsonValue value)
        ? (value.TryGetValue<string>(value: out var text)
            ? text
            : value.ToJsonString())
        : node?.ToJsonString());
}
