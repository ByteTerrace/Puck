using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Puck.Abstractions.Documents;

namespace Puck.World;

public sealed partial class WorldDocumentWalker {
    // The accepted patch and the candidate come from the same decisions. Resolution runs after all root members have
    // been assembled so new rows can read the merged state declarations and shade table.
    internal sealed record ReloadEdit(object? Value, JsonNode? Patch, int Rows, Func<WorldDefinition, object?>? Resolve = null);

    internal static ReloadEdit? MergeReloadRows(object? previous, object? live, object? next, JsonNode? before, JsonNode? after, JsonNode? patch,
        string path, ICollection<string> conflicts) =>
        new Cursor(rewriter: null!, tables: Everything.CurrentTables(), visitor: null!).MergeReloadRows(previous, live, next, before, after, patch, path, conflicts);

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
                if (priorByInstance.TryGetValue(WorldStateDocumentValues.SourceOfBinding(item), out var original)) {
                    liveKeys.Add(original);
                    continue;
                }
                if (tables.ShapeOf(item.GetType()) is not { Described: true, Kind: JsonTypeInfoKind.Object } rowShape) {
                    liveKeys.Add(JsonSerializer.SerializeToNode(item, WorldJsonContext.Default.Options.GetTypeInfo(element)));
                    continue;
                }
                var identity = new JsonObject();
                foreach (var member in tables.MembersOf(rowShape)) {
                    if (WorldDocumentBasis.IsRowKeyMember(member.Name) && member.Read(item) is { } value) {
                        identity.Add(member.Name, JsonSerializer.SerializeToNode(value, WorldJsonContext.Default.Options.GetTypeInfo(member.Property.PropertyType)));
                    }
                }
                liveKeys.Add(identity);
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

            return new ReloadEdit(Rebuild(live, [], merged, dictionary: false), accepted, patch.Count, resolveAt.Count == 0 ? null : source => {
                foreach (var index in resolveAt) { merged[index] = ResolveReloadValue(source, merged[index]); }
                return Rebuild(live, [], merged, dictionary: false);
            });
        }
    }

    private static bool SameAuthoredValue(object? left, object? right) => ReferenceEquals(left, right) ||
        (left is not null && right is not null && ReferenceEquals(WorldStateDocumentValues.SourceOfBinding(left), WorldStateDocumentValues.SourceOfBinding(right)));
}
