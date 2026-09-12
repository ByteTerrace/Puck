namespace Puck.Transpiler.Diagnostics;

/// <summary>Maintains bidirectional mapping between canonical JSON pointers and source AST spans.</summary>
public sealed class SourceMap {
    private readonly Dictionary<string, SourceSpan> m_pathToSpan = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a JSON pointer path to a source text span.</summary>
    /// <param name="jsonPointer">RFC 6901 JSON pointer (e.g., "/views/layouts/0/study").</param>
    /// <param name="span">The AST node source span.</param>
    public void Register(string jsonPointer, SourceSpan span) {
        if (string.IsNullOrEmpty(jsonPointer) || span.Line <= 0) {
            return;
        }

        var normalized = NormalizePointer(jsonPointer);
        m_pathToSpan[normalized] = span;
    }

    /// <summary>Attempts to resolve a JSON pointer path to its originating source text span.</summary>
    /// <param name="jsonPointer">The candidate JSON pointer path.</param>
    /// <param name="span">The matched or closest ancestor span.</param>
    /// <returns>True if a match or ancestor match was found.</returns>
    public bool TryGetSpan(string jsonPointer, out SourceSpan span) {
        var pointer = NormalizePointer(jsonPointer);

        // Exact match
        if (m_pathToSpan.TryGetValue(pointer, out span)) {
            return true;
        }

        // Walk up ancestor path segments: /a/b/c -> /a/b -> /a -> ""
        while (true) {
            var lastSlash = pointer.LastIndexOf('/');
            if (lastSlash <= 0) {
                break;
            }

            pointer = pointer[..lastSlash];
            if (m_pathToSpan.TryGetValue(pointer, out span)) {
                return true;
            }
        }

        span = SourceSpan.None;
        return false;
    }

    private static string NormalizePointer(string pointer) {
        if (string.IsNullOrEmpty(pointer)) {
            return "";
        }
        if (!pointer.StartsWith('/')) {
            pointer = "/" + pointer;
        }
        return pointer.TrimEnd('/');
    }
}
