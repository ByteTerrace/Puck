using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Puck.Maths;

namespace Puck.State;

/// <summary>The hand-written wire shape of a <see cref="StateRow"/>: the <c>value</c>-vs-<c>cells</c> exclusivity
/// and the decimal fixed-point spelling refuse by name rather than defaulting. A document project that derives its
/// own row type extends this converter: it claims its extra members by name, validates them beside the shared
/// ones, and writes them back at the two hook points that keep the canonical member order.</summary>
/// <typeparam name="TRow">The row type the converter reads and writes.</typeparam>
/// <remarks>Every nested object (a trait, a domain, a draw) is read and written through the options' own
/// resolver, so the same converter serves any context that registers the nested types.</remarks>
public abstract class StateRowJsonConverter<TRow> : JsonConverter<TRow> where TRow : StateRow {
    /// <summary>The wire members the shared shape parses, held as raw elements until the row's kind decides how
    /// each is read, plus whatever members the derived converter claimed.</summary>
    protected sealed class RowMembers {
        /// <summary>Gets the row's authored name.</summary>
        public string Name { get; internal set; } = string.Empty;
        /// <summary>Gets the raw <c>cycle</c> member, when authored.</summary>
        public JsonElement? Cycle { get; internal set; }
        /// <summary>Gets the raw <c>draw</c> member, when authored.</summary>
        public JsonElement? Draw { get; internal set; }
        /// <summary>Gets the raw <c>capacity</c> member, when authored.</summary>
        public JsonElement? Capacity { get; internal set; }
        /// <summary>Gets the raw <c>cells</c> member, when authored.</summary>
        public JsonElement? Cells { get; internal set; }
        /// <summary>Gets the members the derived converter claimed, keyed by wire name.</summary>
        public Dictionary<string, JsonElement> Claimed { get; } = new(comparer: StringComparer.Ordinal);
    }

