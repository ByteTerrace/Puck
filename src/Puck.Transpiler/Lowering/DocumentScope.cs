using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Units;

namespace Puck.Transpiler.Lowering;

/// <summary>Schema-owned value semantics, field dimensions and call argument names used by generic lowering.</summary>
public interface IDocumentVocabulary {
    /// <summary>Optionally lowers a schema-owned value before compile-time evaluation, including nested properties.</summary>
    /// <param name="expression">The authored value.</param>
    /// <param name="scope">The active lexical scope.</param>
    /// <param name="fieldKey">The destination field.</param>
    /// <param name="value">The schema's JSON representation.</param>
    /// <returns>Whether this vocabulary owns the field's value semantics.</returns>
    bool TryLowerValue(ExpressionNode expression, DocumentScope scope, string? fieldKey, out JsonNode? value) {
        value = null;
        return false;
    }
    /// <summary>Returns the physical dimension a field of this document carries, governing which unit suffixes its
    /// values admit and what they convert to.</summary>
    /// <param name="fieldKey">The bare property name, or the qualified <c>call.argument</c> key.</param>
    /// <returns>The field's dimension, or <see cref="UnitDimension.None"/> when it admits no unit.</returns>
    UnitDimension ClassifyField(string fieldKey);
    /// <summary>Returns the JSON key a call's positional argument fills.</summary>
    /// <param name="callName">The call's name as written.</param>
    /// <param name="positionalIndex">The argument's 0-based position.</param>
    /// <returns>The key, or <see langword="null"/> to fall back to the positional <c>arg&lt;n&gt;</c> spelling.</returns>
    string? NameCallArgument(string callName, int positionalIndex);
    /// <summary>Returns whether the given identifier introduces an embedded language block (e.g. <c>sql { ... }</c>) for this vocabulary.</summary>
    /// <param name="identifier">The block identifier.</param>
    /// <returns><c>true</c> if the block should be parsed as raw embedded source rather than standard DSL statements.</returns>
    bool IsEmbeddedLanguage(string identifier) => false;
    /// <summary>Returns whether a declaration statement of <paramref name="keyword"/> writes
    /// <paramref name="modifier"/> bare — as a flag with no argument list, the way a <c>table</c> writes
    /// <c>evicts</c>.</summary>
    /// <param name="keyword">The declaration's opening keyword as written (<c>table</c>, <c>slot</c>,
    /// <c>pile</c>, <c>grid</c>).</param>
    /// <param name="modifier">The bare identifier read where a <c>name(args)</c> modifier could stand.</param>
    /// <returns><c>true</c> when the modifier belongs to that declaration and is written without arguments.</returns>
    /// <remarks>An identifier not followed by <c>(</c> otherwise belongs to whatever statement comes next, so
    /// answering <c>false</c> leaves the cursor rewound past it.</remarks>
    bool IsBareModifier(string keyword, string modifier) => false;
    /// <summary>Returns how a call's argument is written: bare, or as quoted text.</summary>
    /// <param name="callName">The call's name as written.</param>
    /// <param name="argumentName">The argument's resolved name.</param>
    /// <returns>The argument's form, or <see cref="DocumentValueForm.Unclassified"/> when the vocabulary holds no
    /// opinion and the value is read as an ordinary compile-time expression.</returns>
    /// <remarks>The parser asks this, and it has no position to offer; lowering asks
    /// <see cref="ClassifyMember"/> with the position it has reached.</remarks>
    DocumentValueForm ClassifyArgument(string callName, string argumentName) => this.ClassifyMember(
        context: this.CallContext(callName: callName, context: null),
        memberName: argumentName
    );
    /// <summary>Returns the position a call's arguments fill: the vocabulary's own token for the shape the call
    /// names, read from <paramref name="context"/> where the call stands in a position that narrows it.</summary>
    /// <param name="context">The position the call itself fills, or <see langword="null"/> when unknown.</param>
    /// <param name="callName">The call's name as written.</param>
    /// <returns>The position, or <see langword="null"/> when the vocabulary does not own the call at this position.</returns>
    /// <remarks>A declared arm at a known position takes precedence over a compile-time builtin of the same name.
    /// Do not return an arm from an unrelated position when <paramref name="context"/> is known.</remarks>
    object? CallContext(object? context, string callName) => null;
    /// <summary>Returns the position a member's value fills, with a list reduced to its element.</summary>
    /// <param name="context">The position of the object or call holding the member.</param>
    /// <param name="memberName">The member's name.</param>
    /// <returns>The position, or <see langword="null"/> when the vocabulary does not know the member.</returns>
    object? MemberContext(object? context, string memberName) => null;
    /// <summary>Returns how a member's value is written: bare, or as quoted text.</summary>
    /// <param name="context">The position of the object or call holding the member.</param>
    /// <param name="memberName">The member's name.</param>
    /// <returns>The member's form, or <see cref="DocumentValueForm.Unclassified"/>.</returns>
    DocumentValueForm ClassifyMember(object? context, string memberName) => DocumentValueForm.Unclassified;
    /// <summary>Returns whether a bare word is one of the words a <see cref="DocumentValueForm.Choice"/> member
    /// admits. Such a word is the value itself, never a read of a compile-time binding that shares its name.</summary>
    /// <param name="context">The position of the object or call holding the member.</param>
    /// <param name="memberName">The member's name.</param>
    /// <param name="word">The bare word.</param>
    /// <returns><c>true</c> when the word is one of the member's choices.</returns>
    bool IsChoiceWord(object? context, string memberName, string word) => false;
    /// <summary>Materializes a computed member value in its document representation after evaluation.</summary>
    /// <param name="value">The owned, lowered value, including results of interpolation or bindings.</param>
    /// <param name="form">The member's classified form.</param>
    /// <param name="scope">The active scope, positioned at the member's value type.</param>
    /// <returns>The document value. The default leaves it unchanged.</returns>
    JsonNode? NormalizeMemberValue(JsonNode? value, DocumentValueForm form, DocumentScope scope) => value;
    /// <summary>Returns whether a runtime document import (an <c>import</c> not naming a <c>.puck</c> module) names a
    /// document that exists beside the importing source.</summary>
    /// <param name="directory">The full path of the importing source's directory.</param>
    /// <param name="name">The imported name as written.</param>
    /// <param name="reason">The named refusal when the import names nothing this vocabulary can read, or empty.</param>
    /// <returns><see langword="true"/> when the document exists. The default reads <paramref name="name"/> as a file
    /// path.</returns>
    bool TryFindDocumentImport(string directory, string name, out string reason) {
        var path = Path.GetFullPath(path: Path.Combine(
            path1: directory,
            path2: name
        ));

        reason = (Modules.CompileInputs.Exists(path: path)
            ? string.Empty
            : $"Imported document '{name}' could not be found at '{path}'."
        );

        return (reason.Length == 0);
    }
}
/// <summary>How the value of a call argument is written in source.</summary>
/// <remarks>A bare form is written without quotes and is never a string literal; <see cref="Text"/> is always a
/// string literal and never a bare word. The two never overlap, so an argument has one spelling.</remarks>
public enum DocumentValueForm {
    /// <summary>The vocabulary holds no opinion: a number, a flag, an array, an object, or a call it does not own.</summary>
    Unclassified,
    /// <summary>A reference to a declared name, written bare; a name computed at compile time is an interpolated
    /// string.</summary>
    Name,
    /// <summary>A cell key, written bare in the key grammar.</summary>
    Key,
    /// <summary>A value expression, written bare in the operand grammar.</summary>
    Expression,
    /// <summary>One word of a closed vocabulary, written bare.</summary>
    Choice,
    /// <summary>Free text, written as a string literal.</summary>
    Text,
}
/// <summary>One lowering pass's carried state: the constants and templates in scope, where diagnostics and source
/// spans go, and the document vocabulary answering the schema-specific questions.</summary>
public sealed partial class DocumentScope {
    /// <summary>Creates the root scope of a lowering pass.</summary>
    /// <param name="vocabulary">The document vocabulary being lowered against.</param>
    /// <param name="basePath">The base directory relative asset paths resolve against, or <see langword="null"/>.</param>
    /// <param name="constants">The <c>let</c> bindings in scope; a fresh dictionary when null.</param>
    /// <param name="templates">The <c>template</c> declarations in scope; a fresh dictionary when null.</param>
    /// <param name="sourceMap">The JSON-pointer-to-span map to fill, or <see langword="null"/> to record none.</param>
    /// <param name="diagnostics">The bag refusals are reported into; a fresh bag when null.</param>
    /// <param name="schema">The document's declared schema, or <see langword="null"/>.</param>
    /// <param name="currentPointer">The JSON pointer lowering is currently positioned at.</param>
    /// <param name="annotations">The arbitrary annotations carried across child scopes, or <see langword="null"/>.</param>
    public DocumentScope(
        IDocumentVocabulary vocabulary,
        string? basePath = null,
        Dictionary<string, ExpressionNode>? constants = null,
        Dictionary<string, TemplateNode>? templates = null,
        SourceMap? sourceMap = null,
        DiagnosticBag? diagnostics = null,
        string? schema = null,
        string currentPointer = "",
        Dictionary<string, object?>? annotations = null
    ) : this(
        annotations: (annotations ?? new(comparer: StringComparer.Ordinal)),
        arguments: new(comparer: StringComparer.Ordinal),
        basePath: basePath,
        budget: new(),
        constants: (constants ?? []),
        currentPointer: currentPointer,
        diagnostics: (diagnostics ?? new DiagnosticBag()),
        evaluating: new(comparer: StringComparer.Ordinal),
        locals: [],
        schema: schema,
        sourceMap: sourceMap,
        templates: (templates ?? []),
        templateScopes: new(comparer: StringComparer.Ordinal),
        values: [],
        vocabulary: vocabulary
    ) {
    }

