namespace Puck.World.Transpiler.Diagnostics;

/// <summary>Every diagnostic identifier the transpiler reports, each declared exactly once. Report sites name a
/// constant here rather than a literal, so a code cannot be minted twice for two different meanings. A retired code
/// stays declared and unused rather than being recycled.</summary>
public static class PuckDiagnosticCodes {
    /// <summary>A syntax error with no more specific code.</summary>
    public const string Syntax = "PUCK001";

    /// <summary>Operand text that <c>ExpressionSpelling</c> refused.</summary>
    public const string OperandParse = "PUCK002";

    /// <summary>A row reference that resolved to something other than exactly one state-row read.</summary>
    public const string RowReferenceExpected = "PUCK003";

    /// <summary>A chained comparison in one <c>when</c> clause.</summary>
    public const string ChainedComparison = "PUCK004";

    /// <summary>A kind annotation naming something other than an admitted cell kind.</summary>
    public const string UnknownKindAnnotation = "PUCK005";

    /// <summary>A <c>bind</c> missing its required kind annotation.</summary>
    public const string BindKindMissing = "PUCK006";

    /// <summary>A <c>bind</c> missing or carrying an unparsable initializer.</summary>
    public const string BindInitializerMissing = "PUCK007";

    /// <summary>An effect statement outside an effects-accumulating body.</summary>
    public const string EffectOutsideEffectsBody = "PUCK008";

    /// <summary>A right-hand side whose shape the destination effect's own fields cannot carry.</summary>
    public const string EffectRhsShape = "PUCK009";

    /// <summary>A <c>schedule ... in</c> delay missing a unit the seconds dimension admits.</summary>
    public const string ScheduleDelayUnit = "PUCK010";

    /// <summary>A <c>rule</c> block missing its quoted name.</summary>
    public const string RuleNameMissing = "PUCK011";

    /// <summary>More than one <c>when</c> clause in one rule or option body.</summary>
    public const string DuplicateWhen = "PUCK012";

    /// <summary>A decision construct outside its required enclosing block, or an <c>option</c> missing its score.</summary>
    public const string DecisionStructure = "PUCK013";

    /// <summary>An <c>onFailure</c> outside a <c>transaction</c>, or a second one inside the same transaction.</summary>
    public const string OnFailureStructure = "PUCK014";

    /// <summary>An unresolved <c>let</c> reference.</summary>
    public const string UnresolvedLet = "PUCK015";

    /// <summary>An import target that does not exist.</summary>
    public const string ImportTargetMissing = "PUCK016";

    /// <summary>An import graph defect (cycle, duplicate alias).</summary>
    public const string ImportGraph = "PUCK017";

    /// <summary>A reserved word used where a name was expected.</summary>
    public const string ReservedWordAsName = "PUCK018";

    /// <summary>A nested <c>transaction</c>.</summary>
    public const string NestedTransaction = "PUCK019";

    /// <summary>A template defect (unknown template, arity mismatch).</summary>
    public const string Template = "PUCK020";

    /// <summary>An addon payload defect.</summary>
    public const string AddonPayload = "PUCK021";

    /// <summary>An addon hash defect.</summary>
    public const string AddonHash = "PUCK023";

    /// <summary>A unit suffix on a field the dimension table does not cover.</summary>
    public const string UnitOnUnknownField = "PUCK024";

    /// <summary>A unit suffix the field's own dimension does not admit.</summary>
    public const string UnitNotAdmitted = "PUCK025";

    /// <summary>A <c>rule</c> block carrying no effect statements.</summary>
    public const string RuleWithoutEffects = "PUCK026";

    /// <summary>A <c>shape</c> naming an unrecognized primitive type.</summary>
    public const string UnknownShapeType = "PUCK027";

    /// <summary>A placement authoring both the bare <c>solid</c> flag and an explicit override.</summary>
    public const string SolidSpelledTwice = "PUCK028";

    /// <summary>A <c>decision</c> missing its required <c>periodSeconds</c>.</summary>
    public const string DecisionPeriodMissing = "PUCK029";

    /// <summary>An engine semantic-validation refusal.</summary>
    public const string SemanticValidation = "PUCK030";

    /// <summary>A document the engine schema rejected outright.</summary>
    public const string SchemaRejected = "PUCK031";

    /// <summary>A lowered document that would not deserialize for validation.</summary>
    public const string DeserializeForValidation = "PUCK032";

    /// <summary>A <c>shape</c> or <c>placement</c> row reusing an id already used in the same collection.</summary>
    public const string DuplicateRowId = "PUCK033";

    /// <summary>A shape or placement parent that resolves to no sibling row in the same collection.</summary>
    public const string UnresolvedParent = "PUCK034";

    /// <summary>A basis or import graph the composer refused.</summary>
    public const string CompositionRefused = "PUCK035";

    /// <summary>A statement inside a section block that the section's own grammar cannot carry.</summary>
    public const string UnrecognizedSectionStatement = "PUCK036";

    /// <summary>An unused <c>let</c> binding.</summary>
    public const string LintUnusedLet = "PUCK_LINT_001";

    /// <summary>A duplicate key in one block.</summary>
    public const string LintDuplicateKey = "PUCK_LINT_002";

    /// <summary>A shadowed declaration.</summary>
    public const string LintShadowedDeclaration = "PUCK_LINT_003";

    /// <summary>An empty block.</summary>
    public const string LintEmptyBlock = "PUCK_LINT_004";

    /// <summary>An unresolved state-row reference, or the note that resolution was skipped for a basis.</summary>
    public const string LintUnresolvedState = "PUCK_LINT_005";

    /// <summary>An unresolved <c>prototypeId</c>.</summary>
    public const string LintUnresolvedPrototype = "PUCK_LINT_006";

    /// <summary>An unresolved placement parent.</summary>
    public const string LintUnresolvedPlacementParent = "PUCK_LINT_007";

    /// <summary>An unresolved camera, view, or spawn-point reference.</summary>
    public const string LintUnresolvedView = "PUCK_LINT_008";

    /// <summary>A reserved-channel prefix matching no known channel.</summary>
    public const string LintUnknownChannelPrefix = "PUCK_LINT_009";
}
