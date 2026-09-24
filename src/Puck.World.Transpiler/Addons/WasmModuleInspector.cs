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
    private const uint WasmVersion1 = 1;

    private static readonly byte[] WasmMagic = [0x00, 0x61, 0x73, 0x6D]; // \0asm

    // Every read is checked against what remains, and a length is compared as the unsigned value it was encoded as
    // before it is narrowed: a module is bytes an author points at, so nothing in it is trusted to be in range.
    private ref struct Reader(ReadOnlySpan<byte> bytes) {
        private readonly ReadOnlySpan<byte> m_bytes = bytes;

        private int m_offset;

        public readonly bool AtEnd => (m_offset >= m_bytes.Length);

        public bool TryReadByte(out byte value) {
            if (m_offset >= m_bytes.Length) {
                value = 0;

                return false;
            }

            value = m_bytes[m_offset++];

            return true;
        }
        public bool TryReadBytes(uint length, out ReadOnlySpan<byte> value) {
            if (length > ((uint)(m_bytes.Length - m_offset))) {
                value = default;

                return false;
            }

            value = m_bytes.Slice(
                length: ((int)length),
                start: m_offset
            );
            m_offset += ((int)length);

            return true;
        }
        // An unsigned LEB128 of at most 32 bits: five bytes, the fifth carrying four.
        public bool TryReadVarUInt32(out uint value) {
            value = 0U;

            for (var shift = 0; (shift < 35); shift += 7) {
                if (!TryReadByte(value: out var next)) {
                    return false;
                }
                if ((shift == 28) && ((next & 0xF0) != 0)) {
                    return false;
                }

                value |= (((uint)(next & 0x7F)) << shift);

                if ((next & 0x80) == 0) {
                    return true;
                }
            }

            return false;
        }
        public bool TryReadName(out string value) {
            if (
                !TryReadVarUInt32(value: out var length) ||
                !TryReadBytes(
                    length: length,
                    value: out var bytes
                )
            ) {
                value = string.Empty;

                return false;
            }

            value = Encoding.UTF8.GetString(bytes: bytes);

            return true;
        }
        public bool TrySkipLimits() {
            if (
                !TryReadByte(value: out var flags) ||
                !TryReadVarUInt32(value: out _)
            ) {
                return false;
            }

            return (((flags & 0x01) == 0) || TryReadVarUInt32(value: out _));
        }
    }

    private static bool TryParseExportSection(ReadOnlySpan<byte> payload, List<string> exports) {
        var reader = new Reader(bytes: payload);

        if (!reader.TryReadVarUInt32(value: out var count)) {
            return false;
        }

        for (var index = 0U; (index < count); index++) {
            if (
                !reader.TryReadName(value: out var name) ||
                !reader.TryReadByte(value: out _) ||
                !reader.TryReadVarUInt32(value: out _)
            ) {
                return false;
            }

            exports.Add(item: name);
        }

        return true;
    }
    private static bool TryParseImportSection(ReadOnlySpan<byte> payload, List<WasmImport> imports) {
        var reader = new Reader(bytes: payload);

        if (!reader.TryReadVarUInt32(value: out var count)) {
            return false;
        }

        for (var index = 0U; (index < count); index++) {
            if (
                !reader.TryReadName(value: out var module) ||
                !reader.TryReadName(value: out var name) ||
                !reader.TryReadByte(value: out var kind)
            ) {
                return false;
            }

            // The descriptor after the kind: a function's type index, a table's element type and limits, a
            // memory's limits, a global's value type and mutability, or a tag's attribute and type index.
            var described = (kind switch {
                0 => reader.TryReadVarUInt32(value: out _),
                1 => (reader.TryReadByte(value: out _) && reader.TrySkipLimits()),
                2 => reader.TrySkipLimits(),
                3 => (reader.TryReadByte(value: out _) && reader.TryReadByte(value: out _)),
                4 => (reader.TryReadByte(value: out _) && reader.TryReadVarUInt32(value: out _)),
                _ => false,
            });

            if (!described) {
                return false;
            }

            imports.Add(item: new WasmImport(
                Kind: kind,
                Module: module,
                Name: name
            ));
        }

        return true;
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
        var reader = new Reader(bytes: wasmBytes[8..]);
        var wellFormed = true;

        while (wellFormed && !reader.AtEnd) {
            wellFormed = (
                reader.TryReadByte(value: out var sectionId) &&
                reader.TryReadVarUInt32(value: out var sectionSize) &&
                reader.TryReadBytes(
                    length: sectionSize,
                    value: out var payload
                ) &&
                (sectionId switch {
                    2 => TryParseImportSection(
                        imports: imports,
                        payload: payload
                    ),
                    7 => TryParseExportSection(
                        exports: exports,
                        payload: payload
                    ),
                    _ => true,
                })
            );
        }

        if (!wellFormed) {
            return new WasmInspectionResult(
                ContentHash: contentHash,
                Exports: [],
                Imports: [],
                IsValid: false
            );
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
