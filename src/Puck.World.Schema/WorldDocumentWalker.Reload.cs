using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;

namespace Puck.World;

public sealed partial class WorldDocumentWalker {
    // The accepted patch and the candidate come from the same decisions. Resolution runs after all root members have
    // been assembled so new rows can read the merged state declarations and shade table.
    internal sealed record ReloadEdit(object? Value, JsonNode? Patch, int Rows, Func<WorldDefinition, object?>? Resolve = null);

    internal static ReloadEdit? MergeReloadRows(object? previous, object? live, object? next, JsonNode? before, JsonNode? after, JsonNode? patch,
        string path, ICollection<string> conflicts) =>
        new Cursor(rewriter: null!, tables: Everything.CurrentTables(), visitor: null!).MergeReloadRows(previous, live, next, before, after, patch, path, conflicts);

    // Applies a member's overlay delta to its typed value row by row: every keyed row the delta leaves alone is the value's
    // own instance, and each row it names is written, merged under the keyed-list rules and read again alone. Null when
    // the delta's shape is outside what a row-wise apply reproduces (a wholesale list, a positioned row, a dictionary, a
    // removed member, a polymorphic object), which the caller applies to the whole member instead.
    internal static ReloadEdit? ApplyPatchRows(object? value, JsonNode? patch, string path) =>
        new Cursor(rewriter: null!, tables: Everything.CurrentTables(), visitor: null!).ApplyPatchRows(value, patch, path);

    private static object? ResolveReloadValue(WorldDefinition source, object? value) {
        if (value is null) { return null; }
        if (!WorldStateDocumentValues.TryResolveGraph(source, value, out var resolved, out var reason)) {
            throw new InvalidDataException(reason);
        }
        return resolved;
    }

    internal sealed partial class Cursor {
        internal ReloadEdit? MergeReloadRows(object? previous, object? live, object? next, JsonNode? before, JsonNode? after, JsonNode? patch,
            string path, ICollection<string> conflicts) {
            if (previous is null || live is null || next is null || tables.ShapeOf(live.GetType()) is not { } shape) { return null; }

            if (shape.Kind == JsonTypeInfoKind.Enumerable && shape.ElementType is { } element &&
                previous is IEnumerable previousItems && live is IEnumerable liveItems && next is IEnumerable nextItems &&
                before is JsonArray beforeRows && after is JsonArray afterRows && patch is JsonArray { Count: > 0 } rowPatch &&
                !WorldDocumentBasis.IsReplaceMarker(rowPatch[0])) {
                return MergeReloadList(previousItems, liveItems, nextItems, element, beforeRows, afterRows, rowPatch, path, conflicts);
            }

            if (shape.Kind != JsonTypeInfoKind.Object || !shape.Described || previous.GetType() != shape.Type || next.GetType() != shape.Type ||
                before is not JsonObject beforeObject || after is not JsonObject afterObject || patch is not JsonObject objectPatch ||
                WorldDocumentBasis.ReplacesObject(objectPatch)) { return null; }

            var accepted = new JsonObject();
            var values = new Dictionary<string, object?>();
            var pending = new Dictionary<string, Func<WorldDefinition, object?>>();
            var rows = 0;
            var found = new List<string>();
            foreach (var (name, delta) in objectPatch) {
                var member = FindMember(shape.Type, name);
                if (member is null) { return null; }

                var prior = member.Read(previous);
                var current = member.Read(live);
                var target = member.Read(next);
                var edit = MergeReloadRows(prior, current, target, beforeObject[name], afterObject[name], delta, $"{path}.{name}", found);
                if (edit is null) {
                    if (!SameAuthoredValue(prior, current)) { return null; }
                    if (SameAuthoredValue(current, target)) { continue; }
                    edit = new ReloadEdit(target, delta, 0, source => ResolveReloadValue(source, target));
                } else if (ReferenceEquals(edit.Value, current)) {
                    rows += edit.Rows;
                    continue;
                }
                values.Add(name, edit.Value);
                accepted.Add(name, edit.Patch?.DeepClone());
                if (edit.Resolve is { } resolve) { pending.Add(name, resolve); }
                rows += edit.Rows;
            }
            if (rows == 0) { return null; }
            foreach (var conflict in found) { conflicts.Add(conflict); }
            if (values.Count == 0) { return new ReloadEdit(live, null, rows); }

            var merged = WithMembers(live, values);
            return new ReloadEdit(merged, accepted, rows, pending.Count == 0 ? null : source =>
                WithMembers(merged, pending.ToDictionary(pair => pair.Key, pair => pair.Value(source))));
        }

