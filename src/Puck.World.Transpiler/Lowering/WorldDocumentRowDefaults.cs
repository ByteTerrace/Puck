using System.Text.Json.Nodes;

namespace Puck.World.Transpiler.Lowering;

/// <summary>The values the <c>shape</c> and <c>placement</c> row sugar elides, and the exact node each elision
/// stands for. The emitter fills from this table and the decompiler elides against it, so the two sides cannot
/// disagree about what an omitted field means.</summary>
/// <remarks><c>ShapeDocument.Group</c> is not in this table: the authored corpus omits the key on some shapes and
/// carries it on others, so filling a default on lowering would add a key the source does not have. (The engine
/// itself cannot tell the two apart — <c>CreationCanonicalizer</c> normalizes an absent <c>group</c> and an
/// explicit <c>0</c> to the same value — so the constraint is round-trip fidelity, not engine semantics.) It
/// passes through both directions like <c>material</c>/<c>parent</c> instead.</remarks>
public static class WorldDocumentRowDefaults {
    /// <summary>The shape fields that carry a default, in the order the emitter fills them.</summary>
    public static IReadOnlyList<string> ShapeKeys { get; } = ["id", "blend", "smooth", "rotation", "scale"];

    /// <summary>The placement fields that carry a default.</summary>
    public static IReadOnlyList<string> PlacementKeys { get; } = ["yawDegrees", "scale"];

    /// <summary>Returns the default node for a shape field.</summary>
    /// <param name="key">The field name, from <see cref="ShapeKeys"/>.</param>
    /// <param name="index">The shape's 0-based position in its collection, which is the default <c>id</c>.</param>
    /// <returns>The default node, or <see langword="null"/> when the field carries no default.</returns>
    public static JsonNode? ShapeDefault(string key, int index) => key switch {
        "id" => JsonValue.Create(index),
        "blend" => JsonValue.Create("Union"),
        "smooth" => JsonValue.Create(0),
        // The literal array only. A rotation authored as a binding reference string is a different value that
        // `CreationCanonicalizer.NormalizeRotation` passes through untouched, and never elides to this.
        "rotation" => new JsonArray(0, 0, 0, 1),
        "scale" => new JsonArray(1, 1, 1),
        _ => null,
    };

    /// <summary>Returns the default node for a placement field.</summary>
    /// <param name="key">The field name, from <see cref="PlacementKeys"/>.</param>
    /// <returns>The default node, or <see langword="null"/> when the field carries no default.</returns>
    public static JsonNode? PlacementDefault(string key) => key switch {
        // WorldPlacement.Scale is a uniform float, never a vector.
        "yawDegrees" => JsonValue.Create(0),
        "scale" => JsonValue.Create(1),
        _ => null,
    };

    /// <summary>The value the bare <c>solid</c> flag stands for. <c>WorldSolid</c> carries exactly one field, so
    /// there is nothing else a default could omit.</summary>
    /// <returns>A fresh <c>{ "margin": 0 }</c> object.</returns>
    public static JsonObject BareSolid() => new() { ["margin"] = 0 };

    /// <summary>Returns a value indicating whether a solid facet is exactly what the bare <c>solid</c> flag spells.</summary>
    /// <param name="solid">The solid facet.</param>
    /// <returns><see langword="true"/> when the facet is the bare form.</returns>
    public static bool IsBareSolid(JsonObject solid) {
        ArgumentNullException.ThrowIfNull(solid);
        return (solid.Count == 1) && (solid["margin"] is JsonValue margin) && IsNumberEqualTo(margin, 0.0);
    }

    /// <summary>Returns a value indicating whether a field's value is exactly its default, so the sugar may elide it.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="value">The value the document carries.</param>
    /// <param name="index">The row's 0-based position, for the shape <c>id</c> default.</param>
    /// <param name="shape"><see langword="true"/> for the shape table, <see langword="false"/> for the placement table.</param>
    /// <returns><see langword="true"/> when the value matches the default exactly.</returns>
    public static bool IsDefaultValue(string key, JsonNode? value, int index, bool shape) {
        var expected = shape ? ShapeDefault(key, index) : PlacementDefault(key);
        return (expected is not null) && (value is not null) && Matches(expected, value);
    }

    private static bool Matches(JsonNode expected, JsonNode actual) {
        if (expected is JsonArray expectedArray) {
            if (actual is not JsonArray actualArray || (actualArray.Count != expectedArray.Count)) {
                return false;
            }
            for (var i = 0; i < expectedArray.Count; i++) {
                if (!Matches(expectedArray[i]!, actualArray[i]!)) {
                    return false;
                }
            }
            return true;
        }

        if (expected is not JsonValue expectedValue || actual is not JsonValue actualValue) {
            return false;
        }
        if (expectedValue.TryGetValue<string>(out var expectedText)) {
            return actualValue.TryGetValue<string>(out var actualText) && string.Equals(expectedText, actualText, StringComparison.Ordinal);
        }
        return TryGetNumber(expectedValue, out var expectedNumber) && IsNumberEqualTo(actualValue, expectedNumber);
    }

    // Which CLR type a JsonValue converts to depends on how it was constructed: a node parsed from JSON text
    // converts to any numeric type, a node built from a C# literal only to that same type.
    private static bool TryGetNumber(JsonValue value, out double number) {
        if (value.TryGetValue<long>(out var l)) {
            number = l;
            return true;
        }
        if (value.TryGetValue<double>(out var d)) {
            number = d;
            return true;
        }
        if (value.TryGetValue<int>(out var i)) {
            number = i;
            return true;
        }
        number = 0;
        return false;
    }

    private static bool IsNumberEqualTo(JsonValue value, double expected) =>
        TryGetNumber(value, out var number) && (number == expected);
}
