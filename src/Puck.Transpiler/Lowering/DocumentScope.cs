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
}
/// <summary>One lowering pass's carried state: the constants and templates in scope, where diagnostics and source
/// spans go, and the document vocabulary answering the schema-specific questions.</summary>
/// <param name="vocabulary">The document vocabulary being lowered against.</param>
/// <param name="basePath">The base directory relative asset paths resolve against, or <see langword="null"/>.</param>
/// <param name="constants">The <c>let</c> bindings in scope; a fresh dictionary when null.</param>
/// <param name="templates">The <c>template</c> declarations in scope; a fresh dictionary when null.</param>
/// <param name="sourceMap">The JSON-pointer-to-span map to fill, or <see langword="null"/> to record none.</param>
/// <param name="diagnostics">The bag refusals are reported into; a fresh bag when null.</param>
/// <param name="schema">The document's declared schema, or <see langword="null"/>.</param>
/// <param name="currentPointer">The JSON pointer lowering is currently positioned at.</param>
public sealed class DocumentScope(
    IDocumentVocabulary vocabulary,
    string? basePath = null,
    Dictionary<string, ExpressionNode>? constants = null,
    Dictionary<string, TemplateNode>? templates = null,
    SourceMap? sourceMap = null,
    DiagnosticBag? diagnostics = null,
    string? schema = null,
    string currentPointer = ""
) {
    /// <summary>Gets the document vocabulary being lowered against.</summary>
    public IDocumentVocabulary Vocabulary { get; } = vocabulary;

    /// <summary>Gets the base directory relative asset paths resolve against.</summary>
    public string? BasePath { get; } = basePath;

    /// <summary>Gets the <c>let</c> bindings in scope.</summary>
    public Dictionary<string, ExpressionNode> Constants { get; } = (constants ?? []);

    /// <summary>Gets the <c>template</c> declarations in scope.</summary>
    public Dictionary<string, TemplateNode> Templates { get; } = (templates ?? []);

    /// <summary>Gets the JSON-pointer-to-span map being filled, or <see langword="null"/>.</summary>
    public SourceMap? SourceMap { get; } = sourceMap;

    /// <summary>Gets the bag refusals are reported into.</summary>
    public DiagnosticBag Diagnostics { get; } = (diagnostics ?? new DiagnosticBag());

    /// <summary>Gets the document's declared schema.</summary>
    public string? Schema { get; } = schema;

    /// <summary>Gets or sets the JSON pointer lowering is currently positioned at.</summary>
    public string CurrentPointer { get; set; } = currentPointer;

    /// <summary>Gets the lambda parameters bound in this scope, as already-lowered values.</summary>
    /// <remarks>Separate from <see cref="Constants"/> because a bound value is a JSON node, not an expression that
    /// could be lowered again; a local shadows a constant of the same name.</remarks>
    public Dictionary<string, JsonNode?> Locals { get; private init; } = [];

    private Dictionary<(string Name, string? Field), (ExpressionNode Expression, JsonNode? Value)> m_values = [];
    private Dictionary<string, DocumentScope> m_arguments = new(StringComparer.Ordinal);
    private Dictionary<string, DocumentScope> m_templateScopes = new(StringComparer.Ordinal);
    private HashSet<string> m_evaluating = new(StringComparer.Ordinal);

    /// <summary>Gets the work budget shared by every scope of this compilation.</summary>
    public DocumentEvaluationBudget Budget { get; init; } = new();

    /// <summary>Indexes document-level declarations before either vocabulary emits rows.</summary>
    /// <param name="statements">The statements in this lexical document scope.</param>
    public void IndexDeclarations(IReadOnlyList<StatementNode> statements) {
        foreach (var statement in statements) {
            if (statement is LetNode let) {
                if (!Constants.TryAdd(let.Name, let.Value)) {
                    Diagnostics.ReportError(PuckDiagnosticCodes.InvalidValue, $"Duplicate constant '{let.Name}'.", let.Span);
                } else { m_arguments[let.Name] = ForConstant(); }
            } else if (statement is TemplateNode template) {
                if (!Templates.TryAdd(template.Name, template)) {
                    Diagnostics.ReportError(PuckDiagnosticCodes.InvalidValue, $"Duplicate template '{template.Name}'.", template.Span);
                } else { m_templateScopes[template.Name] = ForConstant(); }
                if (template.Parameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != template.Parameters.Count) {
                    Diagnostics.ReportError(PuckDiagnosticCodes.InvalidValue, $"Template '{template.Name}' repeats a parameter name.", template.Span);
                }
            }
        }
    }

    /// <summary>Resolves a lexical binding into an independently owned JSON value.</summary>
    /// <param name="name">The binding's name.</param>
    /// <param name="value">The resolved value, which may be null.</param>
    /// <param name="fieldKey">The destination field used to validate unit suffixes.</param>
    /// <returns>True when a local, template argument or constant bears the name.</returns>
    public bool TryLowerBinding(string name, out JsonNode? value, string? fieldKey = null) {
        var found = TryEvaluateBinding(name, fieldKey, out value);
        value = Budget.Copy(value, Constants.TryGetValue(name, out var expression) ? expression.Span : new SourceSpan(0, 1, 1, 1));
        return found;
    }

    // Evaluator-only borrowed values: never mutate or attach these nodes to an output container.
    internal bool TryEvaluateBinding(string name, string? fieldKey, out JsonNode? value) {
        if (Locals.TryGetValue(name, out value)) {
            return true;
        }
        if (!Constants.TryGetValue(name, out var expression)) {
            return false;
        }
        var key = (name, fieldKey);
        if (m_values.TryGetValue(key, out var cached) && ReferenceEquals(cached.Expression, expression)) {
            value = cached.Value;
            return true;
        }
        if (!m_evaluating.Add(name)) {
            throw new DocumentEvaluationException($"The binding '{name}' refers to itself.", expression.Span, PuckDiagnosticCodes.InvalidValue);
        }
        try {
            var lexical = m_arguments.TryGetValue(name, out var caller) ? caller : ForConstant();
            value = DocumentLowering.EvaluateValue(expression, lexical, fieldKey);
            m_values[key] = (expression, value);
            return true;
        } finally {
            m_evaluating.Remove(name);
        }
    }

    internal void BindArgument(string name, ExpressionNode expression, DocumentScope caller) {
        Constants[name] = expression;
        m_arguments[name] = caller;
    }

    internal DocumentScope TemplateDefinitionScope(string name) => m_templateScopes.GetValueOrDefault(name, this);

    /// <summary>Gets a scope for lowering a CONSTANT: the same constants and templates, with no locals in scope.</summary>
    /// <remarks>A <c>let</c> is a document-level value and cannot read a loop binding or a lambda parameter — which
    /// is both what a reader expects of it and what makes its lowered value safe to keep.</remarks>
    /// <returns>This scope when it already binds no locals, or a locals-free one.</returns>
    public DocumentScope ForConstant() => ((Locals.Count == 0) ? this : new(
        vocabulary: Vocabulary,
        basePath: BasePath,
        constants: Constants,
        templates: Templates,
        sourceMap: SourceMap,
        diagnostics: Diagnostics,
        schema: Schema,
        currentPointer: CurrentPointer
    ) {
        m_values = m_values,
        m_arguments = m_arguments,
        m_templateScopes = m_templateScopes,
        m_evaluating = m_evaluating,
        Budget = Budget,
    });

    /// <summary>Creates a scope identical to this one but carrying a different constant set, for a template
    /// invocation's bound parameters.</summary>
    /// <param name="invocationConstants">The constants the nested scope sees.</param>
    /// <returns>The nested scope.</returns>
    public DocumentScope WithConstants(Dictionary<string, ExpressionNode> invocationConstants) => new(
        vocabulary: Vocabulary,
        basePath: BasePath,
        constants: invocationConstants,
        templates: Templates,
        sourceMap: SourceMap,
        diagnostics: Diagnostics,
        schema: Schema,
        currentPointer: CurrentPointer
    ) {
        m_arguments = new(m_arguments, StringComparer.Ordinal),
        m_templateScopes = m_templateScopes,
        Budget = Budget,
    };

    /// <summary>Creates a scope identical to this one but carrying a different local set, for one application of a
    /// lambda.</summary>
    /// <param name="lambdaLocals">The locals the nested scope sees.</param>
    /// <returns>The nested scope.</returns>
    public DocumentScope WithLocals(Dictionary<string, JsonNode?> lambdaLocals) => new(
        vocabulary: Vocabulary,
        basePath: BasePath,
        constants: Constants,
        templates: Templates,
        sourceMap: SourceMap,
        diagnostics: Diagnostics,
        schema: Schema,
        currentPointer: CurrentPointer
    ) {
        Locals = lambdaLocals,
        m_values = m_values,
        m_arguments = m_arguments,
        m_templateScopes = m_templateScopes,
        m_evaluating = m_evaluating,
        Budget = Budget,
    };
}
/// <summary>JSON helpers every lowering pass needs and none should restate.</summary>
public static class JsonNodeExtensions {
    /// <summary>Appends a node to an array, keeping a null as a JSON null rather than refusing it.</summary>
    /// <param name="array">The array to append to.</param>
    /// <param name="item">The node, which may be null.</param>
    public static void AppendNode(this JsonArray array, JsonNode? item) {
        ArgumentNullException.ThrowIfNull(array);

        ((IList<JsonNode?>)array).Add(item);
    }
}
