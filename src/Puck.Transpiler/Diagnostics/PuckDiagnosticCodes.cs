namespace Puck.Transpiler.Diagnostics;

/// <summary>Every diagnostic identifier the transpiler reports, each declared exactly once. Report sites name a
/// constant here rather than a literal, so a code cannot be minted twice for two different meanings. A new code may
/// take any number no constant here declares.</summary>
public static class PuckDiagnosticCodes {
    /// <summary>An addon hash defect.</summary>
    public const string AddonHash = "PUCK023";
    /// <summary>An addon payload defect.</summary>
    public const string AddonPayload = "PUCK021";
    /// <summary>A <c>local</c> missing or carrying an unparsable initializer.</summary>
    public const string LocalInitializerMissing = "PUCK007";
    /// <summary>A <c>local</c> missing its required kind annotation.</summary>
    public const string LocalKindMissing = "PUCK006";
    /// <summary>PUCK041: an array builtin (<c>map</c>, <c>filter</c>, <c>reduce</c>, <c>range</c>, <c>length</c>,
    /// <c>concat</c>) called with arguments it cannot evaluate at compile time.</summary>
    public const string BuiltinRefused = "PUCK041";
    /// <summary>A chained comparison in one <c>when</c> clause.</summary>
    public const string ChainedComparison = "PUCK004";
    /// <summary>PUCK040: a colon in front of a container value. A block or an array is written as a block
    /// (<c>host { }</c>, <c>cameras [ ]</c>); the colon is what marks a scalar leaf, so there is exactly one
    /// spelling for each.</summary>
    public const string ColonBeforeContainer = "PUCK040";
    /// <summary>A basis or import graph the composer refused, or a composition whose links or entry it cannot carry.</summary>
    public const string CompositionRefused = "PUCK035";
    /// <summary>A <c>decision</c> missing its required <c>periodSeconds</c>.</summary>
    public const string DecisionPeriodMissing = "PUCK029";
    /// <summary>A decision construct outside its required enclosing block, or an <c>option</c> missing its score.</summary>
    public const string DecisionStructure = "PUCK013";
    /// <summary>A lowered document that would not deserialize for validation.</summary>
    public const string DeserializeForValidation = "PUCK032";
    /// <summary>A <c>shape</c> or <c>placement</c> row reusing an id already used in the same collection.</summary>
    public const string DuplicateRowId = "PUCK033";
    /// <summary>More than one <c>when</c> clause in one rule or option body.</summary>
    public const string DuplicateWhen = "PUCK012";
    /// <summary>An effect statement outside an effects-accumulating body.</summary>
    public const string EffectOutsideEffectsBody = "PUCK008";
    /// <summary>A right-hand side whose shape the destination effect's own fields cannot carry.</summary>
    public const string EffectRhsShape = "PUCK009";
    /// <summary>Compilation work, source size, collection size or recursive depth exceeds its limit.</summary>
    public const string EvaluationLimit = "PUCK047";
    /// <summary>PUCK046: a <c>for</c> body assigning a field rather than emitting a row. Building a value from a
    /// sequence is <c>map</c>'s job, in value position.</summary>
    public const string ForAssignsAField = "PUCK046";
    /// <summary>PUCK114: a statement a rule body's <c>for</c> cannot repeat — a <c>when</c>, a <c>decision</c> or a
    /// nested <c>rule</c>. A rule has one gate and one decision, so a loop inside it repeats only locals and
    /// effects.</summary>
    public const string ForRepeatsARuleMember = "PUCK114";
    /// <summary>PUCK044: a <c>for</c> whose sequence is not an array known at compile time.</summary>
    public const string ForSequenceRefused = "PUCK044";
    /// <summary>An import graph defect (cycle, duplicate alias).</summary>
    public const string ImportGraph = "PUCK017";
    /// <summary>An import target that does not exist.</summary>
    public const string ImportTargetMissing = "PUCK016";
    /// <summary>PUCK043: an index read that cannot be resolved at compile time — a non-array, a fractional or
    /// unknown ordinal, or one outside the array.</summary>
    public const string IndexRefused = "PUCK043";
    /// <summary>A compile-time value is cyclic, invalid or outside its numeric representation.</summary>
    public const string InvalidValue = "PUCK048";
    /// <summary>PUCK042: a lambda written where no builtin takes one. A lambda is an argument to an array builtin
    /// and nothing else.</summary>
    public const string LambdaOutsideBuiltin = "PUCK042";
    /// <summary>A duplicate key in one block.</summary>
    public const string LintDuplicateKey = "PUCK_LINT_002";
    /// <summary>A shadowed declaration.</summary>
    public const string LintShadowedDeclaration = "PUCK_LINT_003";
    /// <summary>A reserved-channel prefix matching no known channel.</summary>
    public const string LintUnknownChannelPrefix = "PUCK_LINT_009";
    /// <summary>An unresolved placement parent.</summary>
    public const string LintUnresolvedPlacementParent = "PUCK_LINT_007";
    /// <summary>An unresolved <c>prototypeId</c>.</summary>
    public const string LintUnresolvedPrototype = "PUCK_LINT_006";
    /// <summary>An unresolved state-row reference, or the note that resolution was skipped for a basis.</summary>
    public const string LintUnresolvedState = "PUCK_LINT_005";
    /// <summary>An unresolved camera, view, or spawn-point reference.</summary>
    public const string LintUnresolvedView = "PUCK_LINT_008";
    /// <summary>An unused <c>let</c> binding.</summary>
    public const string LintUnusedLet = "PUCK_LINT_001";
    /// <summary>A nested <c>transaction</c>.</summary>
    public const string NestedTransaction = "PUCK019";
    /// <summary>An <c>onFailure</c> outside a <c>transaction</c>, or a second one inside the same transaction.</summary>
    public const string OnFailureStructure = "PUCK014";
    /// <summary>An operand spells a reserved channel with colons (<c>$reduce:count:zone</c>); the source spells it
    /// as a call (<c>count(zone)</c>), and a rule local by its bare name.</summary>
    public const string OperandColonChannel = "PUCK106";
    /// <summary>PUCK110: a call argument that holds a name, a cell key, a value expression, or a word of a closed
    /// vocabulary is written as a plain string literal. Such an argument is written bare, and the message names
    /// the bare spelling; an interpolated string computes a name, a key or a word and is admitted there.</summary>
    public const string ArgumentWrittenBare = "PUCK110";
    /// <summary>PUCK112: a member that holds a value expression is written as an interpolated string. An
    /// expression is written bare, and an interpolated string stands inside it only as an atom that computes one
    /// name or one number; the message names the bare spelling.</summary>
    public const string ExpressionBuiltAsText = "PUCK112";
    /// <summary>PUCK111: a call argument that holds free text is written as a bare word. Text is a string
    /// literal.</summary>
    public const string ArgumentWrittenQuoted = "PUCK111";
    /// <summary>Operand text that <c>ExpressionSpelling</c> refused.</summary>
    public const string OperandParse = "PUCK002";
    /// <summary>PUCK107: a <c>table</c>/<c>slot</c>/<c>grid</c> declaration spells its cell kind explicitly
    /// (<c>: Int</c>, <c>: Fixed</c>, ...); the kind is inferred from the row's cells, value, and modifiers, so the
    /// annotation is dropped rather than written.</summary>
    public const string StateDeclarationKindAnnotated = "PUCK107";
    /// <summary>PUCK108: a <c>local</c> spells its kind explicitly (<c>: Int</c>/<c>: Fixed</c> or <c>as Int</c>/
    /// <c>as Fixed</c>); the kind is inferred from its expression, so the annotation is dropped rather than
    /// written.</summary>
    public const string LocalKindAnnotated = "PUCK108";
    /// <summary>A reserved word used where a name was expected.</summary>
    public const string ReservedWordAsName = "PUCK018";
    /// <summary>A row reference that resolved to something other than exactly one state-row read.</summary>
    public const string RowReferenceExpected = "PUCK003";
    /// <summary>A <c>rule</c> block missing its quoted name.</summary>
    public const string RuleNameMissing = "PUCK011";
    /// <summary>A <c>rule</c> block carrying no effect statements.</summary>
    public const string RuleWithoutEffects = "PUCK026";
    /// <summary>A <c>schedule ... in</c> delay missing a unit the seconds dimension admits.</summary>
    public const string ScheduleDelayUnit = "PUCK010";
    /// <summary>A document the engine schema rejected outright.</summary>
    public const string SchemaRejected = "PUCK031";
    /// <summary>An engine semantic-validation refusal.</summary>
    public const string SemanticValidation = "PUCK030";
    /// <summary>A placement authoring both the bare <c>solid</c> flag and an explicit override.</summary>
    public const string SolidSpelledTwice = "PUCK028";
    /// <summary>A syntax error with no more specific code.</summary>
    public const string Syntax = "PUCK001";
    /// <summary>A template defect (unknown template, arity mismatch).</summary>
    public const string Template = "PUCK020";
    /// <summary>PUCK045: a template or <c>for</c> nested past the expansion ceiling — what a template that invokes
    /// itself looks like from inside the expander.</summary>
    public const string TemplateExpansionTooDeep = "PUCK045";
    /// <summary>A unit suffix the field's own dimension does not admit.</summary>
    public const string UnitNotAdmitted = "PUCK025";
    /// <summary>A unit suffix on a field the dimension table does not cover.</summary>
    public const string UnitOnUnknownField = "PUCK024";
    /// <summary>A kind annotation naming something other than an admitted cell kind.</summary>
    public const string UnknownKindAnnotation = "PUCK005";
    /// <summary>A <c>shape</c> naming an unrecognized primitive type.</summary>
    public const string UnknownShapeType = "PUCK027";
    /// <summary>A statement inside a section block that the section's own grammar cannot carry.</summary>
    public const string UnrecognizedSectionStatement = "PUCK036";
    /// <summary>An unresolved <c>let</c> reference.</summary>
    public const string UnresolvedLet = "PUCK015";
    /// <summary>A shape or placement parent that resolves to no sibling row in the same collection.</summary>
    public const string UnresolvedParent = "PUCK034";
    /// <summary>PUCK039: a compound assignment (<c>-=</c>, <c>*=</c>, <c>&gt;&gt;=</c>, …) in a document whose
    /// effects carry no such operator.</summary>
    public const string UnsupportedAssignmentOperator = "PUCK039";
    /// <summary>PUCK038: a call-form gate (<c>key(left, held)</c>) in a document whose gates are comparisons
    /// only.</summary>
    public const string UnsupportedCallGate = "PUCK038";
    /// <summary>PUCK037: a control-flow statement (<c>if</c>, <c>repeat</c>, <c>break</c>) in a document whose rule
    /// shape is straight-line. The language parses control flow for every vocabulary; whether one can CARRY a branch
    /// is the vocabulary's own answer.</summary>
    public const string UnsupportedControlFlow = "PUCK037";
    /// <summary>PUCK049: a <c>table</c>/<c>slot</c>/<c>pile</c>/<c>grid</c> declaration outside <c>state.world</c> —
    /// in <c>state.body</c>, <c>state.identity</c>, or anywhere else the owning vocabulary does not admit one.</summary>
    public const string StateDeclarationOutsideWorld = "PUCK049";
    /// <summary>PUCK050: <c>state.world</c> authored more than once — as the array form and a declaration block, as
    /// two declaration blocks, or as two arrays.</summary>
    public const string StateWorldSectionMixed = "PUCK050";
    /// <summary>PUCK051: a declaration reusing a row name already declared in the same <c>state.world</c>, or a
    /// <c>table</c>/<c>grid</c> cell reusing a key already declared in the same row.</summary>
    public const string StateDeclarationDuplicateName = "PUCK051";
    /// <summary>PUCK052: a declared row name or cell key carrying the reserved <c>$</c> prefix.</summary>
    public const string StateDeclarationReservedKey = "PUCK052";
    /// <summary>PUCK053: a default value or a <c>bounds</c> argument that does not fit the row's declared kind.</summary>
    public const string StateDeclarationInvalidDefault = "PUCK053";
    /// <summary>PUCK054: a <c>table</c>'s <c>capacity(...)</c> smaller than its own authored cell count.</summary>
    public const string StateDeclarationCapacityTooSmall = "PUCK054";
    /// <summary>PUCK055: a modifier the declaration's shape or kind does not admit — <c>bounds</c>/<c>advance</c> on
    /// a <c>Bool</c>/<c>Text</c> row, or <c>capacity</c> on a <c>slot</c>.</summary>
    public const string StateDeclarationModifierNotAdmitted = "PUCK055";
    /// <summary>PUCK056: more than one behavior on a row or cell — a repeated <c>advance</c>/<c>bounds</c> modifier,
    /// or a cell combining <c>advance</c> with <c>behavior(none)</c>.</summary>
    public const string StateDeclarationBehaviorConflict = "PUCK056";
    /// <summary>PUCK057: an unrecognized modifier name, or a modifier argument the modifier's own shape refuses.</summary>
    public const string StateDeclarationUnknownModifier = "PUCK057";
    /// <summary>PUCK058: an <c>advance(perSecond: ...)</c> rate that does not reduce to an exact 64-bit fraction.</summary>
    public const string StateDeclarationRateInexact = "PUCK058";
    /// <summary>PUCK059: a <c>pile</c>/<c>grid</c> declaration naming another row that does not exist in the same
    /// <c>state.world</c> — an unknown <c>of</c> token row, <c>positions</c> row, or <c>inverse</c> tokens/codes
    /// row.</summary>
    public const string StateDeclarationUnknownReference = "PUCK059";
    /// <summary>PUCK060: a <c>pile</c>/<c>grid</c> declaration naming a row that exists but carries the wrong shape
    /// for the reference — a <c>pile</c>'s <c>of</c> row that is not a plain token domain, a <c>positions</c> row
    /// that is not an integer <c>keysOf</c> row, or an <c>inverse</c> pair whose keys or order disagree.</summary>
    public const string StateDeclarationReferenceShapeMismatch = "PUCK060";
    /// <summary>PUCK061: a <c>pile</c> declaring the same token, or a <c>grid</c> declaring the same cell key, more
    /// than once.</summary>
    public const string StateDeclarationDuplicateToken = "PUCK061";
    /// <summary>PUCK062: a <c>pile</c>'s <c>capacity(...)</c> greater than the token count its <c>of</c> row
    /// provides — a pile can never hold more members than its domain admits.</summary>
    public const string StateDeclarationCapacityExceedsDomain = "PUCK062";
    /// <summary>PUCK063: a <c>grid</c> declared with a kind other than <c>Int</c> or <c>Bool</c> — the two kinds a
    /// physical-lattice board's cells may carry.</summary>
    public const string StateDeclarationKindNotAdmitted = "PUCK063";
    /// <summary>PUCK064: a <c>grid</c> cell key that is not a whole-number topology cell ordinal inside
    /// <c>0..width*depth-1</c>.</summary>
    public const string StateDeclarationInvalidCellReference = "PUCK064";
    /// <summary>PUCK065: a <c>grid</c> combining <c>inverse(...)</c> with an authored cell body — a derived board's
    /// cells come from its tokens/codes rows alone.</summary>
    public const string StateDeclarationInverseWithAuthoredCells = "PUCK065";
    /// <summary>PUCK066: a <c>grid</c> reusing a topology name already declared by another <c>grid</c> or an
    /// explicitly authored <c>state.lattices</c> entry.</summary>
    public const string StateDeclarationDuplicateTopologyName = "PUCK066";
    /// <summary>PUCK070: a SQL column or slot type the dialect does not admit; a floating-point one (<c>REAL</c>, <c>FLOAT</c>, <c>DOUBLE</c>) is told state holds no floats, use <c>FIXED</c>.</summary>
    public const string SqlUnsupportedType = "PUCK070";
    /// <summary>PUCK071: a composite <c>PRIMARY KEY (a, b)</c> in a SQL table declaration — a state cell has exactly one key.</summary>
    public const string SqlCompositePrimaryKey = "PUCK071";
    /// <summary>PUCK072: a <c>CHECK</c> constraint shape outside <c>BETWEEN a AND b</c>, <c>&gt;= a</c>, or <c>&lt;= b</c>.</summary>
    public const string SqlInvalidCheckShape = "PUCK072";
    /// <summary>PUCK073: an unsupported SQL clause or construct (<c>GROUP BY</c>, <c>HAVING</c>, <c>WINDOW</c>, <c>LIMIT</c>, cross-key <c>JOIN</c>, <c>UNION</c>, <c>TRIGGER</c>, etc.).</summary>
    public const string SqlUnsupportedClause = "PUCK073";
    /// <summary>PUCK074: a set-based <c>UPDATE</c> reading a column it writes at other keys — self-referential multi-key updates are refused.</summary>
    public const string SqlSelfReferentialSetUpdate = "PUCK074";
    /// <summary>PUCK075: an inserted row missing a <c>NOT NULL</c> column that declares no default value.</summary>
    public const string SqlMissingRequiredColumn = "PUCK075";
    /// <summary>PUCK076: a syntax or grammatical refusal inside a <c>sql { ... }</c> block.</summary>
    public const string SqlSyntaxError = "PUCK076";
    /// <summary>PUCK077: a space is malformed: an unknown or missing field, dimensions out of range, a duplicate name, or over the space limit.</summary>
    public const string EmbeddingSpaceInvalid = "PUCK077";
    /// <summary>PUCK078: a Vector row names no space and there is no default, names an undeclared space, or a non-Vector row names a space.</summary>
    public const string EmbeddingSpaceUnknown = "PUCK078";
    /// <summary>PUCK079: an embedded text has no lock entry; run puck embed.</summary>
    public const string EmbeddingLockMissing = "PUCK079";
    /// <summary>PUCK080: the lock's model, revision, or dimensions differ from the space; run puck embed.</summary>
    public const string EmbeddingLockStale = "PUCK080";
    /// <summary>PUCK081: a vector literal is not base64url, has the wrong length, holds -128, or fails admission.</summary>
    public const string VectorLiteralInvalid = "PUCK081";
    /// <summary>PUCK082: embed or vector appears where no vector operand or value is admitted.</summary>
    public const string VectorLiteralMisplaced = "PUCK082";
    /// <summary>PUCK083: capacity × dimensions or the section total exceeds its ceiling.</summary>
    public const string VectorRowTooLarge = "PUCK083";
    /// <summary>PUCK084: a vector operation names a non-vector operand, mixes spaces, or gives nearest or remember a shape, k, or threshold they do not admit.</summary>
    public const string VectorOperandMismatch = "PUCK084";
    /// <summary>PUCK085: an embed literal has no operand, destination, or default to take its space from.</summary>
    public const string EmbeddingSpaceAmbiguous = "PUCK085";
    /// <summary>PUCK086: embeds on something other than a Text table, or a colliding row name.</summary>
    public const string EmbedsInvalid = "PUCK086";
    /// <summary>PUCK087: a mix term count or weight is out of range, or a weight is zero.</summary>
    public const string VectorMixInvalid = "PUCK087";
    /// <summary>PUCK088: where is not a keyed Bool row, or exclude is malformed.</summary>
    public const string VectorFilterInvalid = "PUCK088";
    /// <summary>PUCK089: a family size that is not a positive integer known at compile time.</summary>
    public const string FamilySizeInvalid = "PUCK089";
    /// <summary>PUCK090: a compile-time family index that names no member of the family, outside its bounds or at a gap.</summary>
    public const string FamilyIndexOutOfBounds = "PUCK090";
    /// <summary>PUCK091: an enum or enum member that was not declared.</summary>
    public const string EnumMemberUnknown = "PUCK091";
    /// <summary>PUCK092: a record field that was not declared or type mismatch.</summary>
    public const string RecordFieldUnknown = "PUCK092";
    /// <summary>PUCK095: a cyclic dependency in derived state declarations.</summary>
    public const string DerivedStateCycle = "PUCK095";
    /// <summary>PUCK096: a duplicate enum member or record field name.</summary>
    public const string DuplicateTypeMember = "PUCK096";
    /// <summary>PUCK097: a <c>match:</c> language, or a symbol's value band, the pattern algebra refuses.</summary>
    public const string PatternExpression = "PUCK097";
    /// <summary>PUCK098: a pattern whose derivative machine needs more states than the row budgets.</summary>
    public const string PatternStateBudget = "PUCK098";
    /// <summary>PUCK099: a rule group a <c>stabilize</c> or <c>workflow</c> block cannot lower — a malformed pass
    /// ceiling, a member the group cannot name, or a step shape a staged cursor does not carry.</summary>
    public const string RuleGroupShapeInadmissible = "PUCK099";
    /// <summary>PUCK100: a rule's name position that resolves to a template parameter's own identifier, so every
    /// instantiation would mint the same name.</summary>
    public const string RuleNameNotLiteral = "PUCK100";
    /// <summary>PUCK101: a <c>set</c> declaration the cell-set algebra refuses.</summary>
    public const string CellSetExpressionInvalid = "PUCK101";
    /// <summary>PUCK102: a family member index set the declaration cannot carry — a duplicate index, a descending
    /// range, or a member count outside the family bounds.</summary>
    public const string FamilyMembersInvalid = "PUCK102";
    /// <summary>PUCK103: a <c>transform</c> statement written with a result label. A transform's destination is the
    /// call's own argument, so the statement is written <c>transform call(...)</c>.</summary>
    public const string TransformResultLabel = "PUCK103";
    /// <summary>PUCK104: a <c>test</c> declaration's own shape — a body member that is not <c>given</c>,
    /// <c>when</c> or <c>expect</c>, one of those written twice or out of order, a test with no expectation, a
    /// <c>with</c> subject naming a template rather than a module, or two tests of one document naming the same
    /// generated world.</summary>
    public const string TestShapeInadmissible = "PUCK104";
    /// <summary>PUCK105: a line inside a <c>test</c> block the generated test world cannot carry — a
    /// <c>given</c> line that is not a cell assignment to a literal, a <c>when</c> step that is neither
    /// <c>ticks &lt;n&gt;</c> nor <c>seat&lt;n&gt;: &lt;command line&gt;</c>, a step acting as something other than a
    /// seat, or a command outside the scheduled step vocabulary.</summary>
    public const string TestStepInadmissible = "PUCK105";
    /// <summary>PUCK113: a name the author wrote, bare or quoted, in a spelling Puck reserves for the names it
    /// generates (<c>Puck.State.GeneratedName</c>): a <c>$</c> inside or a <c>~</c> anywhere in a document name, such as
    /// a row, a rule, a placement or a destination, or a <c>~</c> in a world name. A name headed by the alias of one of
    /// the document's own aliased imports (<c>dive$diveCourt</c>) is that import's, restated where it is written.</summary>
    public const string GeneratedNameReserved = "PUCK113";
    /// <summary>PUCK115: an enum member spelled like a <c>let</c> or a module parameter in the same scope, or a bare
    /// read of a member two enums in the same scope declare. A bare member name reads as the member's ordinal, so it
    /// cannot keep a second meaning; rename one, or read the member qualified (<c>Enum.member</c>). A shared member is
    /// reported where it is read bare; a position that takes a name, not a value, keeps its spelling.</summary>
    public const string EnumMemberShadowsConstant = "PUCK115";
    /// <summary>PUCK116: a placement id a channel cannot name — one carrying <c>:</c>, which separates a reserved
    /// channel's arguments (<c>$region:&lt;placementId&gt;</c>), or one exactly <c>$each</c>, the token
    /// <c>placement:$each</c> binds to a rule's <c>forEach</c> key.</summary>
    public const string PlacementIdReserved = "PUCK116";
    /// <summary>PUCK117: a module alias spelled like a row, pool, <c>let</c> or module parameter of the scope that
    /// uses it. The scope reads a name the instance declares as <c>alias.name</c>, which would also spell a cell of the
    /// row or a member of the constant; rename one.</summary>
    public const string ModuleAliasShadowsName = "PUCK117";
    /// <summary>PUCK118: an information notice that the engine's validation of a world deferred a check to the host
    /// that runs it, because the host diagnosing the source carries no catalog for it (a machine engine, a
    /// post-process package, a probe kind). A deferral is no finding against the author: it never fails a
    /// compile or a composition.</summary>
    public const string SemanticValidationDeferred = "PUCK118";
    /// <summary>PUCK119: a state row names an enum its composed world's state section does not declare — a
    /// <c>table</c>/<c>slot</c>/<c>grid</c> written <c>: Enum</c> or a <c>row { enum: Enum }</c>, refused by the
    /// validation of the composed world — or a record field <c>field: Enum</c> names one its document does not declare.
    /// Declare it with <c>enum Enum { … }</c>; a cell kind is never written.</summary>
    public const string StateEnumUndeclared = "PUCK119";
    /// <summary>PUCK_LINT_010: a literal mix weight's share is below 1/64; consider mean over a history table.</summary>
    public const string VectorMixStall = "PUCK_LINT_010";
}

