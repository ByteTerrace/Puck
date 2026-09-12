using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler;

/// <summary>Represents the result of a compilation or parsing step, carrying any emitted diagnostics.</summary>
/// <typeparam name="T">The type of the compiled output.</typeparam>
/// <param name="Value">The parsed or compiled value, or null if fatal errors prevented creation.</param>
/// <param name="Diagnostics">The collection of errors, warnings, and informational diagnostics.</param>
public sealed record CompilationResult<T>(T? Value, DiagnosticBag Diagnostics) {
    /// <summary>Gets whether the step succeeded without fatal errors.</summary>
    public bool Success => !Diagnostics.HasErrors && Value is not null;

    /// <summary>Returns the compiled value, refusing to expose partial output after an error.</summary>
    /// <returns>The successful compiled value.</returns>
    /// <exception cref="InvalidOperationException">Compilation produced an error or no value.</exception>
    public T RequireValue() {
        if (!Success) {
            throw new InvalidOperationException(string.Join(Environment.NewLine, Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        }
        return Value!;
    }

    /// <summary>Creates a successful compilation result.</summary>
    public static CompilationResult<T> Succeeded(T value, DiagnosticBag? diagnostics = null) =>
        new(value, diagnostics ?? new DiagnosticBag());

    /// <summary>Creates a failed compilation result.</summary>
    public static CompilationResult<T> Failed(DiagnosticBag diagnostics) =>
        new(default, diagnostics);

    /// <summary>Deconstructs the compilation result into its value and diagnostic bag.</summary>
    public void Deconstruct(out T? value, out DiagnosticBag diagnostics) {
        value = Value;
        diagnostics = Diagnostics;
    }
}