        private ReloadEdit? MergeReloadList(IEnumerable previous, IEnumerable live, IEnumerable next, Type element, JsonArray before, JsonArray after,
            JsonArray patch, string path, ICollection<string> conflicts) {
            var previousItems = previous.Cast<object?>().ToArray();
            var liveItems = live.Cast<object?>().ToArray();
            var nextItems = next.Cast<object?>().ToArray();
            if (previousItems.Length != before.Count || nextItems.Length != after.Count || liveItems.Length == 0) { return null; }

            var priorByInstance = new Dictionary<object, JsonNode?>(ReferenceEqualityComparer.Instance);
            for (var index = 0; index < previousItems.Length; index++) {
                if (previousItems[index] is { } item && !item.GetType().IsValueType) { priorByInstance.TryAdd(item, before[index]); }
            }
            var liveKeys = new List<JsonNode?>(liveItems.Length);
            foreach (var item in liveItems) {
                if (item is null) { return null; }
                liveKeys.Add(priorByInstance.TryGetValue(WorldStateDocumentValues.SourceOfBinding(item), out var original) ? original : IdentityOf(item, element));
            }

            if (!WorldDocumentBasis.TryFindRowKey(before, liveKeys, out var key, out _) ||
                !WorldDocumentBasis.TryFindRowKey(liveKeys, after, out var nextKey, out _) || key != nextKey ||
                !WorldDocumentBasis.TryFindRowKey(liveKeys, patch, out var patchKey, out _) || key != patchKey) { return null; }

            var priorIndex = WorldDocumentBasis.IndexRows(before, key, out _);
            var liveIndex = WorldDocumentBasis.IndexRows(liveKeys, key, out _);
            var nextIndex = WorldDocumentBasis.IndexRows(after, key, out _);
            var changed = new Dictionary<int, object?>();
            var added = new SortedDictionary<int, (object? Row, JsonObject Delta)>();
            var found = new List<string>();
            var accepted = new JsonArray();

            foreach (var node in patch) {
                var row = (JsonObject)node!;
                if (row.ContainsKey(WorldDocumentBasis.BeforeMemberName)) { return null; }
                var identity = WorldDocumentBasis.RowIdentity(row, key)!;
                var priorAt = priorIndex.GetValueOrDefault(identity, -1);
                var liveAt = liveIndex.GetValueOrDefault(identity, -1);
                var nextAt = nextIndex.GetValueOrDefault(identity, -1);
                var priorTree = priorAt < 0 ? null : (JsonObject)before[priorAt]!;
                var nextTree = nextAt < 0 ? null : (JsonObject)after[nextAt]!;
                if (OrderedJson.Equals(priorTree, nextTree)) { continue; }

                var current = liveAt < 0 ? null : liveItems[liveAt];
                var prior = priorAt < 0 ? null : previousItems[priorAt];
                var target = nextAt < 0 ? null : nextItems[nextAt];
                if (SameAuthoredValue(current, target)) { continue; }

                var liveTree = SameAuthoredValue(current, prior) ? priorTree : current is null ? null :
                    JsonSerializer.SerializeToNode(current, WorldJsonContext.Default.Options.GetTypeInfo(element))?.AsObject();
                if (!WorldDocumentBasis.SourceWinsRow(priorTree, liveTree, nextTree, out var conflict)) { continue; }

                if (!WorldDocumentBasis.TryDiffReloadRow(key, liveTree, nextTree, path, out var delta)) { return null; }
                if (conflict) { found.Add(WorldDocumentBasis.RowPath(path, key, identity)); }
                if (liveAt >= 0) {
                    changed.Add(liveAt, target);
                    accepted.Add(delta);
                } else {
                    added.Add(nextAt, (target, delta));
                }
            }

            if (changed.Count == 0 && added.Count == 0) { return new ReloadEdit(live, null, patch.Count); }

            var merged = new List<object?>(liveItems.Length + added.Count);
            var resolveAt = new List<int>();
            void AppendChanged(object? value) {
                if (value is null) { return; }
                resolveAt.Add(merged.Count);
                merged.Add(value);
            }
            for (var index = 0; index < liveItems.Length; index++) {
                if (changed.TryGetValue(index, out var replacement)) { AppendChanged(replacement); } else { merged.Add(liveItems[index]); }
            }
            foreach (var (row, delta) in added.Values) {
                AppendChanged(row);
                accepted.Add(delta);
            }
            if (!WorldDocumentBasis.TryFindRowKey(liveKeys, accepted, out var acceptedKey, out _) || acceptedKey != key) { return null; }
            foreach (var conflict in found) { conflicts.Add(conflict); }

            return RowsEdit(live, merged, resolveAt, accepted, patch.Count);
        }

