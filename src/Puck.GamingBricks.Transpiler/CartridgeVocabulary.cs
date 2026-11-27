using Puck.Transpiler.Lowering;
using Puck.Transpiler.Units;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using System.Text.Json.Nodes;

namespace Puck.GamingBricks.Transpiler;

/// <summary>The <c>puck.cartridge.v1</c> answers to the questions generic value lowering cannot settle for
/// itself.</summary>
public sealed class CartridgeVocabulary : IDocumentVocabulary {
    // Positional spellings for the call-form actions, so `map(r, c, tile)` reads as well as the fully named form.
    private static readonly Dictionary<string, string[]> CallArguments = new(comparer: StringComparer.Ordinal) {
        ["blend"] = ["surface", "weight"],
        ["blit"] = ["row", "column", "screen"],
        ["fade"] = ["amount", "toward"],
        ["map"] = ["row", "column", "tile", "palette", "surface"],
        ["play"] = ["sound", "rate"],
        ["plot"] = ["row", "column", "colour"],
    };

    /// <summary>The shared instance; the vocabulary is a pure lookup and carries no per-pass state.</summary>
    public static CartridgeVocabulary Instance { get; } = new();

    private static readonly HashSet<string> ExpressionFields = new(comparer: StringComparer.Ordinal) {
        "angle", "behindBackground", "centreX", "centreY", "clear", "flipX", "flipY", "palette", "scale", "scrollX",
        "scrollY", "tile", "turn", "visible", "x", "y",
    };

    /// <inheritdoc />
    /// <remarks>Cartridge numeric fields use literal hardware units and admit no unit suffix; suffixed values are PUCK024.</remarks>
    public UnitDimension ClassifyField(string fieldKey) => UnitDimension.None;
    /// <inheritdoc />
    public string? NameCallArgument(string callName, int positionalIndex) {
        ArgumentNullException.ThrowIfNull(callName);

        if (
            !CallArguments.TryGetValue(
            key: callName,
            value: out var names
        ) ||
            (positionalIndex >= names.Length)
        ) {
            return null;
        }

        return names[positionalIndex];
    }
    /// <summary>Returns whether a member of a document object is one of the fields a cartridge evaluates at run time
    /// (a sprite's position, a panel's visibility, a scroll), which carry their expression's infix spelling.</summary>
    /// <param name="fieldKey">The member's name.</param>
    /// <returns><see langword="true"/> for an expression field.</returns>
    public static bool IsExpressionField(string fieldKey) => ExpressionFields.Contains(item: fieldKey);
    /// <inheritdoc />
    /// <remarks>Only an expression that reads machine state is lowered here, since generic lowering cannot evaluate
    /// it; a value compile-time bindings decide lowers generically wherever it stands, so a <c>let</c> record whose
    /// member shares an expression field's name (<c>{ x: 64 }</c>) keeps its value. The emitter spells the expression
    /// fields of the document itself afterwards (<see cref="CartridgeDocumentEmitter"/>).</remarks>
    public bool TryLowerValue(ExpressionNode expression, DocumentScope scope, string? fieldKey, out JsonNode? value) {
        value = null;
        if (
            (fieldKey is null) ||
            !ExpressionFields.Contains(item: fieldKey) ||
            !CartridgeOperand.ReadsState(expression: DocumentLowering.InlineFunctions(expression: expression, scope: scope), scope: scope)
        ) { return false; }
        value = CartridgeOperand.FromExpression(
            expression: expression,
            reason: out var reason,
            scope: scope
        );
        if (reason is not null) {
            scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.SemanticValidation,
            message: reason,
            span: expression.Span
        );
        }
        return true;
    }
}
