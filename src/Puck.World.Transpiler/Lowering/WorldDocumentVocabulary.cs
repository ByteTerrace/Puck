using Puck.Transpiler.Lowering;
using Puck.Transpiler.Units;

namespace Puck.World.Transpiler.Lowering;

/// <summary>The <c>puck.world.def.v1</c> answers to the questions generic value lowering cannot settle for
/// itself.</summary>
public sealed class WorldDocumentVocabulary : IDocumentVocabulary {
    /// <summary>The shared instance; the vocabulary is a pure lookup and carries no per-pass state.</summary>
    public static WorldDocumentVocabulary Instance { get; } = new();

    /// <inheritdoc />
    public UnitDimension ClassifyField(string fieldKey) => WorldDocumentEmitterUnits.Classify(fieldKey: fieldKey);

    /// <inheritdoc />
    /// <remarks>Only the camera-program ops take positional arguments; everything else is written by name and falls
    /// through to the generic <c>arg&lt;n&gt;</c> spelling.</remarks>
    public string? NameCallArgument(string callName, int positionalIndex) {
        if (string.Equals(a: callName, b: "orbit", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return positionalIndex switch {
                0 => "distance",
                1 => "pitch",
                2 => "yaw",
                _ => null,
            };
        }

        if (string.Equals(a: callName, b: "fov", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return "fieldOfViewRadians";
        }

        return null;
    }
}
