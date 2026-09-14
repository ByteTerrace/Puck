using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.State;

/// <summary>The hand-written wire shape of a <see cref="StateRow"/>: the <c>value</c>-vs-<c>cells</c> exclusivity
/// and the decimal fixed-point spelling refuse by name rather than defaulting. A document project that derives its
/// own row type extends this converter: it claims its extra members by name, validates them beside the shared
/// ones, and writes them back at the two hook points that keep the canonical member order.</summary>
/// <typeparam name="TRow">The row type the converter reads and writes.</typeparam>
/// <remarks>Every nested object (a trait, a domain, a draw) is read and written through the options' own
/// resolver, so the same converter serves any context that registers the nested types.</remarks>
public abstract partial class StateRowJsonConverter<TRow> : JsonConverter<TRow>, IJsonSchemaNodeConverter where TRow : StateRow {
    /// <summary>The wire members the shared shape parses, held as raw elements until the row's kind decides how
    /// each is read, plus whatever members the derived converter claimed.</summary>
    protected sealed class RowMembers {
        /// <summary>Gets the row's authored name.</summary>
        public string Name { get; internal set; } = string.Empty;
        /// <summary>Gets the members the derived converter claimed, keyed by wire name.</summary>
        public Dictionary<string, JsonElement> Claimed { get; } = new(comparer: StringComparer.Ordinal);

        /// <summary>Gets the raw <c>capacity</c> member, when authored.</summary>
        public JsonElement? Capacity { get; internal set; }
        /// <summary>Gets the raw <c>cells</c> member, when authored.</summary>
        public JsonElement? Cells { get; internal set; }
        /// <summary>Gets the raw <c>cycle</c> member, when authored.</summary>
        public JsonElement? Cycle { get; internal set; }
        /// <summary>Gets the raw <c>draw</c> member, when authored.</summary>
        public JsonElement? Draw { get; internal set; }
    }

    /// <summary>Gets the shape a refusal quotes for an unmapped or missing member — also the completeness law's own
    /// oracle for which top-level members the schema must describe.</summary>
    public abstract string Shape { get; }

    /// <summary>Determines whether a wire member outside the shared shape belongs to the derived row; a claimed
    /// member lands in <see cref="RowMembers.Claimed"/> instead of refusing.</summary>
    /// <param name="name">The wire member name.</param>
    protected virtual bool ClaimsMember(string name) => false;
    /// <summary>Determines whether the derived row's own members declare a draw site, so <c>drawCursor</c> and
    /// <c>drawnMasks</c> are admitted without a <c>draw</c> facet.</summary>
    /// <param name="members">The parsed members.</param>
    protected virtual bool DeclaresDrawSite(RowMembers members) => false;
    /// <summary>Refuses combinations of the derived row's own members with the shared ones, by name.</summary>
    /// <param name="members">The parsed members.</param>
    protected virtual void Validate(RowMembers members) { }
    /// <summary>Builds the row the converter returns from the shared row and the claimed members.</summary>
    /// <param name="row">The shared row, every shared member read.</param>
    /// <param name="members">The parsed members.</param>
    /// <param name="options">The serializer options the nested objects resolve through.</param>
    protected abstract TRow Create(StateRow row, RowMembers members, JsonSerializerOptions options);
    /// <summary>Writes the derived row's boolean flags, between <c>overflow</c> and <c>evicts</c>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="row">The row being written.</param>
    protected virtual void WriteFlags(Utf8JsonWriter writer, TRow row) { }
    /// <summary>Writes the derived row's trait objects, between <c>cycle</c> and <c>draw</c>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="row">The row being written.</param>
    /// <param name="options">The serializer options the nested objects resolve through.</param>
    protected virtual void WriteTraits(Utf8JsonWriter writer, TRow row, JsonSerializerOptions options) { }
    /// <summary>Reads a nested object through the options' resolver.</summary>
    /// <typeparam name="T">The nested type.</typeparam>
    /// <param name="element">The element to read.</param>
    /// <param name="options">The serializer options.</param>
    /// <param name="context">The member's spelling, for the refusal.</param>
    protected static T ReadNested<T>(JsonElement element, JsonSerializerOptions options, string context) =>
        (element.Deserialize(jsonTypeInfo: ((JsonTypeInfo<T>)options.GetTypeInfo(type: typeof(T))))
            ?? throw new JsonException(message: $"{context} must be an object."));
    /// <summary>Writes a nested object through the options' resolver.</summary>
    /// <typeparam name="T">The nested type.</typeparam>
    /// <param name="writer">The writer.</param>
    /// <param name="propertyName">The member name.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="options">The serializer options.</param>
    protected static void WriteNested<T>(Utf8JsonWriter writer, string propertyName, T value, JsonSerializerOptions options) {
        writer.WritePropertyName(propertyName: propertyName);
        JsonSerializer.Serialize(
            writer: writer,
            value: value,
            jsonTypeInfo: ((JsonTypeInfo<T>)options.GetTypeInfo(type: typeof(T)))
        );
    }
    /// <summary>Reads a boolean member, refusing any other token.</summary>
    /// <param name="element">The element.</param>
    /// <param name="context">The member's spelling, for the refusal.</param>
    protected static bool RequireBool(JsonElement element, string context) => element.ValueKind switch {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new JsonException(message: $"{context} must be a boolean."),
    };