        // A list rebuilt from its rows, with the rows at resolveAt resolved against the assembled document.
        private ReloadEdit RowsEdit(object list, List<object?> rows, List<int> resolveAt, JsonNode? patch, int count) =>
            new(Rebuild(list, [], rows, dictionary: false), patch, count, resolveAt.Count == 0 ? null : source => {
                foreach (var index in resolveAt) { rows[index] = ResolveReloadValue(source, rows[index]); }
                return Rebuild(list, [], rows, dictionary: false);
            });

        // A row's identity as the serializer writes it: the row-key members of a row whose shape the model lists, or the
        // whole row.
        private JsonNode? IdentityOf(object item, Type element) {
            if (tables.ShapeOf(item.GetType()) is not { Described: true, Kind: JsonTypeInfoKind.Object or JsonTypeInfoKind.None } rowShape) {
                return JsonSerializer.SerializeToNode(item, WorldJsonContext.Default.Options.GetTypeInfo(element));
            }
            var identity = new JsonObject();
            foreach (var member in tables.MembersOf(rowShape)) {
                if (WorldDocumentBasis.IsRowKeyMember(member.Name) && member.Read(item) is { } value) {
                    identity.Add(member.Name, JsonSerializer.SerializeToNode(value, WorldJsonContext.Default.Options.GetTypeInfo(member.Property.PropertyType)));
                }
            }
            return identity;
        }

        // A list's element type: the one its shape names, or, where the shape enumerates only objects (an owned list held
        // as an immutable array, which the model does not describe), the one its generic enumeration yields.
        private static Type? ElementOf(WorldModelType shape, Type type) =>
            ((shape.ElementType == typeof(object)) && (Enumerated(type: type, definition: typeof(IEnumerable<>)) is { } typed)) ? typed.GenericTypeArguments[0] : shape.ElementType;

        internal ReloadEdit? ApplyPatchRows(object? value, JsonNode? patch, string path) {
            if (value is null || patch is null || value is string || (value.GetType().IsValueType && value is not IEnumerable) || tables.ShapeOf(value.GetType()) is not { } shape) { return null; }

            if (shape.Kind == JsonTypeInfoKind.Enumerable && ElementOf(shape, value.GetType()) is { } element && value is IEnumerable items &&
                patch is JsonArray { Count: > 0 } rowPatch && !WorldDocumentBasis.IsReplaceMarker(rowPatch[0])) {
                return ApplyPatchList(items, element, rowPatch, path);
            }
            if (shape.Kind != JsonTypeInfoKind.Object || !shape.Described || patch is not JsonObject objectPatch ||
                WorldDocumentBasis.ReplacesObject(objectPatch) || objectPatch.ContainsKey("$type")) { return null; }

            var members = tables.MembersOf(shape);
            var values = new Dictionary<string, object?>();
            var pending = new Dictionary<string, Func<WorldDefinition, object?>>();
            var rows = 0;
            foreach (var (name, delta) in objectPatch) {
                if (Array.Find(members, member => member.Name == name) is not { } member) { return null; }

                var held = member.Read(value);
                var memberPath = $"{path}.{name}";
                if ((ApplyPatchRows(held, delta, memberPath) ?? ApplyPatchValue(member.Property.PropertyType, name, held, delta, path)) is not { } edit) { return null; }
                rows += edit.Rows;
                if (ReferenceEquals(edit.Value, held)) { continue; }
                values.Add(name, edit.Value);
                if (edit.Resolve is { } resolve) { pending.Add(name, resolve); }
            }
            if (values.Count == 0) { return new ReloadEdit(value, null, rows); }

            var merged = WithMembers(value, values);
            return new ReloadEdit(merged, null, rows, pending.Count == 0 ? null : source =>
                WithMembers(merged, pending.ToDictionary(pair => pair.Key, pair => pair.Value(source))));
        }

