namespace Puck.Transpiler.Diagnostics;

/// <summary>Severity classification for compiler diagnostics.</summary>
public enum DiagnosticSeverity {
    /// <summary>Informational message or suggestion.</summary>
    Information,
    /// <summary>Potential issue or deprecation that does not prevent compilation.</summary>
    Warning,
    /// <summary>Fatal or semantic defect that halts valid code generation.</summary>
    Error
}
