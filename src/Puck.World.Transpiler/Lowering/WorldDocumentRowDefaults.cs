using System.Text.Json.Nodes;
using Puck.World.Authoring;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Lowering;

/// <summary>The exact node each default the <c>shape</c> and <c>placement</c> row sugar elides stands for. Which
/// members carry a default is their construct's own description; this resolves each one to a node, so the emitter
/// that fills it and the decompiler that elides against it cannot disagree.</summary>
/// <remarks>A member written at its default and the member omitted mean the same, so both lower to the one
/// canonical row that carries it (the spelling-equivalence corpus holds the pair). <c>parent</c> has no default
/// value and passes through as written.</remarks>
public static class WorldDocumentRowDefaults {
    // A described default is the canonical JSON text of the node it stands for, so `{"scale": [1, 1, 1]}` and
    // `{"scale": 1}` are told apart by the text rather than by a switch here. `$index` is the one positional
    // default: a shape's own place in its array.
    private static JsonNode? Resolve(string keyword, string key, int index) {
        if (DefaultText(key: key, keyword: keyword) is not { } text) {
            return null;
        }
        if (string.Equals(
            a: text,
            b: WorldConstructMember.RowIndexDefault,
            comparisonType: StringComparison.Ordinal
        )) {
            return JsonValue.Create(value: index);
        }

        return JsonNode.Parse(json: text);
    }

    // The emitter asks for a row's defaults once per row it writes, so each construct's description is read once
    // per key rather than searched again for every row.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Keyword, string Key), string?> Defaults = new();

    private static string? DefaultText(string keyword, string key) => Defaults.GetOrAdd(
        key: (keyword, key),
        valueFactory: static entry => (WorldConstructs.Table.TryGet(
            construct: out var construct,
            keyword: entry.Keyword
        )
            ? construct!.DefaultFor(key: entry.Key)
            : throw new InvalidOperationException(message: $"'{entry.Keyword}' is not a described construct.")
        )
    );
    private static IReadOnlyList<string> KeysWithDefaults(string keyword) => (WorldConstructs.Table.TryGet(
        construct: out var construct,
        keyword: keyword
    )
        ? [.. construct!.Members
            .Where(predicate: static member => (member.Default is not null))
            .SelectMany(selector: static member => member.DocumentKeys)]
        : throw new InvalidOperationException(message: $"'{keyword}' is not a described construct.")
    );

    /// <summary>The placement fields that carry a default.</summary>
    public static IReadOnlyList<string> PlacementKeys { get; } = KeysWithDefaults(keyword: "placement");
    /// <summary>The shape fields that carry a default.</summary>
    public static IReadOnlyList<string> ShapeKeys { get; } = KeysWithDefaults(keyword: "shape");

    private static bool IsNumberEqualTo(JsonValue value, double expected) =>
        (TryGetNumber(
            number: out var number,
            value: value
        ) && (number == expected));
    private static bool Matches(JsonNode expected, JsonNode actual) {
        if (expected is JsonArray expectedArray) {
            if (
                (actual is not JsonArray actualArray) ||
                (actualArray.Count != expectedArray.Count)
            ) {
                return false;
            }
            for (var i = 0; (i < expectedArray.Count); i++) {
                if (!Matches(
                    expectedArray[i]!,
                    actualArray[i]!
                )) {
                    return false;
                }
            }
            return true;
        }

        if (
            (expected is not JsonValue expectedValue) ||
            (actual is not JsonValue actualValue)
        ) {
            return false;
        }
        if (expectedValue.TryGetValue<string>(value: out var expectedText)) {
            return (
                actualValue.TryGetValue<string>(value: out var actualText) &&
                string.Equals(
                a: expectedText,
                b: actualText,
                comparisonType: StringComparison.Ordinal
            )
            );
        }
        return (
            TryGetNumber(
            number: out var expectedNumber,
            value: expectedValue
        ) &&
            IsNumberEqualTo(
            expected: expectedNumber,
            value: actualValue
        )
        );
    }
    // Which CLR type a JsonValue converts to depends on how it was constructed: a node parsed from JSON text
    // converts to any numeric type, a node built from a C# literal only to that same type.
    private static bool TryGetNumber(JsonValue value, out double number) {
        if (value.TryGetValue<long>(value: out var l)) {
            number = l;
            return true;
        }
        if (value.TryGetValue<double>(value: out var d)) {
            number = d;
            return true;
        }
        if (value.TryGetValue<int>(value: out var i)) {
            number = i;
            return true;
        }
        if (value.TryGetValue<decimal>(value: out var exact)) {
            number = ((double)exact);
            return true;
        }
        number = 0;
        return false;
    }

