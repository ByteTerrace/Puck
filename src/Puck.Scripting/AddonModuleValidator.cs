using Wasmtime;

using Module = Wasmtime.Module;

namespace Puck.Scripting;

/// <summary>The static export-shape check run against a compiled module before it is ever instantiated: every
/// required export (A.1) must be present with its exact signature and the linear memory must be exported. On
/// failure the error names the offending export, matching the E.7 diagnostic shape.</summary>
public static class AddonModuleValidator {
    /// <summary>Validates that <paramref name="module"/> exports the frozen <c>puck.addon.v1</c> surface.</summary>
    /// <param name="module">The compiled module to validate.</param>
    /// <param name="error">When this returns <see langword="false"/>, the offending-export message; otherwise empty.</param>
    /// <returns><see langword="true"/> if the export surface matches the ABI; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="module"/> is <see langword="null"/>.</exception>
    public static bool TryValidate(Module module, out string error) {
        ArgumentNullException.ThrowIfNull(argument: module);

        var exports = module.Exports;

        if (!HasMemory(exports: exports)) {
            error = $"export '{AddonAbi.Exports.Memory}' missing or not a memory";
            return false;
        }

        if (!RequireNullaryI32(error: out error, exports: exports, name: AddonAbi.Exports.AbiVersion)
            || !RequireNullaryI32(error: out error, exports: exports, name: AddonAbi.Exports.SnapshotPtr)
            || !RequireNullaryI32(error: out error, exports: exports, name: AddonAbi.Exports.CommandsPtr)
            || !RequireNullaryI32(error: out error, exports: exports, name: AddonAbi.Exports.CommandsCap)
            || !RequireNullaryI32(error: out error, exports: exports, name: AddonAbi.Exports.OnTick)) {
            return false;
        }

        return ValidateOptionalInit(error: out error, exports: exports);
    }

    private static FunctionExport? FindFunction(IReadOnlyList<Export> exports, string name) {
        foreach (var export in exports) {
            if ((export is FunctionExport function) && string.Equals(a: export.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return function;
            }
        }

        return null;
    }
    private static bool HasMemory(IReadOnlyList<Export> exports) {
        foreach (var export in exports) {
            if ((export is MemoryExport) && string.Equals(a: export.Name, b: AddonAbi.Exports.Memory, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
    private static bool IsNullaryI32(FunctionExport function) {
        return ((function.Parameters.Count == 0) && (function.Results.Count == 1) && (function.Results[0] == ValueKind.Int32));
    }
    private static bool RequireNullaryI32(IReadOnlyList<Export> exports, string name, out string error) {
        var function = FindFunction(
            exports: exports,
            name: name
        );

        if ((function is null) || !IsNullaryI32(function: function)) {
            error = $"export '{name}' missing or not ()->i32";
            return false;
        }

        error = "";
        return true;
    }
    private static bool ValidateOptionalInit(IReadOnlyList<Export> exports, out string error) {
        var function = FindFunction(
            exports: exports,
            name: AddonAbi.Exports.Init
        );

        if ((function is not null) && ((function.Parameters.Count != 0) || (function.Results.Count != 0))) {
            error = $"export '{AddonAbi.Exports.Init}' present but not ()->()";
            return false;
        }

        error = "";
        return true;
    }
}