    // A clock's y0/v0 ride the same per-kind spelling as an ordinary cell value (a decimal string via FixedQ4816 for
    // a fixed row, a plain JSON number for int) — never raw bits, matching StateCell.Value's own wire convention.
    private static StateCellClock ReadClock(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new JsonException(message: $"{context} must be an object.");
        }

        var epochTick = 0L;
        var epochEngineTick = 0L;
        JsonElement? y0 = null;
        JsonElement? v0 = null;
        var substepTicks = 0L;

        foreach (var member in element.EnumerateObject()) {
            switch (member.Name) {
                case "epochTick":
                    epochTick = RequireInt64(
                        context: $"{context}.epochTick",
                        element: member.Value
                    );
                    break;
                case "epochEngineTick":
                    epochEngineTick = RequireInt64(
                        context: $"{context}.epochEngineTick",
                        element: member.Value
                    );
                    break;
                case "y0":
                    y0 = member.Value;
                    break;
                case "v0":
                    v0 = member.Value;
                    break;
                case "substepTicks":
                    substepTicks = RequireInt64(
                        context: $"{context}.substepTicks",
                        element: member.Value
                    );
                    break;
                default:
                    throw new JsonException(message: $"{context} contains unmapped member '{member.Name}'.");
            }
        }

        // y0/v0 are the follower's continuous state and ride raw FixedQ4816 bits whatever the carrying row's kind
        // (see StateDynamics), so they are authored in the fixed spelling — a decimal string — regardless of row kind.
        return new StateCellClock(
            EpochTick: epochTick,
            EpochEngineTick: epochEngineTick,
            Y0: ((y0 is { } y0Element)
            ? RequireNumeric(
                    context: $"{context}.y0",
                    element: y0Element,
                    kind: CellKind.Fixed
                )
            : 0L),
            V0: ((v0 is { } v0Element)
            ? RequireNumeric(
                    context: $"{context}.v0",
                    element: v0Element,
                    kind: CellKind.Fixed
                )
            : 0L),
            SubstepTicks: substepTicks
        );
    }
    private static StateCell ReadCell(CellKind cellKind, CellName key, JsonElement element, string context, StateAdvance? advance = null, string? provenance = null, StateDynamics? dynamics = null, StateCycle? cycle = null, StateCellBehavior behavior = StateCellBehavior.Inherit, StateCellClock? clock = null) => cellKind switch {
        CellKind.Text => new StateCell(
        Key: key,
        Text: RequireString(
            context: context,
            element: element
        ),
        Advance: advance,
        Provenance: provenance,
        Dynamics: dynamics,
        Cycle: cycle,
        Behavior: behavior,
        Clock: clock
    ),
        CellKind.Bool => new StateCell(
        Key: key,
        Value: (RequireBool(
            context: context,
            element: element
        )
        ? 1
        : 0),
        Advance: advance,
        Provenance: provenance,
        Dynamics: dynamics,
        Cycle: cycle,
        Behavior: behavior,
        Clock: clock
    ),
        _ => new StateCell(
        Key: key,
        Value: RequireNumeric(
            context: context,
            element: element,
            kind: cellKind
        ),
        Advance: advance,
        Provenance: provenance,
        Dynamics: dynamics,
        Cycle: cycle,
        Behavior: behavior,
        Clock: clock
    ),
    };
    private static List<StateCell> ReadCells(string name, CellKind cellKind, JsonElement cellsElement, JsonSerializerOptions options) {
        if (cellsElement.ValueKind != JsonValueKind.Array) {
            throw new JsonException(message: $"state row '{name}'.cells must be an array of {{\"key\":…,\"value\":…}} objects.");
        }

        var cells = new List<StateCell>();
        var index = 0;

        foreach (var entry in cellsElement.EnumerateArray()) {
            if (entry.ValueKind != JsonValueKind.Object) {
                throw new JsonException(message: $"state row '{name}'.cells[{index}] must be an object.");
            }

            string? key = null;
            JsonElement? cellValue = null;
            JsonElement? cellAdvance = null;
            JsonElement? cellDynamics = null;
            JsonElement? cellCycle = null, cellVisibility = null, cellObservation = null;
            JsonElement? cellBehavior = null, cellClock = null;
            string? provenance = null;

            foreach (var member in entry.EnumerateObject()) {
                switch (member.Name) {
                    case "key":
                        key = ((member.Value.ValueKind == JsonValueKind.String)
                            ? member.Value.GetString()
                            : null
                        );
                        break;
                    case "value":
                        cellValue = member.Value;
                        break;
                    case "advance":
                        cellAdvance = member.Value;
                        break;
                    case "dynamics":
                        cellDynamics = member.Value;
                        break;
                    case "cycle":
                        cellCycle = member.Value;
                        break;
                    case "behavior": cellBehavior = member.Value; break;
                    case "clock": cellClock = member.Value; break;
                    case "visibility": cellVisibility = member.Value; break;
                    case "observation": cellObservation = member.Value; break;
                    case "provenance":
                        provenance = ((member.Value.ValueKind == JsonValueKind.String)
                            ? member.Value.GetString()
                            : null
                        );
                        break;
                    default:
                        throw new JsonException(message: $"state row '{name}'.cells[{index}] contains unmapped member '{member.Name}'.");
                }
            }

            if (string.IsNullOrEmpty(value: key)) {
                throw new JsonException(message: $"state row '{name}'.cells[{index}] requires member 'key'.");
            }
            if (!CellName.TryParse(
                candidate: key,
                name: out var cellKey,
                reason: out var keyReason
            )) {
                throw new JsonException(message: $"state row '{name}'.cells[{index}].key '{key}' {keyReason}.");
            }
            if (cellValue is not { } cellValueElement) {
                throw new JsonException(message: $"state row '{name}'.cells[{index}] requires member 'value'.");
            }

            var advance = ((cellAdvance is { } cellAdvanceElement)
                ? ReadNested<StateAdvance>(
                    context: $"state row '{name}'.cells[{index}].advance",
                    element: cellAdvanceElement,
                    options: options
                )
                : null
            );
            var dynamics = ((cellDynamics is { } cellDynamicsElement)
                ? ReadNested<StateDynamics>(
                    context: $"state row '{name}'.cells[{index}].dynamics",
                    element: cellDynamicsElement,
                    options: options
                )
                : null
            );

            var cycle = ((cellCycle is { } cellCycleElement)
                ? ReadNested<StateCycle>(
                    context: $"state row '{name}'.cells[{index}].cycle",
                    element: cellCycleElement,
                    options: options
                )
                : null
            );
            var behavior = ((cellBehavior is { } cellBehaviorElement)
                ? ReadNested<StateCellBehavior>(
                    context: $"state row '{name}'.cells[{index}].behavior",
                    element: cellBehaviorElement,
                    options: options
                )
                : StateCellBehavior.Inherit
            );
            var clock = ((cellClock is { } cellClockElement)
                ? ReadClock(
                    context: $"state row '{name}'.cells[{index}].clock",
                    element: cellClockElement
                )
                : null
            );

            if (
                (behavior == StateCellBehavior.None) &&
                ((advance is not null) || (dynamics is not null) || (cycle is not null))
            ) {
                throw new JsonException(message: $"state row '{name}'.cells[{index}] declares behavior 'none' beside its own advance/dynamics/cycle — a cell that opts out declares no trait of its own either.");
            }

            cells.Add(item: ReadCell(
                advance: advance,
                behavior: behavior,
                cellKind: cellKind,
                clock: clock,
                context: $"state row '{name}'.cells[{index}].value",
                cycle: cycle,
                dynamics: dynamics,
                element: cellValueElement,
                key: cellKey,
                provenance: provenance
            ) with {
                Visibility = ((cellVisibility is { } cv)
                ? ReadNested<StateVisibility>(
                    context: "visibility",
                    element: cv,
                    options: options
                )
                : null),
                Observation = ((cellObservation is { } co)
                ? ReadNested<StateObservation>(
                    context: "observation",
                    element: co,
                    options: options
                )
                : null),
            });
            index++;
        }

        return cells;
    }
    // Engine-minted per-context drawn masks, by the source's context declaration ordinal. Refused off-shape rather
    // than coerced: these are the one part of a draw site's position the cursor cannot express.
    private static List<ClosedBitset256> ReadDrawnMasks(string name, JsonElement element) {
        if (element.ValueKind != JsonValueKind.Array) {
            throw new JsonException(message: $"state row '{name}'.drawnMasks must be an array of per-context drawn masks.");
        }

        var masks = new List<ClosedBitset256>(capacity: element.GetArrayLength());

        foreach (var entry in element.EnumerateArray()) {
            if (
                (entry.ValueKind != JsonValueKind.String) ||
                !ClosedBitset256.TryParse(
                text: entry.GetString(),
                value: out var mask
            )
            ) {
                throw new JsonException(message: $"state row '{name}'.drawnMasks[{masks.Count}] must be a 64-digit hexadecimal string.");
            }
            masks.Add(item: mask);
        }

        return masks;
    }
    // Fixed-kind values are human-authored decimal text, never the raw Q48.16 bit pattern: the document, the console
    // verb JSON, and every echo agree on one spelling.
    private static long RequireFixed(JsonElement element, string context) {
        if (
            (element.ValueKind != JsonValueKind.String) ||
            !FixedQ4816.TryParse(
            s: element.GetString(),
            provider: CultureInfo.InvariantCulture,
            result: out var parsed
        )
        ) {
            throw new JsonException(message: $"{context} must be a decimal string parseable as FixedQ4816 (e.g. \"12.5\"), never raw bits.");
        }

        return parsed.Value;
    }
    private static int RequireInt32(JsonElement element, string context) {
        if (
            (element.ValueKind != JsonValueKind.Number) ||
            !element.TryGetInt32(value: out var parsed)
        ) {
            throw new JsonException(message: $"{context} must be a whole number.");
        }

        return parsed;
    }
    private static long RequireInt64(JsonElement element, string context) {
        if (
            (element.ValueKind != JsonValueKind.Number) ||
            !element.TryGetInt64(value: out var parsed)
        ) {
            throw new JsonException(message: $"{context} must be a whole number.");
        }

        return parsed;
    }
    private static long RequireNumeric(CellKind kind, JsonElement element, string context) => kind switch {
        CellKind.Fixed => RequireFixed(
        context: context,
        element: element
    ),
        _ => RequireInt64(
        context: context,
        element: element
    ),
    };
    private static string RequireString(JsonElement element, string context) =>
        ((element.ValueKind == JsonValueKind.String)
            ? (element.GetString() ?? string.Empty)
            : throw new JsonException(message: $"{context} must be a string.")
        );
    private static void WriteCellValue(Utf8JsonWriter writer, string propertyName, CellKind kind, StateCell cell) {
        switch (kind) {
            case CellKind.Text:
                writer.WriteString(
                    propertyName: propertyName,
                    value: (cell.Text ?? string.Empty)
                );
                break;
            case CellKind.Bool:
                writer.WriteBoolean(
                    propertyName: propertyName,
                    value: (cell.Value != 0)
                );
                break;
            case CellKind.Fixed:
                writer.WriteString(
                    propertyName: propertyName,
                    value: FixedQ4816.FromRawBits(value: cell.Value).ToString()
                );
                break;
            default:
                writer.WriteNumber(
                    propertyName: propertyName,
                    value: cell.Value
                );
                break;
        }
    }
    // Written only when non-default (a fresh cell, or one settled back to epoch zero, carries no "clock" member at
    // all) — the follower's continuous state is fixed-native on every row kind, so y0/v0 are written in the fixed
    // spelling.
    private static void WriteClock(Utf8JsonWriter writer, string propertyName, StateCellClock clock) {
        writer.WritePropertyName(propertyName: propertyName);
        writer.WriteStartObject();

        if (clock.EpochTick != 0L) {
            writer.WriteNumber(
                propertyName: "epochTick",
                value: clock.EpochTick
            );
        }
        if (clock.EpochEngineTick != 0L) {
            writer.WriteNumber(
                propertyName: "epochEngineTick",
                value: clock.EpochEngineTick
            );
        }
        if (clock.Y0 != 0L) {
            WriteOptionalNumeric(
                writer: writer,
                propertyName: "y0",
                kind: CellKind.Fixed,
                raw: clock.Y0
            );
        }
        if (clock.V0 != 0L) {
            WriteOptionalNumeric(
                writer: writer,
                propertyName: "v0",
                kind: CellKind.Fixed,
                raw: clock.V0
            );
        }
        if (clock.SubstepTicks != 0L) {
            writer.WriteNumber(
                propertyName: "substepTicks",
                value: clock.SubstepTicks
            );
        }

        writer.WriteEndObject();
    }
    private static void WriteOptionalNumeric(Utf8JsonWriter writer, string propertyName, CellKind kind, long? raw) {
        if (raw is not { } rawValue) {
            return;
        }

        if (kind == CellKind.Fixed) {
            writer.WriteString(
                propertyName: propertyName,
                value: FixedQ4816.FromRawBits(value: rawValue).ToString()
            );
        } else {
            writer.WriteNumber(
                propertyName: propertyName,
                value: rawValue
            );
        }
    }

    /// <inheritdoc/>
    public override TRow Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartObject) {
            throw new JsonException(message: "a state row must be a JSON object.");
        }

        string? name = null;
        JsonElement? kind = null;
        JsonElement? value = null;
        JsonElement? cells = null;
        JsonElement? min = null;
        JsonElement? max = null;
        JsonElement? capacity = null;
        JsonElement? overflow = null;
        JsonElement? evicts = null;
        JsonElement? draw = null;
        JsonElement? drawCursor = null;
        JsonElement? drawnMasks = null;
        JsonElement? historyCursor = null;
        JsonElement? domain = null;
        JsonElement? inverse = null;
        string? phaseOf = null;
        string? valuesFrom = null;
        JsonElement? phase = null, visibility = null, knowledge = null;
        JsonElement? advance = null;
        JsonElement? dynamics = null;
        JsonElement? cycle = null;
        JsonElement? clock = null;
        var members = new RowMembers();

        while (
            reader.Read() &&
            (reader.TokenType != JsonTokenType.EndObject)
        ) {
            if (reader.TokenType != JsonTokenType.PropertyName) {
                throw new JsonException(message: "a state row member name was expected.");
            }

            var property = reader.GetString();

            if (!reader.Read()) {
                throw new JsonException(message: $"state row member '{property}' has no value.");
            }

            switch (property) {
                case "name":
                    name = ((reader.TokenType == JsonTokenType.String)
                        ? reader.GetString()
                        : null
                    );
                    break;
                case "kind":
                    kind = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "value":
                    value = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "cells":
                    cells = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "min":
                    min = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "max":
                    max = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "capacity":
                    capacity = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "overflow":
                    overflow = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "evicts":
                    evicts = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "draw":
                    draw = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "drawCursor":
                    drawCursor = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "historyCursor":
                    historyCursor = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "visibility": visibility = JsonElement.ParseValue(reader: ref reader); break;
                case "knowledge": knowledge = JsonElement.ParseValue(reader: ref reader); break;
                case "phase":
                    phase = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "valuesFrom":
                    valuesFrom = reader.GetString();
                    break;
                case "phaseOf": phaseOf = reader.GetString(); break;
                case "domain":
                    domain = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "inverse":
                    inverse = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "drawnMasks":
                    drawnMasks = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "advance":
                    advance = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "dynamics":
                    dynamics = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "cycle":
                    cycle = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "clock":
                    clock = JsonElement.ParseValue(reader: ref reader);
                    break;
                default:
                    if (
                        (property is not null) &&
                        ClaimsMember(name: property)
                    ) {
                        members.Claimed[property] = JsonElement.ParseValue(reader: ref reader);
                        break;
                    }

                    throw new JsonException(message: $"state row contains unmapped member '{property}' — a state row is {Shape}.");
            }
        }

        if (string.IsNullOrEmpty(value: name)) {
            throw new JsonException(message: $"a state row requires member 'name' — a state row is {Shape}.");
        }
        if (!CellName.TryParse(
            candidate: name,
            name: out var rowName,
            reason: out var nameReason
        )) {
            throw new JsonException(message: $"state row 'name' '{name}' {nameReason}.");
        }
        if (kind is not { } kindElement) {
            throw new JsonException(message: $"state row '{name}' requires member 'kind' (Int|Fixed|Bool|Text).");
        }

        members.Name = name;
        members.Cycle = cycle;
        members.Draw = draw;
        members.Capacity = capacity;
        members.Cells = cells;

        if (
            (value is not null) &&
            (cells is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares both 'value' and 'cells' — 'value' IS the one-cell spelling of 'cells' (it addresses the reserved key '{StateRow.SlotKey}'); author one or the other.");
        }
        if (
            (value is not null) &&
            (capacity is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'value' beside 'capacity' — declaring a capacity is declaring a keyed row, whose cells are authored under 'cells'.");
        }
        // 'draw' sits beside 'value'/'cells' (the shape 'advance' already follows, never XOR against them): the cell
        // is the site's current value and 'draw' is what decides it. An authored 'value' therefore pre-empts the first
        // fill — the resolver only fills a site carrying no cell yet — which is the deliberate authored-override door.
        if (
            (advance is not null) &&
            (draw is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares both 'advance' and 'draw' — a row is a continuous accumulator or an authored-randomness draw site, never both.");
        }
        if (
            (drawCursor is not null) &&
            (draw is null) &&
            !DeclaresDrawSite(members: members)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'drawCursor' without 'draw' — drawCursor is engine bookkeeping for a draw site alone.");
        }
        if (
            (drawnMasks is not null) &&
            (draw is null) &&
            !DeclaresDrawSite(members: members)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'drawnMasks' without 'draw' — drawnMasks is engine bookkeeping for a draw site alone.");
        }
        if (
            (dynamics is not null) &&
            (draw is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares both 'dynamics' and 'draw' — a row is an authored-randomness draw site or a second-order easing cell, never both.");
        }
        if (
            (dynamics is not null) &&
            (advance is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares both 'dynamics' and 'advance' — a row is a linear accumulator or a second-order easing cell, never both.");
        }

        if (
            (cycle is not null) &&
            (draw is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares both 'cycle' and 'draw' — a row is an authored-randomness draw site or a tick-indexed rotation, never both.");
        }
        if (
            (cycle is not null) &&
            (advance is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares both 'cycle' and 'advance' — a row is a linear accumulator or a tick-indexed rotation, never both.");
        }
        if (
            (cycle is not null) &&
            (dynamics is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares both 'cycle' and 'dynamics' — a row is a second-order easing cell or a tick-indexed rotation, never both.");
        }
        // 'clock' is the slot cell's own timing state (see StateCellClock), so it rides beside 'value' alone — the
        // same rule 'advance'/'dynamics'/'cycle' at the row level follow, restated for the timing member that
        // carries no trait of its own.
        if (
            (clock is not null) &&
            (value is null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'clock' without 'value' — 'clock' is the slot cell's own timing state, authored beside 'value'; a keyed row's cells each carry their own 'clock'.");
        }
        Validate(members: members);

        CellKind cellKind;

        try {
            cellKind = kindElement.Deserialize(jsonTypeInfo: ((JsonTypeInfo<CellKind>)options.GetTypeInfo(type: typeof(CellKind))));
        } catch (JsonException) {
            throw new JsonException(message: $"state row '{name}'.kind must be one of {string.Join(
                separator: ", ",
                values: Enum.GetNames<CellKind>()
            )}.");
        }

        var row = new StateRow(
            Name: rowName,
            Kind: cellKind,
            Min: ((min is { } minElement)
            ? RequireNumeric(
                    context: $"state row '{name}'.min",
                    element: minElement,
                    kind: cellKind
                )
            : null),
            Max: ((max is { } maxElement)
            ? RequireNumeric(
                    context: $"state row '{name}'.max",
                    element: maxElement,
                    kind: cellKind
                )
            : null),
            Capacity: ((capacity is { } capacityElement)
            ? RequireInt32(
                    context: $"state row '{name}'.capacity",
                    element: capacityElement
                )
            : null),
            Overflow: ((overflow is { } overflowElement)
            ? ReadNested<StateOverflow>(
                    context: $"state row '{name}'.overflow",
                    element: overflowElement,
                    options: options
                )
            : StateOverflow.Refuse),
            Evicts: ((evicts is { } evictsElement) && RequireBool(
                context: $"state row '{name}'.evicts",
                element: evictsElement
            )),
            Cells: ((value is { } valueElement)
            ? [ReadCell(
                        cellKind: cellKind,
                        clock: ((clock is { } clockElement)
                            ? ReadClock(
                                context: $"state row '{name}'.clock",
                                element: clockElement
                            )
                            : null),
                        key: StateRow.SlotKey,
                        element: valueElement,
                        context: $"state row '{name}'.value"
                    )]
            : ((cells is { } cellsElement)
                ? ReadCells(
                        cellKind: cellKind,
                        cellsElement: cellsElement,
                        name: name,
                        options: options
                    )
                : [])),
            Advance: ((advance is { } advanceElement)
            ? ReadNested<StateAdvance>(
                    context: $"state row '{name}'.advance",
                    element: advanceElement,
                    options: options
                )
            : null),
            Draw: ((draw is { } drawElement)
            ? ReadNested<Draw>(
                    context: $"state row '{name}'.draw",
                    element: drawElement,
                    options: options
                )
            : null),
            DrawCursor: ((drawCursor is { } drawCursorElement)
            ? RequireInt64(
                    context: $"state row '{name}'.drawCursor",
                    element: drawCursorElement
                )
            : 0L),
            Visibility: ((visibility is { } visibilityElement)
            ? ReadNested<StateVisibility>(
                    context: "visibility",
                    element: visibilityElement,
                    options: options
                )
            : null),
            Knowledge: ((knowledge is { } knowledgeElement)
            ? ReadNested<StateKnowledge>(
                    context: "knowledge",
                    element: knowledgeElement,
                    options: options
                )
            : null),
            Phase: ((phase is { } phaseElement)
            ? ReadNested<StatePhase>(
                    context: "phase",
                    element: phaseElement,
                    options: options
                )
            : null),
            PhaseOf: phaseOf,
            ValuesFrom: valuesFrom,
            Domain: ((domain is { } domainElement)
            ? ReadNested<StateDomain>(
                    context: $"state row '{name}'.domain",
                    element: domainElement,
                    options: options
                )
            : null),
            Inverse: ((inverse is { } inverseElement)
            ? ReadNested<StateInverse>(
                    context: $"state row '{name}'.inverse",
                    element: inverseElement,
                    options: options
                )
            : null),
            DrawnMasks: ((drawnMasks is { } drawnMasksElement)
            ? ReadDrawnMasks(
                    element: drawnMasksElement,
                    name: name
                )
            : null),
            Dynamics: ((dynamics is { } dynamicsElement)
            ? ReadNested<StateDynamics>(
                    context: $"state row '{name}'.dynamics",
                    element: dynamicsElement,
                    options: options
                )
            : null),
            Cycle: ((cycle is { } cycleElement)
            ? ReadNested<StateCycle>(
                    context: $"state row '{name}'.cycle",
                    element: cycleElement,
                    options: options
                )
            : null),
            HistoryCursor: ((historyCursor is { } historyCursorElement)
            ? RequireInt64(
                    context: $"state row '{name}'.historyCursor",
                    element: historyCursorElement
                )
            : 0L)
        );

        return Create(
            members: members,
            options: options,
            row: row
        );
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TRow value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(argument: value);

        writer.WriteStartObject();
        writer.WriteString(
            propertyName: "name",
            value: value.Name
        );
        writer.WriteString(
            propertyName: "kind",
            value: StateSpelling.Kind(kind: value.Kind)
        );
        WriteOptionalNumeric(
            writer: writer,
            propertyName: "min",
            kind: value.Kind,
            raw: value.Min
        );
        WriteOptionalNumeric(
            writer: writer,
            propertyName: "max",
            kind: value.Kind,
            raw: value.Max
        );

        if (value.Capacity is { } declaredCapacity) {
            writer.WriteNumber(
                propertyName: "capacity",
                value: declaredCapacity
            );
        }

        if (value.Overflow != StateOverflow.Refuse) {
            WriteNested(
                options: options,
                propertyName: "overflow",
                value: value.Overflow,
                writer: writer
            );
        }

        WriteFlags(
            row: value,
            writer: writer
        );

        if (value.Evicts) {
            writer.WriteBoolean(
                propertyName: "evicts",
                value: true
            );
        }

        // The one authored shape's two carriers: a slot writes back the same `value` sugar it was authored with, so a
        // load->save round-trip is byte-identical; every other shape writes its cells keyed. A slot row that has
        // never been explicitly set carries no cell yet (the first write mints it) — round-trips as an empty
        // `cells` array, since there is no value to spell as `value` sugar.
        if (
            value.IsSlot &&
            (value.Cells is { Count: 1 })
        ) {
            WriteCellValue(
                writer: writer,
                propertyName: "value",
                kind: value.Kind,
                cell: value.Cells[0]
            );

            if (value.Cells[0].Clock is { } slotClock) {
                WriteClock(
                    clock: slotClock,
                    propertyName: "clock",
                    writer: writer
                );
            }
        } else {
            writer.WriteStartArray(propertyName: "cells");

            foreach (var cell in (value.Cells ?? [])) {
                writer.WriteStartObject();
                writer.WriteString(
                    propertyName: "key",
                    value: cell.Key
                );
                WriteCellValue(
                    writer: writer,
                    propertyName: "value",
                    kind: value.Kind,
                    cell: cell
                );

                if (cell.Advance is { } cellAdvance) {
                    WriteNested(
                        options: options,
                        propertyName: "advance",
                        value: cellAdvance,
                        writer: writer
                    );
                }

                if (cell.Dynamics is { } cellDynamics) {
                    WriteNested(
                        options: options,
                        propertyName: "dynamics",
                        value: cellDynamics,
                        writer: writer
                    );
                }

                if (cell.Cycle is { } cellCycle) {
                    WriteNested(
                        options: options,
                        propertyName: "cycle",
                        value: cellCycle,
                        writer: writer
                    );
                }

                if (cell.Behavior != StateCellBehavior.Inherit) {
                    WriteNested(
                        options: options,
                        propertyName: "behavior",
                        value: cell.Behavior,
                        writer: writer
                    );
                }

                if (cell.Clock is { } cellClock) {
                    WriteClock(
                        clock: cellClock,
                        propertyName: "clock",
                        writer: writer
                    );
                }

                if (cell.Visibility is { } cellVisibility) { WriteNested(
                    options: options,
                    propertyName: "visibility",
                    value: cellVisibility,
                    writer: writer
                ); }
                if (cell.Observation is { } observation) { WriteNested(
                    options: options,
                    propertyName: "observation",
                    value: observation,
                    writer: writer
                ); }
                if (cell.Provenance is { } provenance) {
                    writer.WriteString(
                        propertyName: "provenance",
                        value: provenance
                    );
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        // Advance and draw are mutually exclusive (see StateAdvance's remarks), so write order between them never
        // collides in practice. The facet goes last, after the drawn value's own cell, and its bookkeeping last of all
        // — so a saved draw site reads top-down as "what it is, then where it is".
        if (value.Advance is { } advance) {
            WriteNested(
                options: options,
                propertyName: "advance",
                value: advance,
                writer: writer
            );
        }

        if (value.Dynamics is { } rowDynamics) {
            WriteNested(
                options: options,
                propertyName: "dynamics",
                value: rowDynamics,
                writer: writer
            );
        }

        if (value.Cycle is { } rowCycle) {
            WriteNested(
                options: options,
                propertyName: "cycle",
                value: rowCycle,
                writer: writer
            );
        }

        WriteTraits(
            options: options,
            row: value,
            writer: writer
        );

        if (value.Draw is { } draw) {
            WriteNested(
                options: options,
                propertyName: "draw",
                value: draw,
                writer: writer
            );
        }

        if (value.DrawCursor != 0L) {
            writer.WriteNumber(
                propertyName: "drawCursor",
                value: value.DrawCursor
            );
        }

        if (value.HistoryCursor != 0L) {
            writer.WriteNumber(
                propertyName: "historyCursor",
                value: value.HistoryCursor
            );
        }

        if (value.Visibility is { } visibility) { WriteNested(
            options: options,
            propertyName: "visibility",
            value: visibility,
            writer: writer
        ); }
        if (value.Knowledge is { } knowledge) { WriteNested(
            options: options,
            propertyName: "knowledge",
            value: knowledge,
            writer: writer
        ); }
        if (value.Phase is { } phase) {
            WriteNested(
                options: options,
                propertyName: "phase",
                value: phase,
                writer: writer
            );
        }
        if (value.PhaseOf is { } phaseOf) { writer.WriteString(
            propertyName: "phaseOf",
            value: phaseOf
        ); }
        if (value.ValuesFrom is { } valuesFrom) {
            writer.WriteString(
                propertyName: "valuesFrom",
                value: valuesFrom
            );
        }
        if (value.Domain is { } domain) {
            WriteNested(
                options: options,
                propertyName: "domain",
                value: domain,
                writer: writer
            );
        }
        if (value.Inverse is { } inverse) {
            WriteNested(
                options: options,
                propertyName: "inverse",
                value: inverse,
                writer: writer
            );
        }
        if (value.DrawnMasks is { Count: > 0 } drawnMasks) {
            writer.WritePropertyName(propertyName: "drawnMasks");
            writer.WriteStartArray();

            foreach (var mask in drawnMasks) {
                writer.WriteStringValue(value: mask.ToString());
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}
/// <summary>The row converter of the standalone state document — the shared shape and nothing more.</summary>
public sealed class StateRowJsonConverter : StateRowJsonConverter<StateRow> {
    /// <inheritdoc/>
    public override string Shape => "{\"name\":…,\"kind\":\"Int\"|\"Fixed\"|\"Bool\"|\"Text\",\"value\":… or \"cells\":[{\"key\":…,\"value\":…,\"provenance\":…,\"advance\":{…},\"dynamics\":{…},\"cycle\":{…},\"behavior\":\"None\",\"clock\":{…}}],\"clock\":{…},\"min\":…,\"max\":…,\"capacity\":…,\"overflow\":\"Refuse\"|\"Saturate\",\"evicts\":…,\"advance\":{…},\"dynamics\":{…},\"cycle\":{…},\"draw\":{…},\"drawCursor\":…,\"drawnMasks\":[…],\"historyCursor\":…,\"visibility\":{…},\"knowledge\":{…},\"phase\":{…},\"phaseOf\":…,\"valuesFrom\":…,\"domain\":{\"$type\":\"slot\"|\"keys\"|\"keysOf\"|\"cellsOf\"|\"ring\",…},\"inverse\":{\"tokens\":…,\"codes\":…}}";

    /// <inheritdoc/>
    protected override StateRow Create(StateRow row, RowMembers members, JsonSerializerOptions options) => row;
}
