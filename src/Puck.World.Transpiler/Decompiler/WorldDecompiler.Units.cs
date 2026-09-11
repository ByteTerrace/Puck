using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Decompiler;

public static partial class WorldDecompiler {
    /// <summary>Returns the unit suffix a field's numeric value prints with, or <see langword="null"/> when it
    /// prints bare. The dimension comes from <see cref="WorldDocumentEmitterUnits.Classify"/> — the same table the
    /// emitter converts against — so a field that reads as degrees or seconds on the way in prints that way on the
    /// way out rather than one hard-coded field name doing so and its siblings not.</summary>
    /// <remarks>Only identity-conversion units are printed: a suffix the emitter would scale by would round-trip a
    /// different number than the document holds.</remarks>
    private static string? UnitSuffixFor(string fieldKey, JsonValue value) {
        if (value.TryGetValue<string>(out _) || value.TryGetValue<bool>(out _)) {
            return null;
        }
        return WorldDocumentEmitterUnits.Classify(fieldKey) switch {
            WorldDocumentEmitterUnits.FieldDimensionKind.DegreesNative => "deg",
            WorldDocumentEmitterUnits.FieldDimensionKind.Seconds => "s",
            _ => null,
        };
    }
}
