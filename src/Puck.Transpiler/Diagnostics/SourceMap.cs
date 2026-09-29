using Puck.Transpiler.Lowering;

namespace Puck.Transpiler.Diagnostics;

/// <summary>The source provenance retained for one lowered JSON location.</summary>
/// <param name="Span">The defining syntax span.</param>
/// <param name="SourcePath">The source document that defines the syntax, or <see langword="null"/> for in-memory input.</param>
/// <param name="ModuleInstancePath">The slash-separated module instance path, or <see langword="null"/> outside an instance.</param>
public sealed record SourceOrigin(SourceSpan Span, string? SourcePath, string? ModuleInstancePath) {
    /// <summary>Whether this entry defines a diagnostic node, rather than only a replaceable value inside one.</summary>
    public bool DefinesNode { get; init; } = true;
    /// <summary>The authored value expression inside the defining node, when generic value lowering recorded one.</summary>
    public SourceSpan? ValueSpan { get; init; }
    /// <summary>The vocabulary's spelling for a replacement value at this position.</summary>
    public DocumentValueForm ValueForm { get; init; }
}
/// <summary>Maintains bidirectional mapping between canonical JSON pointers and source AST origins.</summary>
public sealed class SourceMap {
    private Dictionary<string, SourceOrigin> m_pathToOrigin = new(comparer: StringComparer.OrdinalIgnoreCase);
    private string? m_sourcePath;
    private string? m_moduleInstancePath;

    private static string NormalizePointer(string pointer) {
        if (string.IsNullOrEmpty(value: pointer)) {
            return "";
        }
        if (!pointer.StartsWith(value: '/')) {
            pointer = ("/" + pointer);
        }
        return pointer.TrimEnd(trimChar: '/');
    }