    /// <summary>Writes a literal rotation quaternion in its one canonical sign. A quaternion and its negation are the
    /// same rotation, so the canonical one has a positive <c>w</c>, or, when <c>w</c> is zero, a positive first
    /// nonzero component of <c>x</c>, <c>y</c>, <c>z</c>. Anything other than four numbers is left as written.</summary>
    /// <param name="rotation">The lowered <c>[x, y, z, w]</c> array; rewritten in place.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rotation"/> is <see langword="null"/>.</exception>
    public static void CanonicalizeRotation(JsonArray rotation) {
        ArgumentNullException.ThrowIfNull(argument: rotation);

        if ((rotation.Count != 4) || !rotation.All(predicate: static component => ((component is JsonValue value) && TryGetNumber(number: out _, value: value)))) {
            return;
        }

        var components = rotation.Select(selector: static component => {
            _ = TryGetNumber(number: out var number, value: ((JsonValue)component!));

            return number;
        }).ToArray();
        var sign = ((components[3] != 0.0) ? components[3] : components.Take(count: 3).FirstOrDefault(predicate: static component => (component != 0.0)));

        if (sign >= 0.0) {
            return;
        }

        for (var index = 0; (index < 4); index++) {
            // Negating zero writes 0, never -0, so a component the author wrote as 0 stays 0; each other component
            // keeps the numeric type it was lowered as, so its spelling is the authored one less its sign.
            var value = ((JsonValue)rotation[index]!);

            rotation[index] = ((components[index] == 0.0)
                ? value.DeepClone()
                : (value.TryGetValue<long>(value: out var whole)
                    ? JsonValue.Create(value: -whole)
                    : (value.TryGetValue<decimal>(value: out var exact) ? JsonValue.Create(value: -exact) : JsonValue.Create(value: -components[index]))));
        }
    }
    /// <summary>Returns a number the model reads as a single-precision float in the float's one spelling: the shortest
    /// decimal that reads back as the same float, so <c>30.24</c> and <c>30.240000000000002</c> are one value.</summary>
    /// <param name="value">The lowered value.</param>
    /// <returns>The canonical number, or <paramref name="value"/> itself when it is already canonical, is not a
    /// number, or has no finite float.</returns>
    public static JsonNode? CanonicalFloat(JsonNode? value) {
        if ((value is not JsonValue scalar) || !TryGetNumber(number: out _, value: scalar)) {
            return value;
        }
        var written = scalar.ToJsonString();

        if (!float.TryParse(s: written, style: System.Globalization.NumberStyles.Float, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var single) || !float.IsFinite(f: single) ||
            !Puck.Transpiler.Lowering.DecimalValues.TryParseEvery(text: single.ToString(format: "R", provider: System.Globalization.CultureInfo.InvariantCulture), number: out var shortest)) {
            return value;
        }
        var canonical = JsonValue.Create(value: shortest);

        return (string.Equals(a: canonical.ToJsonString(), b: written, comparisonType: StringComparison.Ordinal) ? value : canonical);
    }
    /// <summary>Lists a convex profile's vertices from their canonical start (<see cref="Puck.SignedDistance.SdfPrismProfile.CanonicalStart"/>),
    /// so one polygon listed from another vertex is one profile. Any other profile, or vertices that are not number
    /// pairs, is left as written.</summary>
    /// <param name="profile">The lowered profile object; its <c>vertices</c> member is rewritten in place.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    public static void CanonicalizeProfile(JsonObject profile) {
        ArgumentNullException.ThrowIfNull(argument: profile);

        if (!string.Equals(a: profile["kind"]?.ToString(), b: nameof(Puck.SignedDistance.SdfPrismProfileKind.Convex), comparisonType: StringComparison.Ordinal) ||
            (profile["vertices"] is not JsonArray vertices)) {
            return;
        }
        var points = new List<System.Numerics.Vector2>(capacity: vertices.Count);

        foreach (var vertex in vertices) {
            if ((vertex is not JsonArray { Count: 2 } pair) || (pair[0] is not JsonValue x) || (pair[1] is not JsonValue y) ||
                !TryGetNumber(number: out var px, value: x) || !TryGetNumber(number: out var py, value: y)) {
                return;
            }
            points.Add(item: new System.Numerics.Vector2(x: ((float)px), y: ((float)py)));
        }
        var start = Puck.SignedDistance.SdfPrismProfile.CanonicalStart(vertices: points);