    // Every derived scope names each piece of state it shares and each it starts fresh, and allocates nothing else:
    // a lambda application and a loop iteration each derive one, so a derivation that built state only to replace
    // it would pay for that on every element of every collection a document folds.
    private DocumentScope(
        IDocumentVocabulary vocabulary,
        string? basePath,
        Dictionary<string, ExpressionNode> constants,
        Dictionary<string, TemplateNode> templates,
        SourceMap? sourceMap,
        DiagnosticBag diagnostics,
        string? schema,
        string currentPointer,
        Dictionary<string, object?> annotations,
        Dictionary<string, JsonNode?> locals,
        DocumentEvaluationBudget budget,
        Dictionary<(string Name, string? Field), (ExpressionNode Expression, JsonNode? Value)> values,
        Dictionary<string, DocumentScope> arguments,
        Dictionary<string, DocumentScope> templateScopes,
        HashSet<string> evaluating
    ) {
        Vocabulary = vocabulary;
        BasePath = basePath;
        Constants = constants;
        Templates = templates;
        SourceMap = sourceMap;
        Diagnostics = diagnostics;
        Schema = schema;
        CurrentPointer = currentPointer;
        Annotations = annotations;
        Locals = locals;
        Budget = budget;
        m_values = values;
        m_arguments = arguments;
        m_templateScopes = templateScopes;
        m_evaluating = evaluating;
    }