    /// <summary>Gets the shape a refusal quotes for an unmapped or missing member.</summary>
    protected abstract string Shape { get; }

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
    /// <summary>Writes the derived row's boolean flags, between <c>nonNegative</c> and <c>evicts</c>.</summary>
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
        (element.Deserialize(jsonTypeInfo: (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)))
            ?? throw new JsonException(message: $"{context} must be an object."));
    /// <summary>Writes a nested object through the options' resolver.</summary>
    /// <typeparam name="T">The nested type.</typeparam>
    /// <param name="writer">The writer.</param>
    /// <param name="propertyName">The member name.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="options">The serializer options.</param>
    protected static void WriteNested<T>(Utf8JsonWriter writer, string propertyName, T value, JsonSerializerOptions options) {
        writer.WritePropertyName(propertyName: propertyName);
        JsonSerializer.Serialize(writer: writer, value: value, jsonTypeInfo: (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));
    }
    /// <summary>Reads a boolean member, refusing any other token.</summary>
    /// <param name="element">The element.</param>
    /// <param name="context">The member's spelling, for the refusal.</param>
    protected static bool RequireBool(JsonElement element, string context) => element.ValueKind switch {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new JsonException(message: $"{context} must be a boolean."),
    };

    private static string DescribeCellKind(CellKind cellKind) => cellKind switch {
        CellKind.Int => "int",
        CellKind.Fixed => "fixed",
        CellKind.Bool => "bool",
        CellKind.Text => "text",
        _ => throw new JsonException(message: $"CellKind '{cellKind}' has no JSON token."),
    };
    // A dynamics trait's y0/v0 ride the same per-kind spelling as an ordinary cell value (a decimal string via
    // FixedQ4816 for a fixed row, a plain JSON number for int) — never raw bits, matching StateCell.Value's own
    // wire convention.
    private static StateDynamics ReadDynamics(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new JsonException(message: $"{context} must be an object.");
        }

        string? row = null;
        JsonElement? y0 = null;
        JsonElement? v0 = null;
        var epochTick = 0L;

        foreach (var member in element.EnumerateObject()) {
            switch (member.Name) {
                case "row":
                    row = ((member.Value.ValueKind == JsonValueKind.String)
                        ? member.Value.GetString()
                        : null
                    );
                    break;
                case "y0":
                    y0 = member.Value;
                    break;
                case "v0":
                    v0 = member.Value;
                    break;
                case "epochTick":
                    epochTick = RequireInt64(
                        context: $"{context}.epochTick",
                        element: member.Value
                    );
                    break;
                default:
                    throw new JsonException(message: $"{context} contains unmapped member '{member.Name}'.");
            }
        }

        if (string.IsNullOrEmpty(value: row)) {
            throw new JsonException(message: $"{context} requires member 'row'.");
        }
        if (y0 is not { } y0Element) {
            throw new JsonException(message: $"{context} requires member 'y0'.");
        }
        if (v0 is not { } v0Element) {
            throw new JsonException(message: $"{context} requires member 'v0'.");
        }

        // y0/v0 are the follower's continuous state and ride raw FixedQ4816 bits whatever the carrying row's kind
        // (see StateDynamics), so they are authored in the fixed spelling — a decimal string — on every row.
        return new StateDynamics(
            Row: row,
            Y0: RequireNumeric(
                context: $"{context}.y0",
                element: y0Element,
                kind: CellKind.Fixed
            ),
            V0: RequireNumeric(
                context: $"{context}.v0",
                element: v0Element,
                kind: CellKind.Fixed
            ),
            EpochTick: epochTick
        );
    }
    private static StateCell ReadCell(CellKind cellKind, CellName key, JsonElement element, string context, StateAdvance? advance = null, string? provenance = null, StateDynamics? dynamics = null, StateCycle? cycle = null) => cellKind switch {
        CellKind.Text => new StateCell(
        Key: key,
        Text: RequireString(
            context: context,
            element: element
        ),
        Advance: advance,
        Provenance: provenance,
        Dynamics: dynamics,
        Cycle: cycle
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
        Cycle: cycle
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
        Cycle: cycle
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
                ? ReadNested<StateAdvance>(element: cellAdvanceElement, options: options, context: $"state row '{name}'.cells[{index}].advance")
                : null
            );
            var dynamics = ((cellDynamics is { } cellDynamicsElement)
                ? ReadDynamics(
                    context: $"state row '{name}'.cells[{index}].dynamics",
                    element: cellDynamicsElement
                )
                : null
            );

            var cycle = ((cellCycle is { } cellCycleElement)
                ? ReadNested<StateCycle>(element: cellCycleElement, options: options, context: $"state row '{name}'.cells[{index}].cycle")
                : null
            );

            cells.Add(item: ReadCell(
                cellKind: cellKind,
                key: cellKey,
                element: cellValueElement,
                context: $"state row '{name}'.cells[{index}].value",
                advance: advance,
                provenance: provenance,
                dynamics: dynamics,
                cycle: cycle
            ) with {
                Visibility = cellVisibility is { } cv ? ReadNested<StateVisibility>(element: cv, options: options, context: "visibility") : null,
                Observation = cellObservation is { } co ? ReadNested<StateObservation>(element: co, options: options, context: "observation") : null
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
            if (entry.ValueKind != JsonValueKind.String || !ClosedBitset256.TryParse(entry.GetString(), out var mask)) {
                throw new JsonException($"state row '{name}'.drawnMasks[{masks.Count}] must be a 64-digit hexadecimal string.");
            }
            masks.Add(mask);
        }

        return masks;
    }
    private static CellKind RequireCellKind(JsonElement element, string context) {
        var token = ((element.ValueKind == JsonValueKind.String)
            ? element.GetString()
            : null
        );

        return token switch {
            "int" => CellKind.Int,
            "fixed" => CellKind.Fixed,
            "bool" => CellKind.Bool,
            "text" => CellKind.Text,
            _ => throw new JsonException(message: $"{context} '{(token ?? "(absent)")}' must be one of 'int', 'fixed', 'bool', 'text'."),
        };
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
    private static void WriteDynamics(Utf8JsonWriter writer, string propertyName, StateDynamics dynamics) {
        writer.WritePropertyName(propertyName: propertyName);
        writer.WriteStartObject();
        writer.WriteString(
            propertyName: "row",
            value: dynamics.Row
        );
        // The follower's continuous state is fixed-native on every row kind, so it is written in the fixed spelling.
        WriteOptionalNumeric(
            writer: writer,
            propertyName: "y0",
            kind: CellKind.Fixed,
            raw: dynamics.Y0
        );
        WriteOptionalNumeric(
            writer: writer,
            propertyName: "v0",
            kind: CellKind.Fixed,
            raw: dynamics.V0
        );
        writer.WriteNumber(
            propertyName: "epochTick",
            value: dynamics.EpochTick
        );
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
        JsonElement? nonNegative = null;
        JsonElement? evicts = null;
        JsonElement? draw = null;
        JsonElement? drawCursor = null;
        JsonElement? drawnMasks = null;
        JsonElement? historyCursor = null;
        JsonElement? domain = null;
        string? phaseOf = null;
        string? valuesFrom = null;
        JsonElement? phase = null, visibility = null, knowledge = null;
        JsonElement? advance = null;
        JsonElement? dynamics = null;
        JsonElement? cycle = null;
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
                case "nonNegative":
                    nonNegative = JsonElement.ParseValue(reader: ref reader);
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
                default:
                    if ((property is not null) && ClaimsMember(name: property)) {
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
            throw new JsonException(message: $"state row '{name}' requires member 'kind' (int|fixed|bool|text).");
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
            (advance is not null) &&
            (capacity is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'advance' beside 'capacity' — advance is a scalar (slot) row trait; a keyed row's cells have no single value to accumulate.");
        }
        // A non-empty cells array only: the canonical writer emits "cells": [] for every non-slot row, including an
        // advance row declared with no value at all, so refusing on the member's mere presence would make that
        // legitimate shape refuse itself the first time it round-tripped through the wire codec.
        if (
            (advance is not null) &&
            (cells is { ValueKind: JsonValueKind.Array } authored) &&
            (authored.GetArrayLength() > 0)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'advance' beside a non-empty 'cells' array — advance is a scalar (slot) row trait; author it with 'value' or leave the row empty until the first explicit set.");
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
            (dynamics is not null) &&
            (capacity is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'dynamics' beside 'capacity' — dynamics is a scalar (slot) row trait; a keyed row's cells have no single value to ease.");
        }
        if (
            (dynamics is not null) &&
            (cells is { ValueKind: JsonValueKind.Array } dynamicsCells) &&
            (dynamicsCells.GetArrayLength() > 0)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'dynamics' beside a non-empty 'cells' array — dynamics is a scalar (slot) row trait; author it with 'value' or leave the row empty until the first explicit set.");
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
        Validate(members: members);
        if (
            (cycle is not null) &&
            (capacity is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'cycle' beside 'capacity' — cycle is a scalar (slot) row trait; a keyed row's cells each declare their own 'cycle'.");
        }
        if (
            (cycle is not null) &&
            (cells is { ValueKind: JsonValueKind.Array } cycleCells) &&
            (cycleCells.GetArrayLength() > 0)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'cycle' beside a non-empty 'cells' array — cycle is a scalar (slot) row trait; author it with 'value' or leave the row empty, and give a keyed row's cells their own 'cycle'.");
        }

        var cellKind = RequireCellKind(
            context: $"state row '{name}'.kind",
            element: kindElement
        );

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
            NonNegative: ((nonNegative is { } nonNegativeElement) && RequireBool(
                context: $"state row '{name}'.nonNegative",
                element: nonNegativeElement
            )),
            Evicts: ((evicts is { } evictsElement) && RequireBool(
                context: $"state row '{name}'.evicts",
                element: evictsElement
            )),
            Cells: ((value is { } valueElement)
            ? [ReadCell(
                        cellKind: cellKind,
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
            ? ReadNested<StateAdvance>(element: advanceElement, options: options, context: $"state row '{name}'.advance")
            : null),
            Draw: ((draw is { } drawElement)
            ? ReadNested<Draw>(element: drawElement, options: options, context: $"state row '{name}'.draw")
            : null),
            DrawCursor: ((drawCursor is { } drawCursorElement)
            ? RequireInt64(
                    context: $"state row '{name}'.drawCursor",
                    element: drawCursorElement
                )
            : 0L),
            Visibility: visibility is { } visibilityElement ? ReadNested<StateVisibility>(element: visibilityElement, options: options, context: "visibility") : null,
            Knowledge: knowledge is { } knowledgeElement ? ReadNested<StateKnowledge>(element: knowledgeElement, options: options, context: "knowledge") : null,
            Phase: phase is { } phaseElement ? ReadNested<StatePhase>(element: phaseElement, options: options, context: "phase") : null,
            PhaseOf: phaseOf,
            ValuesFrom: valuesFrom,
            Domain: ((domain is { } domainElement)
            ? ReadNested<StateDomain>(element: domainElement, options: options, context: $"state row '{name}'.domain")
            : null),
            DrawnMasks: ((drawnMasks is { } drawnMasksElement)
            ? ReadDrawnMasks(
                    element: drawnMasksElement,
                    name: name
                )
            : null),
            Dynamics: ((dynamics is { } dynamicsElement)
            ? ReadDynamics(
                    context: $"state row '{name}'.dynamics",
                    element: dynamicsElement
                )
            : null),
            Cycle: ((cycle is { } cycleElement)
            ? ReadNested<StateCycle>(element: cycleElement, options: options, context: $"state row '{name}'.cycle")
            : null),
            HistoryCursor: ((historyCursor is { } historyCursorElement)
            ? RequireInt64(
                    context: $"state row '{name}'.historyCursor",
                    element: historyCursorElement
                )
            : 0L)
        );

        return Create(row: row, members: members, options: options);
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
            value: DescribeCellKind(cellKind: value.Kind)
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

        if (value.NonNegative) {
            writer.WriteBoolean(
                propertyName: "nonNegative",
                value: true
            );
        }

        WriteFlags(writer: writer, row: value);

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
        if (value.IsSlot && (value.Cells is { Count: 1 })) {
            WriteCellValue(
                writer: writer,
                propertyName: "value",
                kind: value.Kind,
                cell: value.Cells[0]
            );
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
                    WriteNested(writer: writer, propertyName: "advance", value: cellAdvance, options: options);
                }

                if (cell.Dynamics is { } cellDynamics) {
                    WriteDynamics(
                        dynamics: cellDynamics,
                        propertyName: "dynamics",
                        writer: writer
                    );
                }

                if (cell.Cycle is { } cellCycle) {
                    WriteNested(writer: writer, propertyName: "cycle", value: cellCycle, options: options);
                }

                if (cell.Visibility is { } cellVisibility) { WriteNested(writer: writer, propertyName: "visibility", value: cellVisibility, options: options); }
                if (cell.Observation is { } observation) { WriteNested(writer: writer, propertyName: "observation", value: observation, options: options); }
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
            WriteNested(writer: writer, propertyName: "advance", value: advance, options: options);
        }

        if (value.Dynamics is { } rowDynamics) {
            WriteDynamics(
                dynamics: rowDynamics,
                propertyName: "dynamics",
                writer: writer
            );
        }

        if (value.Cycle is { } rowCycle) {
            WriteNested(writer: writer, propertyName: "cycle", value: rowCycle, options: options);
        }

        WriteTraits(writer: writer, row: value, options: options);

        if (value.Draw is { } draw) {
            WriteNested(writer: writer, propertyName: "draw", value: draw, options: options);
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

        if (value.Visibility is { } visibility) { WriteNested(writer: writer, propertyName: "visibility", value: visibility, options: options); }
        if (value.Knowledge is { } knowledge) { WriteNested(writer: writer, propertyName: "knowledge", value: knowledge, options: options); }
        if (value.Phase is { } phase) {
            WriteNested(writer: writer, propertyName: "phase", value: phase, options: options);
        }
        if (value.PhaseOf is { } phaseOf) { writer.WriteString("phaseOf", phaseOf); }
        if (value.ValuesFrom is { } valuesFrom) {
            writer.WriteString("valuesFrom", valuesFrom);
        }
        if (value.Domain is { } domain) {
            WriteNested(writer: writer, propertyName: "domain", value: domain, options: options);
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
    protected override string Shape => "{\"name\":…,\"kind\":\"int\"|\"fixed\"|\"bool\"|\"text\",\"value\":… or \"cells\":[{\"key\":…,\"value\":…,\"provenance\":…,\"advance\":{…},\"dynamics\":{…},\"cycle\":{…}}],\"min\":…,\"max\":…,\"capacity\":…,\"nonNegative\":…,\"evicts\":…,\"advance\":{…},\"dynamics\":{…},\"cycle\":{…},\"draw\":{…},\"drawCursor\":…,\"drawnMasks\":[…],\"domain\":{\"$type\":\"slot\"|\"keys\"|\"keysOf\"|\"cellsOf\"|\"ring\",…}}";

    /// <inheritdoc/>
    protected override StateRow Create(StateRow row, RowMembers members, JsonSerializerOptions options) => row;
}
