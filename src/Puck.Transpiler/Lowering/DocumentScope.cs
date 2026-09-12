using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Units;

namespace Puck.Transpiler.Lowering;

/// <summary>The two questions generic value lowering cannot answer for itself, because both are facts about a
/// particular document schema rather than about the language.</summary>
public interface IDocumentVocabulary {
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
        Locals = Locals,
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
