using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

[JsonConverter(typeof(CountersBudgetValuesJsonConverter))]
internal sealed class CountersBudgetValues : SortedDictionary<string, long> {
    public CountersBudgetValues() : base(comparer: StringComparer.Ordinal) { }
    public CountersBudgetValues(IEnumerable<KeyValuePair<string, long>> values) : this() {
        foreach (var (kind, value) in values) { Add(key: kind, value: value); }
    }
}
internal sealed class CountersBudgetValuesJsonConverter : JsonConverter<CountersBudgetValues>, IJsonSchemaNodeConverter {
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["type"] = "object",
        ["additionalProperties"] = new JsonObject { ["type"] = "integer", ["not"] = new JsonObject { ["const"] = 0 } },
    };
    public override CountersBudgetValues Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        using var document = JsonDocument.ParseValue(reader: ref reader);

        if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException(message: "Ceilings must be an object of nonzero budgets."); }
        var result = new CountersBudgetValues();

        foreach (var property in document.RootElement.EnumerateObject()) {
            _ = WorldCountersCeilingsJsonConverter.DefaultClass(kind: property.Name);
            if ((property.Value.ValueKind != JsonValueKind.Number) || !property.Value.TryGetInt64(value: out var value) || (value == 0)) {
                throw new JsonException(message: $"kind={property.Name} must have a nonzero integer budget; absent budgets are zero.");
            }
            if (!result.TryAdd(key: property.Name, value: value)) { throw new JsonException(message: $"kind={property.Name} repeats its budget."); }
        }
        return result;
    }
    public override void Write(Utf8JsonWriter writer, CountersBudgetValues value, JsonSerializerOptions options) {
        writer.WriteStartObject();
        foreach (var (kind, ceiling) in value) {
            _ = WorldCountersCeilingsJsonConverter.DefaultClass(kind: kind);
            if (ceiling == 0) { throw new JsonException(message: $"kind={kind} records zero; absent budgets are zero."); }
            writer.WriteNumber(propertyName: kind, value: ceiling);
        }
        writer.WriteEndObject();
    }
}
