using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Gpu;

namespace Puck.World;

internal sealed record CountersCeilingsDocument(string Schema, string Workload, string Script, int Width, int Height,
    IReadOnlyList<CountersKindLayout> Layouts, IReadOnlyList<CountersBackendDocument> Backends);
internal sealed record CountersKindLayout(IReadOnlyList<string> Kinds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, WorkClass>? Classes = null);
internal sealed record CountersBackendDocument(string Backend, CountersNodeMap Ceilings, IReadOnlyList<CountersDeviceDocument> Devices);
internal sealed record CountersDeviceDocument(GpuDeviceIdentity Device, CountersNodeMap Ceilings);
internal sealed class CountersNodeMap : SortedDictionary<string, CountersPassMap> {
    public CountersNodeMap() : base(comparer: StringComparer.Ordinal) { }
}
internal sealed class CountersPassMap : SortedDictionary<string, CountersBudgetGroup> {
    public CountersPassMap() : base(comparer: StringComparer.Ordinal) { }
}
internal sealed record CountersBudgetGroup {
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, CountersBudgetGroup>? Details { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? Device { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? Shared { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CountersBudgetValues? Values { get; init; }
}
/// <summary>The single compact ceilings spelling: interned measurement layouts, implicit zero budgets, and device deltas.</summary>
internal sealed class WorldCountersCeilingsJsonConverter : JsonConverter<WorldCountersCeilings>, IJsonSchemaNodeConverter {
    private static readonly IReadOnlyDictionary<string, WorkClass> Classes = GpuWork.SubmissionKinds.ToArray()
        .ToDictionary(keySelector: static kind => kind.Name, elementSelector: static kind => kind.Class, comparer: StringComparer.Ordinal);

    private readonly record struct Place(string Node, string? Pass, string? Detail);
    private sealed record Group(IReadOnlyList<int> Layout, IReadOnlyDictionary<string, long> Values);

    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => exportType(typeof(CountersCeilingsDocument)).AsObject();
    public override WorldCountersCeilings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var document = (JsonSerializer.Deserialize(ref reader, TypeInfo(options: options)) ?? throw new JsonException(message: "The ceilings document is null."));

        foreach (var layout in document.Layouts) {
            if ((layout.Kinds.Count == 0) || (layout.Kinds.Distinct(comparer: StringComparer.Ordinal).Count() != layout.Kinds.Count)) {
                throw new JsonException(message: "A measurement layout must name distinct GPU submission kinds.");
            }
            foreach (var kind in layout.Kinds) {
                _ = DefaultClass(kind: kind);
            }
            if (layout.Classes is not null) {
                foreach (var (kind, value) in layout.Classes) {
                    if (!layout.Kinds.Contains(kind, StringComparer.Ordinal) || (value == DefaultClass(kind: kind))) {
                        throw new JsonException(message: $"The class override for {kind} is not a difference in its layout.");
                    }
                }
            }
        }
        return new WorldCountersCeilings(document.Workload, document.Script, document.Width, document.Height,
            document.Backends.Select(selector: backend => {
                var common = Flatten(map: backend.Ceilings);
                var shared = Expand(common, document.Layouts, shared: true);
                var sharedKeys = shared.Select(selector: row => (new Place(row.Node, row.Pass, row.Detail), row.Kind)).ToHashSet();

                return new WorldCountersBackendCeilings(backend.Backend,
                    backend.Devices.Select(selector: device => {
                        var changes = Flatten(map: device.Ceilings);
                        var effective = new Dictionary<Place, CountersBudgetGroup>(dictionary: common);

                        foreach (var (place, delta) in changes) {
                            if (delta.Shared is not null) {
                                throw new JsonException(message: "A device cannot replace the shared measurement layout.");
                            }
                            var original = (common.GetValueOrDefault(key: place) ?? new CountersBudgetGroup());

                            if ((delta.Device is not null) && Same(left: delta.Device, right: original.Device)) {
                                throw new JsonException(message: "A device measurement layout must differ from its backend's.");
                            }
                            var values = new Dictionary<string, long>((original.Values ?? new CountersBudgetValues()), StringComparer.Ordinal);

                            foreach (var (kind, value) in (delta.Values ?? new CountersBudgetValues())) {
                                if ((value == 0) || (values.GetValueOrDefault(key: kind) == value)) {
                                    throw new JsonException(message: $"The device budget for {kind} is not a nonzero difference.");
                                }
                                values[kind] = value;
                            }
                            effective[place] = original with { Device = (delta.Device ?? original.Device), Values = new CountersBudgetValues(values: values) };
                        }
                        var own = Expand(effective, document.Layouts, shared: false);
                        var ownKeys = own.Select(selector: row => (new Place(row.Node, row.Pass, row.Detail), row.Kind)).ToHashSet();

                        foreach (var (place, delta) in changes) {
                            foreach (var kind in (delta.Values ?? new CountersBudgetValues()).Keys) {
                                if (sharedKeys.Contains(item: (place, kind)) && !ownKeys.Contains(item: (place, kind))) {
                                    throw new JsonException(message: $"kind={kind} cannot replace a shared budget in a device record.");
                                }
                            }
                        }
                        var held = shared.Concat(second: own).Select(selector: row => (new Place(row.Node, row.Pass, row.Detail), row.Kind)).ToHashSet();

                        foreach (var (place, group) in effective) {
                            foreach (var kind in (group.Values ?? new CountersBudgetValues()).Keys) {
                                if (!held.Contains(item: (place, kind))) { throw new JsonException(message: $"kind={kind} has a budget without a measurement layout."); }
                            }
                        }
                        return new WorldCountersDeviceCeilings(device.Device, own);
                    }).ToArray(), shared);
            }).ToArray()) { Schema = document.Schema };
    }
    public override void Write(Utf8JsonWriter writer, WorldCountersCeilings value, JsonSerializerOptions options) {
        var layouts = new Dictionary<string, CountersKindLayout>(comparer: StringComparer.Ordinal);

        foreach (var backend in value.Backends) {
            Collect(backend.Ceilings, layouts);
            foreach (var device in backend.Devices) {
                Collect(device.Ceilings, layouts);
            }
        }
        var ordered = layouts.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var indices = ordered.Select(selector: (pair, index) => (pair.Key, index)).ToDictionary(item => item.Key, item => item.index, StringComparer.Ordinal);
        var backends = value.Backends.Select(selector: backend => {
            var shared = Groups(backend.Ceilings, indices);
            var devices = backend.Devices.Select(selector: device => Groups(device.Ceilings, indices)).ToArray();
            var places = shared.Keys.Concat(second: devices.SelectMany(selector: device => device.Keys)).Distinct().ToArray();
            var common = new Dictionary<Place, CountersBudgetGroup>();

            foreach (var place in places) {
                var sharedGroup = shared.GetValueOrDefault(key: place);
                var first = devices.FirstOrDefault()?.GetValueOrDefault(key: place);
                var values = new SortedDictionary<string, long>(comparer: StringComparer.Ordinal);

                foreach (var (kind, reading) in (sharedGroup?.Values ?? new CountersBudgetValues())) {
                    if (reading != 0) { values.Add(key: kind, value: reading); }
                }
                foreach (var (kind, reading) in (first?.Values ?? new CountersBudgetValues())) {
                    if ((reading != 0) && devices.All(predicate: device => ((device.GetValueOrDefault(key: place)?.Values.GetValueOrDefault(key: kind) ?? 0L) == reading))) {
                        values[kind] = reading;
                    }
                }
                common.Add(key: place, value: new CountersBudgetGroup {
                    Shared = sharedGroup?.Layout,
                    Device = first?.Layout,
                    Values = ((values.Count == 0) ? null : new CountersBudgetValues(values: values)),
                });
            }
            return new CountersBackendDocument(Backend: backend.Backend, Ceilings: Nest(groups: common),
                Devices: backend.Devices.Select(selector: (device, index) => {
                    var deltas = new Dictionary<Place, CountersBudgetGroup>();

                    foreach (var place in places) {
                        var original = common[place];
                        var actual = devices[index].GetValueOrDefault(key: place);
                        var layout = (actual?.Layout ?? []);
                        var values = (actual?.Values ?? new CountersBudgetValues())
                            .Where(predicate: pair => ((pair.Value != 0) && (pair.Value != (original.Values?.GetValueOrDefault(key: pair.Key) ?? 0L))))
                            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                        var changedLayout = (Same(left: layout, right: original.Device) ? null : layout);

                        if ((changedLayout is not null) || (values.Count > 0)) {
                            deltas.Add(key: place, value: new CountersBudgetGroup { Device = changedLayout, Values = ((values.Count == 0) ? null : new CountersBudgetValues(values: values)) });
                        }
                    }
                    return new CountersDeviceDocument(device.Device, Nest(groups: deltas));
                }).ToArray());
        }).ToArray();
        var document = new CountersCeilingsDocument(value.Schema, value.Workload, value.Script, value.Width, value.Height,
            ordered.Select(selector: pair => pair.Value).ToArray(), backends);

        WriteCanonical(writer, JsonSerializer.SerializeToElement(document, TypeInfo(options: options)));
    }

    private static JsonTypeInfo<CountersCeilingsDocument> TypeInfo(JsonSerializerOptions options) =>
        ((JsonTypeInfo<CountersCeilingsDocument>)options.GetTypeInfo(type: typeof(CountersCeilingsDocument)));

    internal static WorkClass DefaultClass(string kind) => (Classes.TryGetValue(key: kind, value: out var value)
        ? value : throw new JsonException(message: $"kind={kind} is not a GPU submission kind"));

    private static bool Same(IReadOnlyList<int>? left, IReadOnlyList<int>? right) => (left ?? []).SequenceEqual(second: (right ?? []));
    private static string PassKey(string? pass) => ((pass is null) ? "" : ((pass.Length == 0) ? "~1" : pass.Replace(comparisonType: StringComparison.Ordinal, newValue: "~0", oldValue: "~")));
    private static string? PassOf(string key) => ((key.Length == 0) ? null : ((key == "~1") ? "" : key.Replace(comparisonType: StringComparison.Ordinal, newValue: "~", oldValue: "~0")));
    private static string Signature(IReadOnlyList<WorldCountCeiling> rows) => string.Join(separator: '|', values: rows.Select(selector: row => $"{row.Kind}:{((byte)row.Class)}"));
    private static IEnumerable<(Place Place, WorldCountCeiling[] Rows)> Runs(IReadOnlyList<WorldCountCeiling> rows) {
        var start = 0;

        while (start < rows.Count) {
            var first = rows[start];
            var place = new Place(first.Node, first.Pass, first.Detail);
            var end = (start + 1);

            while ((end < rows.Count) && (new Place(rows[end].Node, rows[end].Pass, rows[end].Detail) == place)) { end++; }
            yield return (place, rows.Skip(count: start).Take(count: (end - start)).ToArray());
            start = end;
        }
    }
    private static void Collect(IReadOnlyList<WorldCountCeiling> rows, Dictionary<string, CountersKindLayout> layouts) {
        foreach (var (_, run) in Runs(rows: rows)) {
            var overrides = run.Where(predicate: row => (row.Class != DefaultClass(kind: row.Kind)))
                .ToDictionary(row => row.Kind, row => row.Class, StringComparer.Ordinal);

            layouts.TryAdd(key: Signature(rows: run), value: new CountersKindLayout(run.Select(selector: row => row.Kind).ToArray(), ((overrides.Count == 0) ? null : overrides)));
        }
    }
    private static Dictionary<Place, Group> Groups(IReadOnlyList<WorldCountCeiling> rows, Dictionary<string, int> indices) {
        var groups = new Dictionary<Place, Group>();
        var order = 0;

        foreach (var (place, run) in Runs(rows: rows)) {
            var previous = groups.GetValueOrDefault(key: place);
            var values = new Dictionary<string, long>(collection: (previous?.Values ?? new CountersBudgetValues()), comparer: StringComparer.Ordinal);

            foreach (var row in run) { values.Add(key: row.Kind, value: row.Ceiling); }
            groups[place] = new Group(Layout: [.. (previous?.Layout ?? []), order++, indices[Signature(rows: run)]], Values: values);
        }
        return groups;
    }
    private static WorldCountCeiling[] Expand(Dictionary<Place, CountersBudgetGroup> groups, IReadOnlyList<CountersKindLayout> layouts, bool shared) {
        var runs = new SortedDictionary<int, WorldCountCeiling[]>();

        foreach (var (place, group) in groups) {
            var references = (shared ? group.Shared : group.Device);

            foreach (var (kind, value) in (group.Values ?? new CountersBudgetValues())) {
                _ = DefaultClass(kind: kind);
                if (value == 0) { throw new JsonException(message: $"kind={kind} records zero; absent budgets are zero."); }
            }
            if (references is null) { continue; }
            if ((references.Count % 2) != 0) { throw new JsonException(message: "A measurement layout reference needs an order and a layout index."); }
            for (var index = 0; (index < references.Count); index += 2) {
                var order = references[index];
                var layoutIndex = references[(index + 1)];

                if ((order < 0) || (layoutIndex < 0) || (layoutIndex >= layouts.Count)) { throw new JsonException(message: "A measurement layout reference is outside its table."); }
                var layout = layouts[layoutIndex];
                var rows = layout.Kinds.Select(selector: kind => new WorldCountCeiling(place.Node, place.Pass, kind,
                    (layout.Classes?.GetValueOrDefault(kind, DefaultClass(kind: kind)) ?? DefaultClass(kind: kind)), (group.Values?.GetValueOrDefault(key: kind) ?? 0L), Detail: place.Detail)).ToArray();

                if (!runs.TryAdd(key: order, value: rows)) { throw new JsonException(message: "A measurement layout repeats its order."); }
            }
        }
        return runs.Values.SelectMany(selector: rows => rows).ToArray();
    }
    private static Dictionary<Place, CountersBudgetGroup> Flatten(CountersNodeMap map) {
        var result = new Dictionary<Place, CountersBudgetGroup>();

        foreach (var (node, passes) in map) {
            foreach (var (pass, group) in passes) {
                result.Add(key: new Place(node, PassOf(key: pass), null), value: group with { Details = null });
                foreach (var (detail, item) in (group.Details ?? new Dictionary<string, CountersBudgetGroup>())) {
                    if (item.Details is not null) { throw new JsonException(message: "A detail cannot contain another detail."); }
                    result.Add(key: new Place(node, PassOf(key: pass), detail), value: item);
                }
            }
        }
        return result;
    }
    private static CountersNodeMap Nest(Dictionary<Place, CountersBudgetGroup> groups) {
        var result = new CountersNodeMap();

        foreach (var node in groups.GroupBy(keySelector: pair => pair.Key.Node)) {
            var passes = new CountersPassMap();

            foreach (var pass in node.GroupBy(keySelector: pair => pair.Key.Pass)) {
                var total = (pass.FirstOrDefault(predicate: pair => (pair.Key.Detail is null)).Value ?? new CountersBudgetGroup());
                var details = pass.Where(predicate: pair => (pair.Key.Detail is not null)).ToDictionary(pair => pair.Key.Detail!, pair => pair.Value, StringComparer.Ordinal);

                passes.Add(key: PassKey(pass: pass.Key), value: total with { Details = ((details.Count == 0) ? null : details) });
            }
            result.Add(key: node.Key, value: passes);
        }
        return result;
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element) {
        if (element.ValueKind == JsonValueKind.Object) {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)) {
                writer.WritePropertyName(propertyName: property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        } else if ((element.ValueKind == JsonValueKind.Array) && element.EnumerateArray().Any(predicate: item => (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array))) {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) { WriteCanonical(element: item, writer: writer); }
            writer.WriteEndArray();
        } else if (element.ValueKind == JsonValueKind.Array) {
            writer.WriteRawValue(CanonicalJsonDocument.SerializeCompact(node: JsonNode.Parse(element.GetRawText())!));
        } else { element.WriteTo(writer: writer); }
    }
}
