using Puck.Transpiler.Lowering;
using Puck.Transpiler.Units;

namespace Puck.GamingBricks.Transpiler;

/// <summary>The <c>puck.cartridge.v1</c> answers to the questions generic value lowering cannot settle for
/// itself.</summary>
public sealed class CartridgeVocabulary : IDocumentVocabulary {
    /// <summary>The document's schema string.</summary>
    public const string Schema = "puck.cartridge.v1";

    // Positional spellings for the call-form actions, so `map(r, c, tile)` reads as well as the fully named form.
    private static readonly Dictionary<string, string[]> s_callArguments = new(StringComparer.Ordinal) {
        ["blend"] = ["surface", "weight"],
        ["blit"] = ["row", "column", "screen"],
        ["fade"] = ["amount", "toward"],
        ["map"] = ["row", "column", "tile", "palette"],
        ["play"] = ["sound", "rate"],
        ["plot"] = ["row", "column", "colour"],
    };

    /// <summary>The shared instance; the vocabulary is a pure lookup and carries no per-pass state.</summary>
    public static CartridgeVocabulary Instance { get; } = new();

    /// <inheritdoc />
    /// <remarks>Every value in a cartridge is an unsigned byte the hardware reads literally, so no field admits a
    /// unit suffix and every one of them is PUCK024.</remarks>
    public UnitDimension ClassifyField(string fieldKey) => UnitDimension.None;

    /// <inheritdoc />
    public string? NameCallArgument(string callName, int positionalIndex) {
        ArgumentNullException.ThrowIfNull(callName);

        if (!s_callArguments.TryGetValue(key: callName, value: out var names) || (positionalIndex >= names.Length)) {
            return null;
        }

        return names[positionalIndex];
    }
}
