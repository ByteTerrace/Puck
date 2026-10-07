using Puck.Assets;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Addons;

/// <summary>Summary of WebAssembly guest module contract inspection.</summary>
/// <param name="IsValid">Whether the module carries the WASM magic bytes and version and every section, length and
/// name in it lies inside the bytes it claims. A module that is not valid reports no exports and no imports.</param>
/// <param name="ContentHash">Canonical content integrity hash in 'sha256-64/{16 hex}' format.</param>
/// <param name="Exports">Exported function and symbol names.</param>
/// <param name="Imports">Imported host functions and symbols.</param>
public sealed record WasmInspectionResult(
    bool IsValid,
    string ContentHash,
    IReadOnlyList<string> Exports,
    IReadOnlyList<WasmImport> Imports
);
/// <summary>Reads a WebAssembly binary's imports and exports without instantiating it.</summary>
public static class WasmModuleInspector {
    /// <summary>Inspects the binary bytes of a WebAssembly module.</summary>
    /// <param name="wasmBytes">The raw WASM module bytes.</param>
    /// <returns>Inspection result with exports, imports, and canonical content hash.</returns>
    public static WasmInspectionResult Inspect(ReadOnlySpan<byte> wasmBytes) {
        var contentHash = WorldDefinitionFileSource.ComputeContentHash(content: wasmBytes.ToArray());

        return (WasmModuleDeclarations.TryRead(
            declarations: out var declarations,
            error: out _,
            module: wasmBytes
        )
            ? new WasmInspectionResult(
                ContentHash: contentHash,
                Exports: declarations.Exports,
                Imports: declarations.Imports,
                IsValid: true
            )
            : new WasmInspectionResult(
                ContentHash: contentHash,
                Exports: [],
                Imports: [],
                IsValid: false
            )
        );
    }
    /// <summary>Validates the inspected module against declared world capability requests.</summary>
    public static void ValidateContract(
        WasmInspectionResult result,
        string modulePath,
        string? pinnedHash,
        IReadOnlyList<string> declaredCapabilities,
        DiagnosticBag diagnostics,
        SourceSpan span
    ) {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!result.IsValid) {
            diagnostics.ReportError(
                code: PuckDiagnosticCodes.Template,
                message: $"Module '{modulePath}' is not a well-formed WebAssembly binary: its header, a section length, or an import or export entry runs past the bytes it claims.",
                span: span
            );
            return;
        }

        // Check pinned hash if author provided one
        if (
            !string.IsNullOrEmpty(value: pinnedHash) &&
            !string.Equals(
            a: pinnedHash,
            b: "auto",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
        ) {
            if (!string.Equals(
                a: pinnedHash,
                b: result.ContentHash,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                diagnostics.ReportWarning(
                    code: PuckDiagnosticCodes.AddonHash,
                    message: $"Pinned hash '{pinnedHash}' does not match computed hash '{result.ContentHash}' of module '{modulePath}'.",
                    span: span
                );
            }
        }

        // Warn if guest imports host capabilities that are not declared in requests
        foreach (var import in result.Imports) {
            if (
                string.Equals(
                a: import.Module,
                b: "puck",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) ||
                string.Equals(
                a: import.Module,
                b: "puck_host",
                comparisonType: StringComparison.OrdinalIgnoreCase
            )
            ) {
                var found = declaredCapabilities.Any(predicate: c => string.Equals(
                    a: c,
                    b: import.Name,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                ));

                if (
                    !found &&
                    (declaredCapabilities.Count > 0)
                ) {
                    diagnostics.ReportWarning(
                        code: PuckDiagnosticCodes.AddonPayload,
                        message: $"Addon '{modulePath}' imports host function '{import.Module}.{import.Name}' which may require undeclared capability requests.",
                        span: span
                    );
                }
            }
        }
    }
}
