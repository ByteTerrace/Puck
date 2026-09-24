using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Units;
using Puck.World.Transpiler.Vocabulary;
using Puck.Transpiler.Modules;

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
        if (expression is AssetExpressionNode asset) {
            value = null;
            if ((scope.Annotations.GetValueOrDefault(key: "AssetContext") is not Assets.AssetCompilationContext context) || (scope.BasePath is null)) {
                scope.Diagnostics.ReportError(code: Puck.Transpiler.Diagnostics.PuckDiagnosticCodes.InvalidValue, message: "An asset reference requires a source path and its sibling asset lock.", span: asset.Span);
            } else if (context.TryResolve(asset.Path, scope.BasePath, asset.Span, scope.Diagnostics, out var path)) {
                value = System.Text.Json.Nodes.JsonValue.Create(path);
            }
            return true;
        }
        if (WorldDocumentEmitter.TryLowerInteractionValue(expression: expression, fieldKey: fieldKey, scope: scope, value: out value)) {
            return true;
        }
        if (
            (expression is IdentifierExpressionNode binding) &&
            (DocumentLowering.MemberContext(scope: scope) is Type model) &&
            ((model == typeof(WorldInteractionsSection)) || (model == typeof(WorldInteraction))) &&
            scope.TryLowerContextualBinding(fieldKey: fieldKey, name: binding.Name, value: out value)
        ) {
            return true;
        }
        if (expression is OperandExpressionNode operand) {
            value = WorldDocumentEmitter.LowerOperandArgument(operand: operand, scope: scope);
            return true;
        }
        if ((expression is IndexExpressionNode { Target: IdentifierExpressionNode { Name: var familyName } }) &&
            WorldDocumentEmitter.GetOrCreateStateFamilies(scope: scope).ContainsKey(key: familyName)) {
            value = WorldDocumentEmitter.LowerOperandArgument(
                operand: Puck.Transpiler.Parsing.PuckParser.CreateOperand(expression: expression, form: DocumentValueForm.Name),
                scope: scope);
            return true;
        }

        value = null;
        return false;
    }
    /// <inheritdoc />
    /// <remarks>A position is the model type a value fills. A call names an arm of the polymorphic type at its
    /// position, or the first arm any type declares under that name when the position is unknown, so a call the
    /// model does not declare (a template, a builtin) has no position.</remarks>
    public object? CallContext(object? context, string callName) => WorldCallArguments.ArmType(baseType: (context as Type), discriminator: callName);
    /// <inheritdoc />
    public object? MemberContext(object? context, string memberName) => ((context is Type owner)
        ? WorldCallArguments.MemberType(member: memberName, owner: owner)
        : null
    );
    /// <inheritdoc />
    /// <remarks>The form is the document model's: <see cref="WorldCallArguments"/> reads it from the member's name
    /// registry role where it carries one and from its type otherwise.</remarks>
    public DocumentValueForm ClassifyMember(object? context, string memberName) => (((context is Type owner)
        ? WorldCallArguments.Classify(member: memberName, owner: owner)
        : WorldArgumentForm.Unclassified
    ) switch {
        WorldArgumentForm.Name => DocumentValueForm.Name,
        WorldArgumentForm.Key => DocumentValueForm.Key,
        WorldArgumentForm.Expression => DocumentValueForm.Expression,
        WorldArgumentForm.Choice => DocumentValueForm.Choice,
        WorldArgumentForm.Text => DocumentValueForm.Text,
        _ => DocumentValueForm.Unclassified,
    });
    /// <inheritdoc />
    public bool IsChoiceWord(object? context, string memberName, string word) => (
        (context is Type owner) &&
        WorldCallArguments.IsChoiceWord(member: memberName, owner: owner, word: word)
    );
    /// <inheritdoc />
    public System.Text.Json.Nodes.JsonNode? NormalizeMemberValue(System.Text.Json.Nodes.JsonNode? value, DocumentValueForm form, DocumentScope scope) => (
        ((form == DocumentValueForm.Name) &&
        (DocumentLowering.MemberContext(scope: scope) is Type type) && (type == typeof(Puck.State.StateChannelRef)))
            ? WorldDocumentEmitter.NormalizeChannelReference(value: value)
            : value
    );
    /// <inheritdoc />
    public UnitDimension ClassifyField(string fieldKey) => WorldDocumentEmitterUnits.Classify(fieldKey: fieldKey);
    /// <inheritdoc />
    /// <remarks>An import names a world document (<see cref="WorldDocumentName"/>): it exists when its <c>.puck</c>
    /// source or its <c>.world.json</c> document stands beside the importer, the files the composer reads at run
    /// time. A file-form spelling is refused by name.</remarks>
    public bool TryFindDocumentImport(string directory, string name, out string reason) {
        if (!WorldDefinitionFileSource.TryResolveDocumentIn(
            directory: directory,
            documentPath: out var documentPath,
            name: name,
            reason: out reason,
            sourcePath: out var sourcePath
        )) {
            return false;
        }

        if (CompileInputs.Exists(path: sourcePath) || CompileInputs.Exists(path: documentPath)) {
            return true;
        }

        reason = $"Imported document '{name}' has neither a source at '{sourcePath}' nor a document at '{documentPath}'.";

        return false;
    }
    /// <inheritdoc />
    /// <remarks>The camera-program ops and rewindGroup take positional arguments; everything else is written by name and falls
    /// through to the generic <c>arg&lt;n&gt;</c> spelling.</remarks>
    public string? NameCallArgument(string callName, int positionalIndex) {
        if ((callName == "rewindGroup") && (positionalIndex == 0)) {
            return "group";
        }
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