    /// <summary>Gets the document vocabulary being lowered against.</summary>
    public IDocumentVocabulary Vocabulary { get; }
    /// <summary>Gets the base directory relative asset paths resolve against.</summary>
    public string? BasePath { get; }
    /// <summary>Gets the <c>let</c> bindings in scope.</summary>
    public Dictionary<string, ExpressionNode> Constants { get; }
    /// <summary>Gets the <c>template</c> declarations in scope.</summary>
    public Dictionary<string, TemplateNode> Templates { get; }
    /// <summary>Gets the JSON-pointer-to-span map being filled, or <see langword="null"/>.</summary>
    public SourceMap? SourceMap { get; }
    /// <summary>Gets the bag refusals are reported into.</summary>
    public DiagnosticBag Diagnostics { get; }
    /// <summary>Gets the document's declared schema.</summary>
    public string? Schema { get; }
    /// <summary>Gets or sets the JSON pointer lowering is currently positioned at.</summary>
    public string CurrentPointer { get; set; }
    /// <summary>Gets the lambda parameters bound in this scope, as already-lowered values.</summary>
    /// <remarks>Separate from <see cref="Constants"/> because a bound value is a JSON node, not an expression that
    /// could be lowered again; a local shadows a constant of the same name.</remarks>
    public Dictionary<string, JsonNode?> Locals { get; }
    /// <summary>Gets schema-agnostic user annotations attached to this compilation scope.</summary>
    public Dictionary<string, object?> Annotations { get; init; }

