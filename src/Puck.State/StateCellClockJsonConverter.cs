using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.State;

/// <summary>The one wire form of a <see cref="StateCellClock"/>, wherever a clock travels: a state row's cell, a pool
/// value, a disclosed observation. It is an object of optional members: <c>epochTick</c>, <c>epochEngineTick</c> and
/// <c>substepTicks</c> as whole numbers, and <c>y0</c> and <c>v0</c> as decimal <see cref="FixedQ4816"/> strings
/// whatever the carrying row's kind, since a follower's state is fixed-native. A zero member is left out, and a member
/// it does not map is refused by name.</summary>
public sealed class StateCellClockJsonConverter : JsonConverter<StateCellClock>, IJsonSchemaNodeConverter {
    /// <summary>Gets the clock's schema node: every member optional, the follower's state as decimal strings.</summary>
    /// <returns>The schema node.</returns>
    public static JsonObject Schema() => new() {
        ["type"] = "object",
        ["properties"] = new JsonObject {
            ["epochTick"] = new JsonObject { ["type"] = "integer" },
            ["epochEngineTick"] = new JsonObject { ["type"] = "integer" },
            ["y0"] = new JsonObject { ["type"] = "string" },
            ["v0"] = new JsonObject { ["type"] = "string" },
            ["substepTicks"] = new JsonObject { ["type"] = "integer" },
        },
        ["additionalProperties"] = false,
    };
    /// <summary>Reads a clock from its element, naming <paramref name="context"/> in every refusal.</summary>
    /// <param name="element">The clock's element.</param>
    /// <param name="context">Where the clock stands, for a refusal's message.</param>
    /// <returns>The clock.</returns>
    /// <exception cref="JsonException">The element is not a clock.</exception>
    public static StateCellClock Read(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new JsonException(message: $"{context} must be an object.");
        }

        var epochTick = 0L;
        var epochEngineTick = 0L;
        var y0 = 0L;
        var v0 = 0L;
        var substepTicks = 0L;

        foreach (var member in element.EnumerateObject()) {
            var path = $"{context}.{member.Name}";

            switch (member.Name) {
                case "epochTick":
                    epochTick = StateRowJsonConverter<StateRow>.RequireInt64(context: path, element: member.Value);
                    break;
                case "epochEngineTick":
                    epochEngineTick = StateRowJsonConverter<StateRow>.RequireInt64(context: path, element: member.Value);
                    break;
                case "y0":
                    y0 = StateRowJsonConverter<StateRow>.RequireFixed(context: path, element: member.Value);
                    break;
                case "v0":
                    v0 = StateRowJsonConverter<StateRow>.RequireFixed(context: path, element: member.Value);
                    break;
                case "substepTicks":
                    substepTicks = StateRowJsonConverter<StateRow>.RequireInt64(context: path, element: member.Value);
                    break;
                default:
                    throw new JsonException(message: $"{context} contains unmapped member '{member.Name}'.");
            }
        }

        return new StateCellClock(
            EpochEngineTick: epochEngineTick,
            EpochTick: epochTick,
            SubstepTicks: substepTicks,
            V0: v0,
            Y0: y0
        );
    }
    /// <summary>Writes a clock as an object, leaving out every zero member.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="clock">The clock.</param>
    public static void Write(Utf8JsonWriter writer, StateCellClock clock) {
        ArgumentNullException.ThrowIfNull(argument: writer);
        ArgumentNullException.ThrowIfNull(argument: clock);

        writer.WriteStartObject();

        if (clock.EpochTick != 0L) {
            writer.WriteNumber(propertyName: "epochTick", value: clock.EpochTick);
        }
        if (clock.EpochEngineTick != 0L) {
            writer.WriteNumber(propertyName: "epochEngineTick", value: clock.EpochEngineTick);
        }
        if (clock.Y0 != 0L) {
            writer.WriteString(propertyName: "y0", value: FixedQ4816.FromRawBits(value: clock.Y0).ToString());
        }
        if (clock.V0 != 0L) {
            writer.WriteString(propertyName: "v0", value: FixedQ4816.FromRawBits(value: clock.V0).ToString());
        }
        if (clock.SubstepTicks != 0L) {
            writer.WriteNumber(propertyName: "substepTicks", value: clock.SubstepTicks);
        }

        writer.WriteEndObject();
    }
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => Schema();
    /// <inheritdoc/>
    public override StateCellClock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        using var document = JsonDocument.ParseValue(reader: ref reader);

        return Read(context: "clock", element: document.RootElement);
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, StateCellClock value, JsonSerializerOptions options) => Write(
        clock: value,
        writer: writer
    );
}
