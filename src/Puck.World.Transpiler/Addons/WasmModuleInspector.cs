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
    private const uint WasmVersion1 = 1;

    private static readonly byte[] WasmMagic = [0x00, 0x61, 0x73, 0x6D]; // \0asm

    private static void ParseExportSection(ReadOnlySpan<byte> payload, List<string> exports) {
        var offset = 0;

        if (offset >= payload.Length) {
            return;
        }

        var count = ((int)ReadVarUInt32(
            buffer: payload,
            offset: ref offset
        ));

        for (var i = 0; ((i < count) && (offset < payload.Length)); i++) {
            var nameLen = ((int)ReadVarUInt32(
                buffer: payload,
                offset: ref offset
            ));

            if ((offset + nameLen) > payload.Length) {
                break;
            }
            var name = Encoding.UTF8.GetString(bytes: payload.Slice(
                length: nameLen,
                start: offset
            ));

            offset += nameLen;

            if (offset >= payload.Length) {
                break;
            }
            _ = payload[offset++]; // kind
            _ = ReadVarUInt32(
                buffer: payload,
                offset: ref offset
            ); // index

            exports.Add(item: name);
        }
    }
    private static void ParseImportSection(ReadOnlySpan<byte> payload, List<WasmImport> imports) {
        var offset = 0;

        if (offset >= payload.Length) {
            return;
        }

        var count = ((int)ReadVarUInt32(
            buffer: payload,
            offset: ref offset
        ));

        for (var i = 0; ((i < count) && (offset < payload.Length)); i++) {
            var modLen = ((int)ReadVarUInt32(
                buffer: payload,
                offset: ref offset
            ));

            if ((offset + modLen) > payload.Length) {
                break;
            }
            var modName = Encoding.UTF8.GetString(bytes: payload.Slice(
                length: modLen,
                start: offset
            ));

            offset += modLen;

            var nameLen = ((int)ReadVarUInt32(
                buffer: payload,
                offset: ref offset
            ));

            if ((offset + nameLen) > payload.Length) {
                break;
            }
            var name = Encoding.UTF8.GetString(bytes: payload.Slice(
                length: nameLen,
                start: offset
            ));

            offset += nameLen;

            if (offset >= payload.Length) {
                break;
            }
            var kind = payload[offset++];

            // Skip descriptor index or limits
            switch (kind) {
                case 0: // func
                    _ = ReadVarUInt32(
                        buffer: payload,
                        offset: ref offset
                    );
                    break;
                case 1: // table
                    _ = payload[offset++]; // elem_type
                    SkipLimits(
                        offset: ref offset,
                        payload: payload
                    );
                    break;
                case 2: // mem
                    SkipLimits(
                        offset: ref offset,
                        payload: payload
                    );
                    break;
                case 3: // global
                    _ = payload[offset++]; // val_type
                    _ = payload[offset++]; // mut
                    break;
            }

            imports.Add(item: new WasmImport(
                Kind: kind,
                Module: modName,
                Name: name
            ));
        }
    }
    private static uint ReadVarUInt32(ReadOnlySpan<byte> buffer, ref int offset) {
        var result = 0U;
        var shift = 0;

        while (offset < buffer.Length) {
            var b = buffer[offset++];

            result |= (((uint)(b & 0x7F)) << shift);
            if ((b & 0x80) == 0) {
                break;
            }
            shift += 7;
        }
        return result;
    }
    private static void SkipLimits(ReadOnlySpan<byte> payload, ref int offset) {
        if (offset >= payload.Length) {
            return;
        }
        var flags = payload[offset++];

        _ = ReadVarUInt32(
            buffer: payload,
            offset: ref offset
        ); // min
        if (
            ((flags & 0x01) != 0) &&
            (offset < payload.Length)
        ) {
            _ = ReadVarUInt32(
                buffer: payload,
                offset: ref offset
            ); // max
        }
    }

    /// <summary>Inspects the binary bytes of a WebAssembly module.</summary>
    /// <param name="wasmBytes">The raw WASM module bytes.</param>
    /// <returns>Inspection result with exports, imports, and canonical content hash.</returns>
    public static WasmInspectionResult Inspect(ReadOnlySpan<byte> wasmBytes) {
        var contentHash = WorldDefinitionFileSource.ComputeContentHash(content: wasmBytes.ToArray());

        if (wasmBytes.Length < 8) {
            return new WasmInspectionResult(
                ContentHash: contentHash,
                Exports: [],
                Imports: [],
                IsValid: false
            );
        }

        if (!wasmBytes[..4].SequenceEqual(other: WasmMagic)) {
            return new WasmInspectionResult(
                ContentHash: contentHash,
                Exports: [],
                Imports: [],
                IsValid: false
            );
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(source: wasmBytes.Slice(
            length: 4,
            start: 4
        ));

        if (version != WasmVersion1) {
            return new WasmInspectionResult(
                ContentHash: contentHash,
                Exports: [],
                Imports: [],
                IsValid: false
            );
        }

        var exports = new List<string>();
        var imports = new List<WasmImport>();
        var offset = 8;

        while (offset < wasmBytes.Length) {
            if (offset >= wasmBytes.Length) {
                break;
            }

            var sectionId = wasmBytes[offset++];
            var sectionSize = ((int)ReadVarUInt32(
                buffer: wasmBytes,
                offset: ref offset
            ));

            if ((offset + sectionSize) > wasmBytes.Length) {
                break;
            }

            var sectionPayload = wasmBytes.Slice(
                length: sectionSize,
                start: offset
            );

            offset += sectionSize;

            switch (sectionId) {
                case 2: // Import Section
                    ParseImportSection(
                        imports: imports,
                        payload: sectionPayload
                    );
                    break;
                case 7: // Export Section
                    ParseExportSection(
                        exports: exports,
                        payload: sectionPayload
                    );
                    break;
            }
        }

        return new WasmInspectionResult(
            ContentHash: contentHash,
            Exports: exports,
            Imports: imports,
            IsValid: true
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
                message: $"Module '{modulePath}' is not a valid WebAssembly binary (invalid header).",
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