    // The constants a vocabulary bound only to refuse every read of their value, each to its refusal. Keyed by the
    // bound node, so a refusal follows the binding into every scope that shares the constants.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ExpressionNode, DocumentBindingRefusal> RefusedBindings = new();

    private readonly Dictionary<(string Name, string? Field), (ExpressionNode Expression, JsonNode? Value)> m_values;
    private readonly Dictionary<string, DocumentScope> m_arguments;
    private readonly Dictionary<string, DocumentScope> m_templateScopes;
    private readonly HashSet<string> m_evaluating;

    /// <summary>Gets the work budget shared by every scope of this compilation.</summary>
    public DocumentEvaluationBudget Budget { get; init; }

    /// <summary>Binds <paramref name="name"/> to a constant whose value no read may take: a name the vocabulary knows
    /// but cannot give one value, such as a bare enum member two enums declare. The name still reads as bound, so a
    /// position that takes a name keeps its spelling and it never falls through to a row; a position that takes its
    /// value reports <see cref="TryGetRefusedBinding"/>'s refusal where the name is read.</summary>
    /// <param name="name">The bare name.</param>
    /// <param name="code">The diagnostic code a read reports.</param>
    /// <param name="message">The refusal, naming the spellings that do read.</param>
    /// <returns>The bound node, which identifies the refused binding.</returns>
    public ExpressionNode BindRefused(string name, string code, string message) {
        var node = new LiteralExpressionNode(Value: null);

        RefusedBindings.AddOrUpdate(
            key: node,
            value: new DocumentBindingRefusal(
                Code: code,
                Message: message
            )
        );
        Constants[name] = node;

        return node;
    }
    /// <summary>Reports a read of <paramref name="name"/>'s value at <paramref name="span"/> when the name is bound by
    /// <see cref="BindRefused"/>, once per read however often lowering evaluates it.</summary>
    /// <param name="name">The bare name read.</param>
    /// <param name="span">The source that reads it.</param>
    /// <returns><see langword="true"/> when the read is refused; the caller then writes a placeholder, never the
    /// binding's value.</returns>
    public bool TryRefuseRead(string name, SourceSpan span) {
        if (!TryGetRefusedBinding(
            name: name,
            refusal: out var refusal
        )) {
            return false;
        }
        if (!Diagnostics.Any(predicate: diagnostic => ((diagnostic.Code == refusal.Code) && (diagnostic.Span == span)))) {
            Diagnostics.ReportError(
                code: refusal.Code,
                message: refusal.Message,
                span: span
            );
        }

        return true;
    }
    /// <summary>Returns the refusal a read of <paramref name="name"/>'s value reports, when the name is bound by
    /// <see cref="BindRefused"/> and no local shadows it.</summary>
    /// <param name="name">The bare name.</param>
    /// <param name="refusal">The refusal, on success.</param>
    /// <returns><see langword="true"/> when reading the name's value is refused.</returns>
    public bool TryGetRefusedBinding(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out DocumentBindingRefusal? refusal) {
        refusal = null;

        return (
            !Locals.ContainsKey(key: name) &&
            Constants.TryGetValue(
                key: name,
                value: out var expression
            ) &&
            RefusedBindings.TryGetValue(
                key: expression,
                value: out refusal
            )
        );
    }

