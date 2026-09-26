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
    // Each string node a vocabulary recorded as written beside a directory, by reference, so a record follows the node
    // through every copy this budget makes.
    private Dictionary<JsonNode, string>? m_writtenBeside;

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
        var copy = value?.DeepClone();

        if ((m_writtenBeside is { Count: > 0 }) && (value is not null)) {
            CarryWrittenBeside(copy: copy!, source: value);
        }

        return copy;
    }
    /// <summary>Records the directory of the source whose literal produced a string node, so the record follows the
    /// node and every copy <see cref="Copy"/> makes of it, whatever expression carries it.</summary>
    /// <param name="value">The string node, as the evaluation produced it.</param>
    /// <param name="directory">The full path of the writing source's directory.</param>
    public void NoteWrittenBeside(JsonValue value, string directory) {
        ArgumentNullException.ThrowIfNull(argument: value);
        ArgumentNullException.ThrowIfNull(argument: directory);

        (m_writtenBeside ??= new(comparer: ReferenceEqualityComparer.Instance))[value] = directory;
    }
    /// <summary>Returns the directory recorded for a node by <see cref="NoteWrittenBeside"/>, carried through copies.</summary>
    /// <param name="value">The node.</param>
    /// <param name="directory">The recorded directory, or empty when none is recorded.</param>
    /// <returns><see langword="true"/> when a directory is recorded for <paramref name="value"/>.</returns>
    public bool TryGetWrittenBeside(JsonNode? value, out string directory) {
        directory = string.Empty;

        return ((value is not null) && (m_writtenBeside?.TryGetValue(key: value, value: out directory!) == true));
    }

    // DeepClone keeps the shape and member order, so the copy is walked beside its source.
    private void CarryWrittenBeside(JsonNode source, JsonNode copy) {
        switch (source) {
            case JsonValue:
                if (m_writtenBeside!.TryGetValue(key: source, value: out var directory)) {
                    m_writtenBeside[copy] = directory;
                }
                break;
            case JsonArray array:
                for (var index = 0; (index < array.Count); index++) {
                    if (array[index] is { } element) {
                        CarryWrittenBeside(copy: ((JsonArray)copy)[index]!, source: element);
                    }
                }
                break;
            case JsonObject obj:
                foreach (var (key, member) in obj) {
                    if (member is not null) {
                        CarryWrittenBeside(copy: ((JsonObject)copy)[key]!, source: member);
                    }
                }
                break;
        }
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