        // One member's value with its delta merged whole: written, merged under the member's rules and read again. Null
        // for a delta that removes the member, whose absent value only its owner's read knows.
        private static ReloadEdit? ApplyPatchValue(Type type, string name, object? held, JsonNode? delta, string path) {
            var tree = JsonSerializer.SerializeToNode(held, WorldJsonContext.Default.Options.GetTypeInfo(type));
            var before = tree?.DeepClone();
            var merged = WorldDocumentBasis.MergeMember(path, name, tree, delta, out var present);
            if (!present || merged is null) { return null; }
            if (OrderedJson.Equals(before, merged)) { return new ReloadEdit(held, null, 0); }

            var value = merged.Deserialize(WorldJsonContext.Authored.Options.GetTypeInfo(type));
            return new ReloadEdit(value, null, merged switch { JsonArray list => list.Count, JsonObject => 1, _ => 0 }, source => ResolveReloadValue(source, value));
        }

        // A keyed list's rows under its delta, as the keyed-list merge composes them: surviving rows in their order, each
        // named row refined or dropped, then new rows in the delta's order.
        private ReloadEdit? ApplyPatchList(IEnumerable items, Type element, JsonArray patch, string path) {
            var held = items.Cast<object?>().ToArray();
            if (held.Length == 0 || Array.IndexOf(held, null) >= 0) { return null; }

            var identities = new List<JsonNode?>(held.Length);
            foreach (var item in held) { identities.Add(IdentityOf(item!, element)); }
            if (!WorldDocumentBasis.TryFindRowKey(identities, patch, out var key, out _)) { return null; }

            var index = WorldDocumentBasis.IndexRows(identities, key, out _);
            var rowInfo = WorldJsonContext.Default.Options.GetTypeInfo(element);
            var readInfo = WorldJsonContext.Authored.Options.GetTypeInfo(element);
            var changed = new Dictionary<int, object?>();
            var added = new List<object?>();
            var rows = 0;

            foreach (var node in patch) {
                if (node is not JsonObject row || row.ContainsKey(WorldDocumentBasis.BeforeMemberName)) { return null; }

                var identity = WorldDocumentBasis.RowIdentity(row, key)!;
                var at = index.GetValueOrDefault(identity, -1);
                if (at < 0) {
                    if (row.ContainsKey(WorldDocumentBasis.DropMemberName)) { return null; }
                    WorldDocumentBasis.RefuseAppendedRow(row, path);
                    added.Add(row.Deserialize(readInfo));
                    rows++;
                    continue;
                }
                if (row.ContainsKey(WorldDocumentBasis.DropMemberName)) {
                    WorldDocumentBasis.RequireTombstone(row, key, path);
                    changed[at] = null;
                    continue;
                }

                // The identity read from the row's members must be the one its written form carries.
                if (JsonSerializer.SerializeToNode(held[at], rowInfo) is not JsonObject tree ||
                    !DocumentValueEqualityComparer.Instance.Equals(WorldDocumentBasis.RowIdentity(tree, key), identity)) { return null; }

                var before = tree.DeepClone();
                var refined = WorldDocumentBasis.RefineKeyedRow(tree, row, key, path);
                if (OrderedJson.Equals(before, refined)) { continue; }
                changed[at] = refined.Deserialize(readInfo);
                rows++;
            }
            if (changed.Count == 0 && added.Count == 0) { return new ReloadEdit(items, null, rows); }

            var merged = new List<object?>(held.Length + added.Count);
            var resolveAt = new List<int>();
            for (var at = 0; at < held.Length; at++) {
                if (!changed.TryGetValue(at, out var replacement)) { merged.Add(held[at]); continue; }
                if (replacement is null) { continue; }
                resolveAt.Add(merged.Count);
                merged.Add(replacement);
            }
            foreach (var row in added) {
                resolveAt.Add(merged.Count);
                merged.Add(row);
            }
            return RowsEdit(items, merged, resolveAt, null, rows);
        }
    }

    private static bool SameAuthoredValue(object? left, object? right) => ReferenceEquals(left, right) ||
        (left is not null && right is not null && ReferenceEquals(WorldStateDocumentValues.SourceOfBinding(left), WorldStateDocumentValues.SourceOfBinding(right)));
}
