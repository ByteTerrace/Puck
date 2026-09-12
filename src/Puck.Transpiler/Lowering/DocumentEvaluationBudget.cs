using Puck.Transpiler.Diagnostics;
using System.Text.Json.Nodes;

namespace Puck.Transpiler.Lowering;

/// <summary>Shared limits for one compilation, including nested bindings, templates and collection operations.</summary>
public sealed class DocumentEvaluationBudget {
    /// <summary>The maximum evaluated nodes and generated collection elements in one compilation.</summary>
    public const int WorkLimit = 4_000_000;
    /// <summary>The maximum elements in one compile-time collection.</summary>
    public const int CollectionLimit = 1_000_000;
    /// <summary>The maximum simultaneously active evaluations or template expansions.</summary>
    public const int DepthLimit = 64;
    /// <summary>The maximum source or generated string length in characters.</summary>
    public const int TextLimit = 4_000_000;
    private int m_work;
    private int m_depth;
    /// <summary>Gets the cancellation signal shared by all evaluations in this compilation.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Charges bounded work before allocating or expanding it.</summary>
    /// <param name="count">The nonnegative work count.</param>
    /// <param name="span">The source responsible for the work.</param>
    /// <exception cref="DocumentEvaluationException">The compilation has exhausted its budget.</exception>
    public void Spend(long count, SourceSpan span) {
        CancellationToken.ThrowIfCancellationRequested();
        if (count < 0 || count > WorkLimit - m_work) {
            throw new DocumentEvaluationException("The compilation exceeds its work budget; split the generated data into smaller documents.", span);
        }
        m_work += (int)count;
    }

    /// <summary>Copies a borrowed value into owned output, charging every copied node and string before allocation.</summary>
    /// <param name="value">The borrowed value.</param>
    /// <param name="span">The expression requesting a copy.</param>
    /// <returns>An independently owned JSON value.</returns>
    public JsonNode? Copy(JsonNode? value, SourceSpan span) {
        Charge(value, 0);
        return value?.DeepClone();
        void Charge(JsonNode? node, int depth) {
            if (depth >= DepthLimit) { throw new DocumentEvaluationException("Generated JSON exceeds 64 levels of nesting.", span); }
            Spend(1, span);
            switch (node) {
                case JsonArray array:
                    foreach (var item in array) { Charge(item, depth + 1); }
                    break;
                case JsonObject obj:
                    foreach (var pair in obj) { Spend(pair.Key.Length, span); Charge(pair.Value, depth + 1); }
                    break;
                case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                    Spend(text.Length, span);
                    break;
            }
        }
    }

    /// <summary>Checks and charges a collection before constructing it.</summary>
    /// <param name="count">The requested number of elements.</param>
    /// <param name="span">The source collection.</param>
    public void Collection(long count, SourceSpan span) {
        if (count < 0 || count > CollectionLimit) {
            throw new DocumentEvaluationException($"A compile-time collection contains at most {CollectionLimit} elements.", span);
        }
        Spend(count, span);
    }

    /// <summary>Enters one recursive evaluation, charging work and depth.</summary>
    /// <param name="span">The evaluated source.</param>
    /// <returns>A scope that releases depth when disposed.</returns>
    public Evaluation Enter(SourceSpan span) {
        Spend(1, span);
        if (m_depth >= DepthLimit) {
            throw new DocumentEvaluationException($"Evaluation nests at most {DepthLimit} levels; check for recursive bindings or templates.", span);
        }
        ++m_depth;
        return new Evaluation(this);
    }

    /// <summary>One active evaluation depth.</summary>
    public readonly struct Evaluation : IDisposable {
        private readonly DocumentEvaluationBudget m_budget;
        internal Evaluation(DocumentEvaluationBudget budget) => m_budget = budget;
        /// <summary>Leaves the evaluation.</summary>
        public void Dispose() => --m_budget.m_depth;
    }
}

/// <summary>A source-located refusal that stops an unsafe or undefined evaluation.</summary>
public sealed class DocumentEvaluationException : Exception {
    /// <summary>Gets the source of the refusal.</summary>
    public SourceSpan Span { get; }
    /// <summary>Gets the diagnostic code.</summary>
    public string Code { get; }
    /// <summary>Creates an evaluation refusal.</summary>
    /// <param name="message">The actionable diagnostic.</param>
    /// <param name="span">The responsible source.</param>
    /// <param name="code">The diagnostic code; a work-limit refusal by default.</param>
    public DocumentEvaluationException(string message, SourceSpan span, string code = PuckDiagnosticCodes.EvaluationLimit) : base(message) {
        Span = span;
        Code = code;
    }
}
