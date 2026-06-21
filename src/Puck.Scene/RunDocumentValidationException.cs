namespace Puck.Scene;

/// <summary>
/// Thrown when a <see cref="PuckRunDocument"/> fails semantic validation. It carries EVERY error found in the single
/// validation pass (not just the first), each a source-attributed message naming the offending field — so a bad
/// document is a clear, actionable failure here rather than a GPU crash or a silently wrong render downstream.
/// </summary>
public sealed class RunDocumentValidationException : Exception {
    /// <summary>Initializes a new instance of the <see cref="RunDocumentValidationException"/> class.</summary>
    /// <param name="errors">The collected, source-attributed validation messages.</param>
    public RunDocumentValidationException(IReadOnlyList<string> errors)
        : base(message: Format(errors: errors)) {
        Errors = errors;
    }

    /// <summary>The individual validation errors, each prefixed with the offending document path.</summary>
    public IReadOnlyList<string> Errors { get; }

    private static string Format(IReadOnlyList<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: errors);

        var builder = new System.Text.StringBuilder();

        _ = builder.Append(value: "The run document is invalid (").Append(value: errors.Count).Append(value: " error(s)):");

        foreach (var error in errors) {
            _ = builder.Append(value: Environment.NewLine).Append(value: "  - ").Append(value: error);
        }

        return builder.ToString();
    }
}