    internal void BindArgument(string name, ExpressionNode expression, DocumentScope caller) {
        Constants[name] = expression;
        m_arguments[name] = caller;
    }
    internal DocumentScope TemplateDefinitionScope(string name) => m_templateScopes.GetValueOrDefault(
        defaultValue: this,
        key: name
    );
    // Evaluator-only borrowed values: never mutate or attach these nodes to an output container.
    internal bool TryEvaluateBinding(string name, string? fieldKey, out JsonNode? value) {
        if (Locals.TryGetValue(
            key: name,
            value: out value
        )) {
            return true;
        }
        if (!Constants.TryGetValue(
            key: name,
            value: out var expression
        )) {
            return false;
        }
        var key = (name, fieldKey);

        if (
            m_values.TryGetValue(
            key: key,
            value: out var cached
        ) &&
            ReferenceEquals(
            objA: cached.Expression,
            objB: expression
        )
        ) {
            value = cached.Value;
            return true;
        }
        if (!m_evaluating.Add(item: name)) {
            throw new DocumentEvaluationException(
                $"The binding '{name}' refers to itself.",
                expression.Span,
                PuckDiagnosticCodes.InvalidValue
            );
        }
        try {
            var lexical = (m_arguments.TryGetValue(
                key: name,
                value: out var caller
            )
                ? caller
                : ForConstant()
            );

            value = EvaluateAtNoPosition(expression: expression, fieldKey: fieldKey, lexical: lexical);
            m_values[key] = (expression, value);
            return true;
        } finally {
            m_evaluating.Remove(item: name);
        }
    }

    // A binding's value is data until something uses it, and its result is cached across uses, so it is evaluated at
    // no position: what it holds is classified where it lands, never where it was first read. Its own method so the
    // closure is built only for a binding evaluated, never for one read from a local or the cache.
    private static JsonNode? EvaluateAtNoPosition(ExpressionNode expression, string? fieldKey, DocumentScope lexical) => DocumentLowering.At(
        context: null,
        lower: () => DocumentLowering.EvaluateValue(
            expr: expression,
            fieldKey: fieldKey,
            scope: lexical
        ),
        scope: lexical
    );

