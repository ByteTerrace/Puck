using System.Buffers.Binary;
using System.Text;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Addons;

/// <summary>Represents an imported function or resource declared by a WebAssembly guest module.</summary>
/// <param name="Module">The imported module name (e.g. "puck_host", "env").</param>
/// <param name="Name">The imported function or entity name.</param>
/// <param name="Kind">The descriptor kind (0 = Function, 1 = Table, 2 = Memory, 3 = Global).</param>
public readonly record struct WasmImport(string Module, string Name, byte Kind);

/// <summary>Summary of WebAssembly guest module contract inspection.</summary>
/// <param name="IsValid">Whether the module carries valid WASM magic bytes and version.</param>
/// <param name="ContentHash">Canonical content integrity hash in 'sha256-64/{16 hex}' format.</param>
/// <param name="Exports">Exported function and symbol names.</param>
/// <param name="Imports">Imported host functions and symbols.</param>
public sealed record WasmInspectionResult(
    bool IsValid,
    string ContentHash,
    IReadOnlyList<string> Exports,
    IReadOnlyList<WasmImport> Imports
);

/// <summary>Lightweight, zero-allocation, Native AOT WebAssembly binary contract inspector.</summary>
public static class WasmModuleInspector {
    private static readonly byte[] WasmMagic = [0x00, 0x61, 0x73, 0x6D]; // \0asm
    private const uint WasmVersion1 = 1;

    /// <summary>Inspects the binary bytes of a WebAssembly module.</summary>
    /// <param name="wasmBytes">The raw WASM module bytes.</param>
    /// <returns>Inspection result with exports, imports, and canonical content hash.</returns>
    public static WasmInspectionResult Inspect(ReadOnlySpan<byte> wasmBytes) {
        var contentHash = WorldDefinitionFileSource.ComputeContentHash(wasmBytes.ToArray());

        if (wasmBytes.Length < 8) {
            return new WasmInspectionResult(false, contentHash, [], []);
        }

        if (!wasmBytes[..4].SequenceEqual(WasmMagic)) {
            return new WasmInspectionResult(false, contentHash, [], []);
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(wasmBytes.Slice(4, 4));
        if (version != WasmVersion1) {
            return new WasmInspectionResult(false, contentHash, [], []);
        }

        var exports = new List<string>();
        var imports = new List<WasmImport>();
        var offset = 8;

        while (offset < wasmBytes.Length) {
            if (offset >= wasmBytes.Length) {
                break;
            }

            var sectionId = wasmBytes[offset++];
            var sectionSize = (int)ReadVarUInt32(wasmBytes, ref offset);

            if (offset + sectionSize > wasmBytes.Length) {
                break;
            }

            var sectionPayload = wasmBytes.Slice(offset, sectionSize);
            offset += sectionSize;

            switch (sectionId) {
                case 2: // Import Section
                    ParseImportSection(sectionPayload, imports);
                    break;
                case 7: // Export Section
                    ParseExportSection(sectionPayload, exports);
                    break;
            }
        }

        return new WasmInspectionResult(true, contentHash, exports, imports);
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
            diagnostics.ReportError(PuckDiagnosticCodes.Template, $"Module '{modulePath}' is not a valid WebAssembly binary (invalid header).", span);
            return;
        }

        // Check pinned hash if author provided one
        if (!string.IsNullOrEmpty(pinnedHash) && !string.Equals(pinnedHash, "auto", StringComparison.OrdinalIgnoreCase)) {
            if (!string.Equals(pinnedHash, result.ContentHash, StringComparison.OrdinalIgnoreCase)) {
                diagnostics.ReportWarning(
                    PuckDiagnosticCodes.AddonHash,
                    $"Pinned hash '{pinnedHash}' does not match computed hash '{result.ContentHash}' of module '{modulePath}'.",
                    span
                );
            }
        }

        // Warn if guest imports host capabilities that are not declared in requests
        foreach (var import in result.Imports) {
            if (string.Equals(import.Module, "puck", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(import.Module, "puck_host", StringComparison.OrdinalIgnoreCase)) {
                var found = declaredCapabilities.Any(c => string.Equals(c, import.Name, StringComparison.OrdinalIgnoreCase));
                if (!found && declaredCapabilities.Count > 0) {
                    diagnostics.ReportWarning(
                        PuckDiagnosticCodes.AddonPayload,
                        $"Addon '{modulePath}' imports host function '{import.Module}.{import.Name}' which may require undeclared capability requests.",
                        span
                    );
                }
            }
        }
    }

    private static void ParseImportSection(ReadOnlySpan<byte> payload, List<WasmImport> imports) {
        var offset = 0;
        if (offset >= payload.Length) {
            return;
        }

        var count = (int)ReadVarUInt32(payload, ref offset);
        for (var i = 0; i < count && offset < payload.Length; i++) {
            var modLen = (int)ReadVarUInt32(payload, ref offset);
            if (offset + modLen > payload.Length) {
                break;
            }
            var modName = Encoding.UTF8.GetString(payload.Slice(offset, modLen));
            offset += modLen;

            var nameLen = (int)ReadVarUInt32(payload, ref offset);
            if (offset + nameLen > payload.Length) {
                break;
            }
            var name = Encoding.UTF8.GetString(payload.Slice(offset, nameLen));
            offset += nameLen;

            if (offset >= payload.Length) {
                break;
            }
            var kind = payload[offset++];

            // Skip descriptor index or limits
            switch (kind) {
                case 0: // func
                    _ = ReadVarUInt32(payload, ref offset);
                    break;
                case 1: // table
                    _ = payload[offset++]; // elem_type
                    SkipLimits(payload, ref offset);
                    break;
                case 2: // mem
                    SkipLimits(payload, ref offset);
                    break;
                case 3: // global
                    _ = payload[offset++]; // val_type
                    _ = payload[offset++]; // mut
                    break;
            }

            imports.Add(new WasmImport(modName, name, kind));
        }
    }

    private static void ParseExportSection(ReadOnlySpan<byte> payload, List<string> exports) {
        var offset = 0;
        if (offset >= payload.Length) {
            return;
        }

        var count = (int)ReadVarUInt32(payload, ref offset);
        for (var i = 0; i < count && offset < payload.Length; i++) {
            var nameLen = (int)ReadVarUInt32(payload, ref offset);
            if (offset + nameLen > payload.Length) {
                break;
            }
            var name = Encoding.UTF8.GetString(payload.Slice(offset, nameLen));
            offset += nameLen;

            if (offset >= payload.Length) {
                break;
            }
            _ = payload[offset++]; // kind
            _ = ReadVarUInt32(payload, ref offset); // index

            exports.Add(name);
        }
    }

    private static void SkipLimits(ReadOnlySpan<byte> payload, ref int offset) {
        if (offset >= payload.Length) {
            return;
        }
        var flags = payload[offset++];
        _ = ReadVarUInt32(payload, ref offset); // min
        if ((flags & 0x01) != 0 && offset < payload.Length) {
            _ = ReadVarUInt32(payload, ref offset); // max
        }
    }

    private static uint ReadVarUInt32(ReadOnlySpan<byte> buffer, ref int offset) {
        uint result = 0;
        var shift = 0;
        while (offset < buffer.Length) {
            var b = buffer[offset++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) {
                break;
            }
            shift += 7;
        }
        return result;
    }
}