    /// <summary>Captures the current pointer map so a speculative lowering can be rolled back.</summary>
    public IReadOnlyDictionary<string, SourceOrigin> Snapshot() => new Dictionary<string, SourceOrigin>(m_pathToOrigin, StringComparer.OrdinalIgnoreCase);
    /// <summary>Restores a previously captured pointer map.</summary>
    public void Restore(IReadOnlyDictionary<string, SourceOrigin> snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        m_pathToOrigin.Clear();
        foreach (var entry in snapshot) { m_pathToOrigin[entry.Key] = entry.Value; }
    }
    /// <summary>Temporarily records registrations in a fresh pointer map, restoring the enclosing map in constant
    /// time when the returned scope is disposed.</summary>
    /// <returns>A token that restores the enclosing pointer map when disposed.</returns>
    public IDisposable PushIsolatedEntries() {
        var previous = m_pathToOrigin;

        m_pathToOrigin = new(comparer: StringComparer.OrdinalIgnoreCase);
        return new EntryScope(owner: this, previous: previous);
    }
    /// <summary>Temporarily supplies source provenance for registrations made inside a lowering scope.</summary>
    /// <param name="sourcePath">A defining source path, or <see langword="null"/> to inherit the current path.</param>
    /// <param name="moduleInstance">A module instance segment to append, or <see langword="null"/> to inherit the current instance path.</param>
    /// <returns>A token that restores the enclosing origin when disposed.</returns>
    public IDisposable PushOrigin(string? sourcePath = null, string? moduleInstance = null) {
        var previousSourcePath = m_sourcePath;
        var previousModuleInstancePath = m_moduleInstancePath;

        if (sourcePath is not null) { m_sourcePath = sourcePath; }
        if (moduleInstance is not null) {
            m_moduleInstancePath = (string.IsNullOrEmpty(value: m_moduleInstancePath)
                ? moduleInstance
                : $"{m_moduleInstancePath}/{moduleInstance}");
        }
        return new OriginScope(moduleInstancePath: previousModuleInstancePath, owner: this, sourcePath: previousSourcePath);
    }
    /// <summary>Registers a JSON pointer path to a source text span under the current origin scope.</summary>
    public void Register(string jsonPointer, SourceSpan span) => Register(
        jsonPointer: jsonPointer,
        origin: CaptureOrigin(span: span)
    );
    /// <summary>Captures a span together with the source and module instance active in this map.</summary>
    /// <param name="span">The source text span to associate with the active origin.</param>
    /// <returns>The complete origin for deferred registration.</returns>
    public SourceOrigin CaptureOrigin(SourceSpan span) => new(ModuleInstancePath: m_moduleInstancePath, SourcePath: m_sourcePath, Span: span);
    /// <summary>Records a value expression without discarding its enclosing authored property's span.</summary>
    /// <param name="jsonPointer">The value's exact JSON pointer.</param>
    /// <param name="span">The value expression's span.</param>
    /// <param name="form">The vocabulary's spelling of the value.</param>
    public void RegisterValue(string jsonPointer, SourceSpan span, DocumentValueForm form) {
        var pointer = NormalizePointer(pointer: jsonPointer);
        var origin = (m_pathToOrigin.TryGetValue(key: pointer, value: out var existing) ? existing : CaptureOrigin(span: span) with { DefinesNode = false });

        Register(jsonPointer: pointer, origin: origin with { ValueSpan = span, ValueForm = form });
    }
    /// <summary>Registers a JSON pointer path to an already captured source origin.</summary>
    public void Register(string jsonPointer, SourceOrigin origin) {
        ArgumentNullException.ThrowIfNull(origin);
        if (string.IsNullOrEmpty(value: jsonPointer) || (origin.Span.Line <= 0)) { return; }
        var pointer = NormalizePointer(pointer: jsonPointer);

        if (origin.DefinesNode && (origin.ValueSpan is null) && m_pathToOrigin.TryGetValue(key: pointer, value: out var previous)) {
            origin = origin with { ValueSpan = previous.ValueSpan, ValueForm = previous.ValueForm };
        }
        m_pathToOrigin[pointer] = origin;
    }
    /// <summary>Attempts to resolve a JSON pointer path to its originating source text span.</summary>
    public bool TryGetSpan(string jsonPointer, out SourceSpan span) {
        if (TryGetOrigin(jsonPointer: jsonPointer, origin: out var origin)) {
            span = origin.Span;
            return true;
        }
        span = SourceSpan.None;
        return false;
    }
    /// <summary>Attempts to resolve a JSON pointer path to its originating span, source, and module instance.</summary>
    public bool TryGetOrigin(string jsonPointer, out SourceOrigin origin) {
        var pointer = NormalizePointer(pointer: jsonPointer);

        if (m_pathToOrigin.TryGetValue(key: pointer, value: out origin!) && origin.DefinesNode) { return true; }
        while (true) {
            var lastSlash = pointer.LastIndexOf(value: '/');

            if (lastSlash <= 0) { break; }
            pointer = pointer[..lastSlash];
            if (m_pathToOrigin.TryGetValue(key: pointer, value: out origin!) && origin.DefinesNode) { return true; }
        }
        origin = new SourceOrigin(ModuleInstancePath: null, SourcePath: null, Span: SourceSpan.None);
        return false;
    }

    private sealed class OriginScope(SourceMap owner, string? sourcePath, string? moduleInstancePath) : IDisposable {
        private SourceMap? m_owner = owner;

        public void Dispose() {
            if (Interlocked.Exchange(location1: ref m_owner, value: null) is not { } current) { return; }
            current.m_sourcePath = sourcePath;
            current.m_moduleInstancePath = moduleInstancePath;
        }
    }
    private sealed class EntryScope(SourceMap owner, Dictionary<string, SourceOrigin> previous) : IDisposable {
        private SourceMap? m_owner = owner;

        public void Dispose() {
            if (Interlocked.Exchange(location1: ref m_owner, value: null) is { } current) { current.m_pathToOrigin = previous; }
        }
    }
}