    /// <summary>Gets a scope for lowering a CONSTANT: the same constants and templates, with no locals in scope.</summary>
    /// <remarks>A <c>let</c> is a document-level value and cannot read a loop binding or a lambda parameter — which
    /// is both what a reader expects of it and what makes its lowered value safe to keep.</remarks>
    /// <returns>This scope when it already binds no locals, or a locals-free one.</returns>
    public DocumentScope ForConstant() => ((Locals.Count == 0)
        ? this
        : new(
            annotations: new(comparer: StringComparer.Ordinal),
            arguments: m_arguments,
            basePath: BasePath,
            budget: Budget,
            constants: Constants,
            currentPointer: CurrentPointer,
            diagnostics: Diagnostics,
            evaluating: m_evaluating,
            locals: [],
            schema: Schema,
            sourceMap: SourceMap,
            templates: Templates,
            templateScopes: m_templateScopes,
            values: m_values,
            vocabulary: Vocabulary
        )
    );
    /// <summary>Indexes document-level declarations before either vocabulary emits rows.</summary>
    /// <param name="statements">The statements in this lexical document scope.</param>
    public void IndexDeclarations(IReadOnlyList<StatementNode> statements) {
        if (!Annotations.TryGetValue(key: "DeclaredRows", value: out var rowsValue)) {
            rowsValue = new HashSet<string>(comparer: StringComparer.Ordinal);
            Annotations["DeclaredRows"] = rowsValue;
        }
        if (!Annotations.TryGetValue(key: "DeclaredPools", value: out var poolsValue)) {
            poolsValue = new HashSet<string>(comparer: StringComparer.Ordinal);
            Annotations["DeclaredPools"] = poolsValue;
        }
        if (!Annotations.TryGetValue(key: "DeclaredGates", value: out var gatesValue)) {
            gatesValue = new HashSet<string>(comparer: StringComparer.Ordinal);
            Annotations["DeclaredGates"] = gatesValue;
        }
        IndexStructuralNames(gates: ((HashSet<string>)gatesValue!), pools: ((HashSet<string>)poolsValue!), rows: ((HashSet<string>)rowsValue!), statements: statements);
        IndexModuleInstances(statements: statements);
        foreach (var statement in statements) {
            if (statement is LetNode let) {
                if (!Constants.TryAdd(
                    key: let.Name,
                    value: let.Value
                )) {
                    Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.InvalidValue,
                        message: $"Duplicate constant '{let.Name}'.",
                        span: let.Span
                    );
                } else { m_arguments[let.Name] = ForConstant(); }
            } else if (statement is TemplateNode template) {
                if (!Templates.TryAdd(
                    key: template.Name,
                    value: template
                )) {
                    Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.InvalidValue,
                        message: $"Duplicate template '{template.Name}'.",
                        span: template.Span
                    );
                } else { m_templateScopes[template.Name] = ForConstant(); }
                if (template.Parameters.Select(selector: parameter => parameter.Name).Distinct(comparer: StringComparer.Ordinal).Count() != template.Parameters.Count) {
                    Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.InvalidValue,
                        message: $"Template '{template.Name}' repeats a parameter name.",
                        span: template.Span
                    );
                }
            }
        }
    }

    private static void IndexStructuralNames(IReadOnlyList<StatementNode> statements, HashSet<string> rows, HashSet<string> pools, HashSet<string> gates) {
        foreach (var statement in statements) {
            switch (statement) {
                case StatePoolDeclarationNode pool: pools.Add(item: pool.Name); rows.Add(item: pool.Name); break;
                case StatePairPoolDeclarationNode pool: pools.Add(item: pool.Name); rows.Add(item: pool.Name); break;
                case StateTableDeclarationNode row:
                    rows.Add(item: row.Name);
                    if ((row.Kind == "Bool") || ((row.Cells.Count > 0) && row.Cells.All(predicate: static cell => IsBoolean(value: cell.Value)))) { gates.Add(item: row.Name); }
                    break;
                case StateSlotDeclarationNode row:
                    rows.Add(item: row.Name);
                    if ((row.Kind == "Bool") || IsBoolean(value: row.Value)) { gates.Add(item: row.Name); }
                    break;
                case StatePileDeclarationNode row: rows.Add(item: row.Name); break;
                case StateGridDeclarationNode row: rows.Add(item: row.Name); break;
                case BlockNode block: IndexStructuralNames(block.Statements, rows, pools, gates); break;
                case ForStatementNode loop: IndexStructuralNames(loop.Body, rows, pools, gates); break;
            }
        }

        static bool IsBoolean(ExpressionNode? value) => (value is LiteralExpressionNode { Value: bool });
    }

    /// <summary>Resolves a lexical binding into an independently owned JSON value.</summary>
    /// <param name="name">The binding's name.</param>
    /// <param name="value">The resolved value, which may be null.</param>
    /// <param name="fieldKey">The destination field used to validate unit suffixes.</param>
    /// <returns>True when a local, template argument or constant bears the name.</returns>
    public bool TryLowerBinding(string name, out JsonNode? value, string? fieldKey = null) {
        var found = TryEvaluateBinding(
            fieldKey: fieldKey,
            name: name,
            value: out value
        );

        value = Budget.Copy(
            value,
            (Constants.TryGetValue(
                key: name,
                value: out var expression
            )
            ? expression.Span
            : new SourceSpan(
                    Column: 1,
                    Length: 1,
                    Line: 1,
                    Offset: 0
                ))
        );
        return found;
    }
    /// <summary>Lowers a binding at its use site's document position without caching contextual output as data.</summary>
    /// <param name="name">The lexical binding's name.</param>
    /// <param name="fieldKey">The destination field used to validate units.</param>
    /// <param name="value">The independently owned document value.</param>
    /// <returns>Whether a constant or template argument bears the name.</returns>
    /// <remarks>Constants and arguments keep their defining lexical environment. Only lowering annotations come
    /// from the use site, including its model position and active runtime bindings.</remarks>
    public bool TryLowerContextualBinding(string name, string? fieldKey, out JsonNode? value) {
        value = null;
        if (Locals.ContainsKey(key: name) || !Constants.TryGetValue(key: name, value: out var expression)) {
            return false;
        }
        if (!m_evaluating.Add(item: name)) {
            throw new DocumentEvaluationException($"The binding '{name}' refers to itself.", expression.Span, PuckDiagnosticCodes.InvalidValue);
        }

        try {
            var lexical = (m_arguments.GetValueOrDefault(key: name) ?? ForConstant());
            var positioned = new DocumentScope(
                annotations: Annotations,
                arguments: lexical.m_arguments,
                basePath: lexical.BasePath,
                budget: Budget,
                constants: lexical.Constants,
                currentPointer: CurrentPointer,
                diagnostics: Diagnostics,
                evaluating: m_evaluating,
                locals: lexical.Locals,
                schema: Schema,
                sourceMap: SourceMap,
                templates: lexical.Templates,
                templateScopes: lexical.m_templateScopes,
                values: lexical.m_values,
                vocabulary: Vocabulary
            );

            value = DocumentLowering.LowerValue(expr: expression, fieldKey: fieldKey, scope: positioned);
            return true;
        } finally {
            m_evaluating.Remove(item: name);
        }
    }
    /// <summary>Creates a scope identical to this one but carrying a different constant set, for a template
    /// invocation's bound parameters.</summary>
    /// <param name="invocationConstants">The constants the nested scope sees.</param>
    /// <param name="annotations">An optional annotation set isolated to the invocation.</param>
    /// <returns>The nested scope.</returns>
    public DocumentScope WithConstants(
        Dictionary<string, ExpressionNode> invocationConstants,
        Dictionary<string, object?>? annotations = null
    ) => new(
        annotations: (annotations ?? Annotations),
        arguments: new(dictionary: m_arguments, comparer: StringComparer.Ordinal),
        basePath: BasePath,
        budget: Budget,
        constants: invocationConstants,
        currentPointer: CurrentPointer,
        diagnostics: Diagnostics,
        evaluating: new(comparer: StringComparer.Ordinal),
        locals: [],
        schema: Schema,
        sourceMap: SourceMap,
        templates: Templates,
        templateScopes: m_templateScopes,
        values: [],
        vocabulary: Vocabulary
    );
    /// <summary>Creates a scope whose relative paths resolve from a defining source directory.</summary>
    /// <param name="basePath">The defining source directory.</param>
    /// <returns>The rebased lexical scope.</returns>
    public DocumentScope WithBasePath(string? basePath) => new(
        annotations: Annotations,
        arguments: new(dictionary: m_arguments, comparer: StringComparer.Ordinal),
        basePath: basePath,
        budget: Budget,
        constants: Constants,
        currentPointer: CurrentPointer,
        diagnostics: Diagnostics,
        evaluating: new(comparer: StringComparer.Ordinal),
        locals: [],
        schema: Schema,
        sourceMap: SourceMap,
        templates: Templates,
        templateScopes: m_templateScopes,
        values: [],
        vocabulary: Vocabulary
    );
    /// <summary>Creates a scope identical to this one but carrying a different local set, for one application of a
    /// lambda.</summary>
    /// <param name="lambdaLocals">The locals the nested scope sees.</param>
    /// <returns>The nested scope.</returns>
    public DocumentScope WithLocals(Dictionary<string, JsonNode?> lambdaLocals) => new(
        annotations: Annotations,
        arguments: m_arguments,
        basePath: BasePath,
        budget: Budget,
        constants: Constants,
        currentPointer: CurrentPointer,
        diagnostics: Diagnostics,
        evaluating: m_evaluating,
        locals: lambdaLocals,
        schema: Schema,
        sourceMap: SourceMap,
        templates: Templates,
        templateScopes: m_templateScopes,
        values: m_values,
        vocabulary: Vocabulary
    );
}
/// <summary>JSON helpers every lowering pass needs and none should restate.</summary>
public static class JsonNodeExtensions {
    /// <summary>Appends a node to an array, keeping a null as a JSON null rather than refusing it.</summary>
    /// <param name="array">The array to append to.</param>
    /// <param name="item">The node, which may be null.</param>
    public static void AppendNode(this JsonArray array, JsonNode? item) {
        ArgumentNullException.ThrowIfNull(array);

        ((IList<JsonNode?>)array).Add(item: item);
    }
}
