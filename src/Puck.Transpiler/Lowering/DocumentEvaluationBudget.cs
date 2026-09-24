using Puck.Transpiler.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Transpiler.Lowering;

/// <summary>Shared limits for one compilation, including nested bindings, templates and collection operations.</summary>
public sealed class DocumentEvaluationBudget {
    /// <summary>The maximum elements in one compile-time collection.</summary>
    public const int CollectionLimit = 1_000_000;
    /// <summary>The maximum simultaneously active evaluations or template expansions.</summary>
    public const int DepthLimit = 64;
    /// <summary>The maximum source or generated string length in characters.</summary>
    public const int TextLimit = 4_000_000;
    /// <summary>The maximum evaluated nodes and generated collection elements in one compilation.</summary>
    public const int WorkLimit = 4_000_000;

    private int m_depth;
    private int m_work;

    /// <summary>Gets the cancellation signal shared by all evaluations in this compilation.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Checks and charges a collection before constructing it.</summary>
    /// <param name="count">The requested number of elements.</param>
    /// <param name="span">The source collection.</param>
    public void Collection(long count, SourceSpan span) {
        if (
            (count < 0) ||
            (count > CollectionLimit)
        ) {
            throw new DocumentEvaluationException(
                $"A compile-time collection contains at most {CollectionLimit} elements.",
                span
            );
        }
        Spend(
            count: count,
            span: span
        );
    }
    /// <summary>Copies a borrowed value into owned output, charging every copied node and string before allocation.</summary>
    /// <param name="value">The borrowed value.</param>
    /// <param name="span">The expression requesting a copy.</param>
    /// <returns>An independently owned JSON value.</returns>
    public JsonNode? Copy(JsonNode? value, SourceSpan span) {
        Charge(
            depth: 0,
            node: value,
            span: span
        );
        return value?.DeepClone();
    }
    /// <summary>Takes a value the evaluation built itself into output, charging it exactly as <see cref="Copy"/>
    /// charges a borrowed one.</summary>
    /// <param name="value">A value no binding, cache or other container holds.</param>
    /// <param name="span">The expression that built it.</param>
    /// <returns><paramref name="value"/> itself.</returns>
    /// <remarks>A value reaches output either way at the same cost to the budget, so whether it had to be copied
    /// never changes what a compilation is allowed to produce.</remarks>
    public JsonNode? Adopt(JsonNode? value, SourceSpan span) {
        Charge(
            depth: 0,
            node: value,
            span: span
        );
        return value;
    }

    private void Charge(JsonNode? node, int depth, SourceSpan span) {
        if (depth >= DepthLimit) {
            throw new DocumentEvaluationException(
            "Generated JSON exceeds 64 levels of nesting.",
            span
        );
        }
        Spend(
            count: 1,
            span: span
        );
        switch (node) {
            case JsonArray array:
                for (var index = 0; (index < array.Count); index++) {
                    Charge(
                    depth: (depth + 1),
                    node: array[index],
                    span: span
                );
                }
                break;
            case JsonObject obj:
                for (var index = 0; (index < obj.Count); index++) {
                    var pair = obj.GetAt(index: index);

                    Spend(
                        count: pair.Key.Length,
                        span: span
                    );
                    Charge(
                        depth: (depth + 1),
                        node: pair.Value,
                        span: span
                    );
                }
                break;
            // Asking a number for text boxes it, so only a value that is text is asked.
            case JsonValue scalar when ((scalar.GetValueKind() == JsonValueKind.String) && scalar.TryGetValue<string>(value: out var text)):
                Spend(
                    count: text.Length,
                    span: span
                );
                break;
        }
    }

    /// <summary>Enters one recursive evaluation, charging work and depth.</summary>
    /// <param name="span">The evaluated source.</param>
    /// <returns>A scope that releases depth when disposed.</returns>
    public Evaluation Enter(SourceSpan span) {
        Spend(
            count: 1,
            span: span
        );
        if (m_depth >= DepthLimit) {
            throw new DocumentEvaluationException(
                $"Evaluation nests at most {DepthLimit} levels; check for recursive bindings or templates.",
                span
            );
        }
        ++m_depth;
        return new Evaluation(budget: this);
    }
    /// <summary>Charges bounded work before allocating or expanding it.</summary>
    /// <param name="count">The nonnegative work count.</param>
    /// <param name="span">The source responsible for the work.</param>
    /// <exception cref="DocumentEvaluationException">The compilation has exhausted its budget.</exception>
    public void Spend(long count, SourceSpan span) {
        CancellationToken.ThrowIfCancellationRequested();
        if (
            (count < 0) ||
            (count > (WorkLimit - m_work))
        ) {
            throw new DocumentEvaluationException(
                "The compilation exceeds its work budget; split the generated data into smaller documents.",
                span
            );
        }
        m_work += ((int)count);
    }

    /// <summary>One active evaluation depth.</summary>
    public readonly struct Evaluation : IDisposable {
        private readonly DocumentEvaluationBudget m_budget;

        internal Evaluation(DocumentEvaluationBudget budget) => m_budget = budget;

        /// <summary>Leaves the evaluation.</summary>
        public void Dispose() => --m_budget.m_depth;
    }
}
/// <summary>The refusal a read of a <see cref="DocumentScope.BindRefused"/> binding's value reports.</summary>
/// <param name="Code">The diagnostic code.</param>
/// <param name="Message">The refusal, naming the spellings that do read.</param>
public sealed record DocumentBindingRefusal(string Code, string Message);
/// <summary>A source-located refusal that stops an unsafe or undefined evaluation.</summary>
public sealed class DocumentEvaluationException : Exception {
    /// <summary>Gets the diagnostic code.</summary>
    public string Code { get; }
    /// <summary>Gets the source of the refusal.</summary>
    public SourceSpan Span { get; }

    /// <summary>Creates an evaluation refusal.</summary>
    /// <param name="message">The actionable diagnostic.</param>
    /// <param name="span">The responsible source.</param>
    /// <param name="code">The diagnostic code; a work-limit refusal by default.</param>
    public DocumentEvaluationException(string message, SourceSpan span, string code = PuckDiagnosticCodes.EvaluationLimit) : base(message) {
        Span = span;
        Code = code;
    }
}
