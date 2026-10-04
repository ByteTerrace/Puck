using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Puck.Assets;

/// <summary>Represents an imported function or resource declared by a WebAssembly module.</summary>
/// <param name="Module">The imported module name (e.g. "puck_host", "env").</param>
/// <param name="Name">The imported function or entity name.</param>
/// <param name="Kind">The descriptor kind (0 = Function, 1 = Table, 2 = Memory, 3 = Global, 4 = Tag).</param>
public readonly record struct WasmImport(string Module, string Name, byte Kind);
/// <summary>One linear memory a WebAssembly module declares, defined in its memory section or imported.</summary>
/// <param name="MinimumPages">The declared minimum size, in pages.</param>
/// <param name="PageSizeLog2">The base-2 logarithm of the page size: 16 (64 KiB) unless the memory declares a custom
/// page size.</param>
/// <param name="Imported">Whether the memory is imported rather than defined by the module.</param>
public readonly record struct WasmMemoryDeclaration(ulong MinimumPages, int PageSizeLog2, bool Imported) {
    /// <summary>Gets the declared minimum size in bytes, saturating at <see cref="ulong.MaxValue"/>.</summary>
    public ulong MinimumBytes => ((MinimumPages > (ulong.MaxValue >> PageSizeLog2))
        ? ulong.MaxValue
        : (MinimumPages << PageSizeLog2));
}
/// <summary>What a WebAssembly binary module declares at its boundary, read from its sections without compiling or
/// instantiating it: its exports, its imports, and every linear memory it defines or imports.</summary>
/// <param name="Exports">Exported names, in section order.</param>
/// <param name="Imports">Imported entities, in section order.</param>
/// <param name="Memories">Every memory, imported ones first, as the module's memory index space orders them.</param>
public sealed record WasmModuleDeclarations(IReadOnlyList<string> Exports, IReadOnlyList<WasmImport> Imports, IReadOnlyList<WasmMemoryDeclaration> Memories) {
    private const int DefaultPageSizeLog2 = 16;

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
        // A signed LEB128 of at most 33 bits (a heap type): five bytes, the last carrying the sign.
        public bool TrySkipVarInt33() {
            for (var count = 0; (count < 5); count++) {
                if (!TryReadByte(value: out var next)) {
                    return false;
                }
                if ((next & 0x80) == 0) {
                    return true;
                }
            }

            return false;
        }
        // An unsigned LEB128 of at most 32 bits: five bytes, the fifth carrying four.
        public bool TryReadVarUInt32(out uint value) {
            var read = TryReadVarUInt(
                bits: 32,
                value: out var wide
            );

            value = ((uint)wide);

            return read;
        }
        // An unsigned LEB128 of at most the given bits.
        public bool TryReadVarUInt(int bits, out ulong value) {
            value = 0UL;

            for (var shift = 0; (shift < bits); shift += 7) {
                if (!TryReadByte(value: out var next)) {
                    return false;
                }

                var payload = ((ulong)(next & 0x7F));

                if (
                    ((bits - shift) < 7) &&
                    ((payload >> (bits - shift)) != 0UL)
                ) {
                    return false;
                }

                value |= (payload << shift);

                if ((next & 0x80) == 0) {
                    return true;
                }
            }

            return false;
        }
        // A value type or reference type: one byte, or a nullable reference prefix followed by its heap type.
        public bool TrySkipValueType() => (
            TryReadByte(value: out var type) &&
            (((type != 0x63) && (type != 0x64)) || TrySkipVarInt33())
        );
    }

    private static string Describe(string where, string what) => $"{where}: {what}";
    // A memory's limits, failing with a phrase that names the part at fault: flags (bit 0 a maximum, bit 1 shared, bit 2 64-bit indices, bit 3 a custom page size), the
    // minimum and the optional maximum in pages, and the page size's base-2 logarithm when it is custom.
    private static bool TryReadMemoryLimits(ref Reader reader, bool imported, out WasmMemoryDeclaration memory, out string error) {
        memory = default;

        if (!reader.TryReadByte(value: out var flags)) {
            error = "limits end before their flags";

            return false;
        }
        if ((flags & ~0x0F) != 0) {
            error = $"limits flags 0x{flags:X2} are not declared";

            return false;
        }

        var bits = (((flags & 0x04) != 0) ? 64 : 32);

        if (!reader.TryReadVarUInt(bits: bits, value: out var minimum)) {
            error = $"minimum is not a {bits}-bit LEB128";

            return false;
        }
        if (
            ((flags & 0x01) != 0) &&
            !reader.TryReadVarUInt(bits: bits, value: out _)
        ) {
            error = $"maximum is not a {bits}-bit LEB128";

            return false;
        }

        var pageSizeLog2 = DefaultPageSizeLog2;

        if ((flags & 0x08) != 0) {
            if (
                !reader.TryReadVarUInt32(value: out var log2) ||
                (log2 > DefaultPageSizeLog2)
            ) {
                error = "custom page size is not a power of two from 1 byte to 64 KiB";

                return false;
            }

            pageSizeLog2 = ((int)log2);
        }

        memory = new WasmMemoryDeclaration(
            Imported: imported,
            MinimumPages: minimum,
            PageSizeLog2: pageSizeLog2
        );
        error = string.Empty;

        return true;
    }
    // A table's limits: flags (bit 0 a maximum, bit 2 64-bit indices), the minimum and the optional maximum.
    private static bool TrySkipTableLimits(ref Reader reader) {
        if (
            !reader.TryReadByte(value: out var flags) ||
            ((flags & ~0x05) != 0)
        ) {
            return false;
        }

        var bits = (((flags & 0x04) != 0) ? 64 : 32);

        return (
            reader.TryReadVarUInt(bits: bits, value: out _) &&
            (((flags & 0x01) == 0) || reader.TryReadVarUInt(bits: bits, value: out _))
        );
    }
    private static bool TryReadExportSection(ReadOnlySpan<byte> payload, List<string> exports, out string error) {
        var reader = new Reader(bytes: payload);

        if (!reader.TryReadVarUInt32(value: out var count)) {
            error = Describe(what: "its count is not a LEB128", where: "export section");

            return false;
        }

        for (var index = 0U; (index < count); index++) {
            if (
                !reader.TryReadName(value: out var name) ||
                !reader.TryReadByte(value: out _) ||
                !reader.TryReadVarUInt32(value: out _)
            ) {
                error = Describe(what: "it runs past its section", where: $"export section entry {index}");

                return false;
            }

            exports.Add(item: name);
        }

        error = string.Empty;

        return true;
    }
    private static bool TryReadImportSection(ReadOnlySpan<byte> payload, List<WasmImport> imports, List<WasmMemoryDeclaration> memories, out string error) {
        var reader = new Reader(bytes: payload);

        if (!reader.TryReadVarUInt32(value: out var count)) {
            error = Describe(what: "its count is not a LEB128", where: "import section");

            return false;
        }

        for (var index = 0U; (index < count); index++) {
            var where = $"import section entry {index}";

            if (
                !reader.TryReadName(value: out var module) ||
                !reader.TryReadName(value: out var name) ||
                !reader.TryReadByte(value: out var kind)
            ) {
                error = Describe(what: "its names run past its section", where: where);

                return false;
            }

            // The descriptor after the kind: a function's type index, a table's element type and limits, a
            // memory's limits, a global's value type and mutability, or a tag's attribute and type index.
            if (kind == 2) {
                if (!TryReadMemoryLimits(
                    error: out var limitsError,
                    imported: true,
                    memory: out var memory,
                    reader: ref reader
                )) {
                    error = Describe(what: $"the imported memory's {limitsError}", where: where);

                    return false;
                }

                memories.Add(item: memory);
            } else if (!(kind switch {
                0 => reader.TryReadVarUInt32(value: out _),
                1 => (reader.TrySkipValueType() && TrySkipTableLimits(reader: ref reader)),
                3 => (reader.TrySkipValueType() && reader.TryReadByte(value: out _)),
                4 => (reader.TryReadByte(value: out _) && reader.TryReadVarUInt32(value: out _)),
                _ => false,
            })) {
                error = Describe(what: $"its kind {kind} descriptor is not declared or runs past its section", where: where);

                return false;
            }

            imports.Add(item: new WasmImport(
                Kind: kind,
                Module: module,
                Name: name
            ));
        }

        error = string.Empty;

        return true;
    }
    private static bool TryReadMemorySection(ReadOnlySpan<byte> payload, List<WasmMemoryDeclaration> memories, out string error) {
        var reader = new Reader(bytes: payload);

        if (!reader.TryReadVarUInt32(value: out var count)) {
            error = Describe(what: "its count is not a LEB128", where: "memory section");

            return false;
        }

        for (var index = 0U; (index < count); index++) {
            if (!TryReadMemoryLimits(
                error: out var limitsError,
                imported: false,
                memory: out var memory,
                reader: ref reader
            )) {
                error = Describe(what: $"its {limitsError}", where: $"memory section entry {index}");

                return false;
            }

            memories.Add(item: memory);
        }

        if (!reader.AtEnd) {
            error = Describe(what: "bytes follow its last entry", where: "memory section");

            return false;
        }

        error = string.Empty;

        return true;
    }

    /// <summary>Reads what a WebAssembly binary module declares at its boundary, without compiling it.</summary>
    /// <param name="module">The module's binary bytes.</param>
    /// <param name="declarations">The declarations, when the module is well formed as far as they reach.</param>
    /// <param name="error">Where and how the module is malformed, naming the section and entry, when it is.</param>
    /// <returns>Whether the header and every section length lie inside the bytes, and the import, memory and export
    /// sections parse.</returns>
    public static bool TryRead(ReadOnlySpan<byte> module, [NotNullWhen(returnValue: true)] out WasmModuleDeclarations? declarations, out string error) {
        declarations = null;

        if (
            (module.Length < 8) ||
            !module[..4].SequenceEqual(other: WasmBinaryFormat.WasmMagic)
        ) {
            error = "the module does not begin with the WebAssembly magic bytes";

            return false;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(source: module.Slice(
            length: 4,
            start: 4
        ));

        if (version != WasmBinaryFormat.Version1) {
            error = $"the module's binary format version is {version}, not {WasmBinaryFormat.Version1}";

            return false;
        }

        var exports = new List<string>();
        var imports = new List<WasmImport>();
        var imported = new List<WasmMemoryDeclaration>();
        var defined = new List<WasmMemoryDeclaration>();
        var reader = new Reader(bytes: module[8..]);

        while (!reader.AtEnd) {
            if (
                !reader.TryReadByte(value: out var sectionId) ||
                !reader.TryReadVarUInt32(value: out var sectionSize) ||
                !reader.TryReadBytes(
                    length: sectionSize,
                    value: out var payload
                )
            ) {
                error = "a section's length runs past the module";

                return false;
            }

            var read = (sectionId switch {
                2 => TryReadImportSection(
                    error: out error,
                    imports: imports,
                    memories: imported,
                    payload: payload
                ),
                5 => TryReadMemorySection(
                    error: out error,
                    memories: defined,
                    payload: payload
                ),
                7 => TryReadExportSection(
                    error: out error,
                    exports: exports,
                    payload: payload
                ),
                _ => Skipped(error: out error),
            });

            if (!read) {
                return false;
            }
        }

        declarations = new WasmModuleDeclarations(
            Exports: exports,
            Imports: imports,
            Memories: [.. imported, .. defined]
        );
        error = string.Empty;

        return true;

        static bool Skipped(out string error) {
            error = string.Empty;

            return true;
        }
    }
}
