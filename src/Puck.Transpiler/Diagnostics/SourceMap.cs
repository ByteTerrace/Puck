namespace Puck.Transpiler.Diagnostics;

/// <summary>Maintains bidirectional mapping between canonical JSON pointers and source AST spans.</summary>
public sealed class SourceMap {
    private readonly Dictionary<string, SourceSpan> m_pathToSpan = new(comparer: StringComparer.OrdinalIgnoreCase);

    private static string NormalizePointer(string pointer) {
        if (string.IsNullOrEmpty(value: pointer)) {
            return "";
        }
        if (!pointer.StartsWith(value: '/')) {
            pointer = ("/" + pointer);
        }
        return pointer.TrimEnd(trimChar: '/');
    }

    /// <summary>Registers a JSON pointer path to a source text span.</summary>
    /// <param name="jsonPointer">RFC 6901 JSON pointer (e.g., "/views/layouts/0/study").</param>
    /// <param name="span">The AST node source span.</param>
    public void Register(string jsonPointer, SourceSpan span) {
        if (
            string.IsNullOrEmpty(value: jsonPointer) ||
            (span.Line <= 0)
        ) {
            return;
        }

        var normalized = NormalizePointer(pointer: jsonPointer);

        m_pathToSpan[normalized] = span;
    }
    /// <summary>Attempts to resolve a JSON pointer path to its originating source text span.</summary>
    /// <param name="jsonPointer">The candidate JSON pointer path.</param>
    /// <param name="span">The matched or closest ancestor span.</param>
    /// <returns>True if a match or ancestor match was found.</returns>
    public bool TryGetSpan(string jsonPointer, out SourceSpan span) {
        var pointer = NormalizePointer(pointer: jsonPointer);

        // Exact match
        if (m_pathToSpan.TryGetValue(
            key: pointer,
            value: out span
        )) {
            return true;
        }

        // Walk up ancestor path segments: /a/b/c -> /a/b -> /a -> ""
        while (true) {
            var lastSlash = pointer.LastIndexOf(value: '/');

            if (lastSlash <= 0) {
                break;
            }

            pointer = pointer[..lastSlash];
            if (m_pathToSpan.TryGetValue(
                key: pointer,
                value: out span
            )) {
                return true;
            }
        }

        span = SourceSpan.None;
        return false;
    }
}
