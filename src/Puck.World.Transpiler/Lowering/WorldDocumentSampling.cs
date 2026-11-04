using System.Globalization;
using System.Text.Json.Nodes;
using Puck.Maths;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.World.Authoring;

namespace Puck.World.Transpiler.Lowering;

// The same seeded fixed-point sampler as a placement's Scatter region, exposed before ordinary map/filter
// lowering so authors can express masks, species and variation without writing another point generator.
internal static class WorldDocumentSampling {
    private static readonly string[] Arguments = ["cellSize", "width", "depth", "spacing", "radius", "seed", "worldSeed"];
    public static JsonArray Scatter(CallExpressionNode call, DocumentScope scope) {
        var values = new JsonNode?[Arguments.Length];
        var supplied = new bool[Arguments.Length];
        var positional = 0;
        DocumentEvaluationException Invalid(string message) => new($"scatterPoints: {message}", call.Span, PuckDiagnosticCodes.InvalidValue);
        foreach (var argument in call.Arguments) {
            var index = argument.Name is null ? positional++ : Array.IndexOf(Arguments, argument.Name);
            if (index < 0 || index >= Arguments.Length || supplied[index]) {
                throw Invalid($"unknown or repeated argument '{argument.Name ?? index.ToString(CultureInfo.InvariantCulture)}'.");
            }
            supplied[index] = true;
            values[index] = DocumentLowering.EvaluateValue(argument.Value, scope);
        }
        for (var i = 0; i < 4; i++) { if (!supplied[i]) { throw Invalid($"'{Arguments[i]}' is required."); } }
        long Whole(int index, long fallback) {
            if (!supplied[index]) { return fallback; }
            if (!long.TryParse(values[index]?.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) {
                throw Invalid($"'{Arguments[index]}' must be a whole number.");
            }
            return n;
        }
        if (!double.TryParse(values[0]?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cell) ||
            !float.IsFinite((float)cell) || cell <= 0) { throw Invalid("cellSize must be positive and finite."); }
        var width = Whole(1, 0); var depth = Whole(2, 0); var spacing = Whole(3, 0); var radius = Whole(4, 1); var seed = Whole(5, 0);
        if (width < 0 || depth < 0 || width > int.MaxValue || depth > int.MaxValue || spacing < 2 || spacing > int.MaxValue ||
            radius < 1 || radius > spacing / 2 || seed < 0 || seed > uint.MaxValue || Math.Max(width, depth) + spacing > int.MaxValue) {
            throw Invalid("width/depth must be nonnegative cells, spacing at least 2, radius in 1..spacing/2 and seed in the unsigned 32-bit range.");
        }
        ulong worldSeed = 0;
        if (supplied[6] && !ulong.TryParse(values[6]?.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out worldSeed)) {
            throw Invalid("worldSeed must be an unsigned 64-bit integer.");
        }
        var edge = FixedQ4816.FromDouble((float)cell);
        if (edge <= FixedQ4816.Zero) { throw Invalid("cellSize must be representable as a positive fixed-point edge."); }
        var count = CreationStampSampling.ScatterInstanceCeiling((int)width, (int)depth, (int)spacing);
        scope.Budget.Collection(count, call.Span);
        var points = CreationStampSampling.ResolveScatter(edge, (int)width, (int)depth,
            (int)spacing, (int)radius, (uint)seed, worldSeed);
        var result = new JsonArray();
        foreach (var point in points) {
            result.AppendNode(new JsonArray(JsonValue.Create((double)point.X), JsonValue.Create(0), JsonValue.Create((double)point.Z)));
        }
        return result;
    }
}