        if (start == 0) {
            return;
        }
        var listed = vertices.Select(selector: static vertex => vertex!.DeepClone()).ToArray();

        vertices.Clear();
        for (var index = 0; (index < listed.Length); index++) {
            vertices.Add(item: listed[((start + index) % listed.Length)]);
        }
    }
    /// <summary>Writes each default a shape row omits, so a whole shape and the same shape spelling its defaults are one
    /// row. A row's <c>id</c> defaults to its own position in <paramref name="shapes"/>.</summary>
    /// <param name="shapes">The lowered <c>shapes</c> array of a document that composes nothing; its rows are rewritten
    /// in place.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shapes"/> is <see langword="null"/>.</exception>
    public static void CompleteShapes(JsonArray shapes) {
        ArgumentNullException.ThrowIfNull(argument: shapes);

        for (var index = 0; (index < shapes.Count); index++) {
            if (shapes[index] is not JsonObject shape) {
                continue;
            }
            foreach (var key in ShapeKeys) {
                if (!shape.ContainsKey(propertyName: key)) {
                    shape[key] = ShapeDefault(
                        index: index,
                        key: key
                    );
                }
            }
        }
    }
    /// <summary>Removes each shape member written at the zero that means the member is absent
    /// (<see cref="ShapeDocument.ZeroIsAbsent"/>), so the member at zero and the member omitted are one row.</summary>
    /// <param name="shape">The lowered shape row; rewritten in place.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    public static void OmitAbsentShapeMembers(JsonObject shape) {
        ArgumentNullException.ThrowIfNull(argument: shape);

        foreach (var key in ShapeDocument.ZeroIsAbsent) {
            if ((shape[key] is JsonValue value) && IsNumberEqualTo(expected: 0.0, value: value)) {
                _ = shape.Remove(propertyName: key);
            }
        }
    }
    /// <summary>The value the bare <c>solid</c> flag stands for. <c>WorldSolid</c> carries exactly one field, so
    /// there is nothing else a default could omit.</summary>
    /// <returns>A fresh <c>{ "margin": 0 }</c> object.</returns>
    public static JsonObject BareSolid() => new() { ["margin"] = 0 };
    /// <summary>Returns a value indicating whether a solid facet is exactly what the bare <c>solid</c> flag spells.</summary>
    /// <param name="solid">The solid facet.</param>
    /// <returns><see langword="true"/> when the facet is the bare form.</returns>
    public static bool IsBareSolid(JsonObject solid) {
        ArgumentNullException.ThrowIfNull(solid);
        return (
            (solid.Count == 1) &&
            (solid["margin"] is JsonValue margin) &&
            IsNumberEqualTo(
            expected: 0.0,
            value: margin
        )
        );
    }
    /// <summary>Returns a value indicating whether a field's value is exactly its default, so the sugar may elide it.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="value">The value the document carries.</param>
    /// <param name="index">The row's 0-based position, for the shape <c>id</c> default.</param>
    /// <param name="shape"><see langword="true"/> for the shape table, <see langword="false"/> for the placement table.</param>
    /// <returns><see langword="true"/> when the value matches the default exactly.</returns>
    public static bool IsDefaultValue(string key, JsonNode? value, int index, bool shape) {
        var expected = (shape
            ? ShapeDefault(
                index: index,
                key: key
            )
            : PlacementDefault(key: key)
        );

        return (
            (expected is not null) &&
            (value is not null) &&
            Matches(
            actual: value,
            expected: expected
        )
        );
    }
    /// <summary>Returns the default node for a placement field.</summary>
    /// <param name="key">The field name, from <see cref="PlacementKeys"/>.</param>
    /// <returns>The default node, or <see langword="null"/> when the field carries no default.</returns>
    public static JsonNode? PlacementDefault(string key) => Resolve(
        index: 0,
        key: key,
        keyword: "placement"
    );
    /// <summary>Returns the default node for a shape field.</summary>
    /// <param name="key">The field name, from <see cref="ShapeKeys"/>.</param>
    /// <param name="index">The shape's 0-based position in its collection, which is the default <c>id</c>.</param>
    /// <returns>The default node, or <see langword="null"/> when the field carries no default.</returns>
    /// <remarks>The literal <c>rotation</c> array only. A rotation authored as a binding reference string is a
    /// different value that <c>CreationCanonicalizer.NormalizeRotation</c> passes through untouched, and never
    /// elides to this.</remarks>
    public static JsonNode? ShapeDefault(string key, int index) => Resolve(
        index: index,
        key: key,
        keyword: "shape"
    );
}
