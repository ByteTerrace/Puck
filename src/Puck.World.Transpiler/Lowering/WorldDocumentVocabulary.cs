using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Units;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Lowering;

/// <summary>The <c>puck.world.definition.v1</c> answers to the questions generic value lowering cannot settle for
/// itself.</summary>
/// <param name="constructs">The described vocabulary the parser and the lowering ask; the shipped
/// <see cref="WorldConstructs.Table"/> when omitted.</param>
/// <remarks>The description is a constructor argument rather than a static read so a law can hand one pass a
/// description it invented and watch every reader of that pass follow it.</remarks>
public sealed class WorldDocumentVocabulary(WorldConstructTable? constructs = null) : IDocumentVocabulary {
    /// <summary>The canonical schema URI for world definitions.</summary>
    public const string Schema = "puck.world.definition.v1";

    /// <summary>The shared instance; the vocabulary is a pure lookup and carries no per-pass state.</summary>
    public static WorldDocumentVocabulary Instance { get; } = new();

    /// <summary>Gets the described constructs this pass parses and lowers against.</summary>
    public WorldConstructTable Constructs { get; } = (constructs ?? WorldConstructs.Table);

    /// <inheritdoc />
    /// <remarks>Which identifiers these are is one row of <see cref="Constructs"/> each, so the parser delegates
    /// to exactly the dialects the vocabulary describes.</remarks>
    public bool IsEmbeddedLanguage(string identifier) => Constructs.IsEmbeddedLanguage(identifier: identifier);
    /// <inheritdoc />
    /// <remarks>A bare flag is a described member of <see cref="WorldMemberKind.Flag"/> written where a modifier
    /// stands, so the parser reads exactly the flags the table carries.</remarks>
    public bool IsBareModifier(string keyword, string modifier) => (
        Constructs.TryGet(
        construct: out var construct,
        keyword: keyword
    ) &&
        construct!.TryGetMember(
        member: out var member,
        name: modifier
    ) &&
        (member!.Kind == WorldMemberKind.Flag) &&
        (member.Position is WorldMemberPosition.Modifier or WorldMemberPosition.Cell)
    );

    /// <inheritdoc />
    public bool TryLowerValue(ExpressionNode expression, DocumentScope scope, string? fieldKey, out System.Text.Json.Nodes.JsonNode? value) {
        if (expression is IndexExpressionNode { Target: IdentifierExpressionNode { Name: var familyName }, Index: var indexExpr }) {
            var families = WorldDocumentEmitter.GetOrCreateStateFamilies(scope: scope);
            if (families.TryGetValue(key: familyName, value: out var family)) {
                if (indexExpr is LiteralExpressionNode lit && lit.Value is int or long or double or decimal) {
                    var idx = Convert.ToInt32(value: lit.Value);
                    if (idx < 0 || idx >= family.Size) {
                        scope.Diagnostics.ReportError(
                            code: Puck.Transpiler.Diagnostics.PuckDiagnosticCodes.FamilyIndexOutOfBounds,
                            message: $"Index '{idx}' is out of bounds for family '{familyName}' of size {family.Size}",
                            span: expression.Span
                        );
                    }
                    value = System.Text.Json.Nodes.JsonValue.Create(value: $"{familyName}{idx}");
                    return true;
                }

                var idxText = (indexExpr is IdentifierExpressionNode idNode ? idNode.Name : DocumentLowering.KeyText(node: DocumentLowering.LowerValue(expr: indexExpr, scope: scope)) ?? indexExpr.ToString());
                value = System.Text.Json.Nodes.JsonValue.Create(value: $"{familyName}[{idxText}]");
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <inheritdoc />
    public UnitDimension ClassifyField(string fieldKey) => WorldDocumentEmitterUnits.Classify(fieldKey: fieldKey);
    /// <inheritdoc />
    /// <remarks>Only the camera-program ops take positional arguments; everything else is written by name and falls
    /// through to the generic <c>arg&lt;n&gt;</c> spelling.</remarks>
    public string? NameCallArgument(string callName, int positionalIndex) {
        if (string.Equals(
            a: callName,
            b: "orbit",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            return positionalIndex switch {
                0 => "distance",
                1 => "pitch",
                2 => "yaw",
                _ => null,
            };
        }

        if (string.Equals(
            a: callName,
            b: "fieldOfView",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            return "fieldOfViewRadians";
        }

        return null;
    }
}
