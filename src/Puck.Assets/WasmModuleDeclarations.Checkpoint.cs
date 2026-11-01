using System.Buffers;

namespace Puck.Assets;

public sealed partial record WasmModuleDeclarations {
    /// <summary>Adds host-only exports for every defined global without changing executable instructions or indices.
    /// Addons import nothing. A data-count section means segment-drop state may exist outside memory; tables and
    /// elements also carry references that this checkpoint implementation cannot serialize.</summary>
    /// <param name="module">The already declaration-checked binary.</param>
    /// <param name="globals">The generated export names, in global-index order.</param>
    /// <param name="refusal">A resource whose state cannot be checkpointed, or null.</param>
    /// <returns>The binary with the additional exports.</returns>
    public byte[] WithCheckpointExports(ReadOnlySpan<byte> module, out string[] globals, out string? refusal) {
        if (Imports.Count != 0) { throw new InvalidDataException(message: "Checkpoint exports require an import-free module."); }
        refusal = ((Tables.Count != 0) ? "table references cannot be checkpointed" : null);
        var sections = new List<(byte Id, byte[] Payload)>();
        var reader = new Reader(bytes: module[8..]);
        var globalCount = 0U;

        while (!reader.AtEnd) {
            if (!reader.TryReadByte(value: out var id) || !reader.TryReadVarUInt32(value: out var size) || !reader.TryReadBytes(length: size, value: out var payload)) {
                throw new InvalidDataException(message: "Malformed checkpoint export source.");
            }
            var countReader = new Reader(bytes: payload);

            if ((id == 6) && (!countReader.TryReadVarUInt32(value: out globalCount) || (globalCount > size))) {
                throw new InvalidDataException(message: "Malformed global count.");
            }
            if (id is 9 or 12) { refusal = "element/data segment drop state cannot be checkpointed"; }
            if ((id == 7) && sections.Any(predicate: section => (section.Id == 7))) { throw new InvalidDataException(message: "Duplicate export section."); }
            sections.Add(item: (id, payload.ToArray()));
        }
        var prefix = "__puck_checkpoint_global_";

        while (Exports.Any(predicate: name => name.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix))) { prefix = ("_" + prefix); }
        globals = Enumerable.Range(count: checked((int)globalCount), start: 0).Select(selector: index => (prefix + index)).ToArray();
        var exports = new ArrayBufferWriter<byte>();
        var original = sections.Find(match: section => (section.Id == 7)).Payload;
        var exportCount = 0U;
        ReadOnlySpan<byte> entries = [];

        if (original is not null) {
            var exportReader = new Reader(bytes: original);

            if (!exportReader.TryReadVarUInt32(value: out exportCount)) { throw new InvalidDataException(message: "Malformed export count."); }
            // Preserve each existing export verbatim after its count.
            // The original count may use a non-minimal LEB encoding.
            var offset = 0;

            while ((original[offset++] & 0x80) != 0) { }
            entries = original.AsSpan(start: offset);
        }
        exports.WriteVarUInt(value: checked((exportCount + globalCount)));
        exports.WriteBytes(value: entries);
        for (var index = 0; (index < globals.Length); index++) {
            exports.WriteText(value: globals[index]); exports.WriteByte(value: 3); exports.WriteVarUInt(value: ((uint)index));
        }
        var output = new ArrayBufferWriter<byte>();

        output.WriteBytes(value: module[..8]);
        var written = false;

        foreach (var (id, payload) in sections) {
            if (!written && (id is 7 or 8 or 9 or 10 or 11 or 12)) {
                WriteSection(output, 7, exports.WrittenSpan); written = true;
            }
            if (id != 7) { WriteSection(id: id, output: output, payload: payload); }
        }
        if (!written) { WriteSection(output, 7, exports.WrittenSpan); }
        return output.WrittenSpan.ToArray();
    }

    private static void WriteSection(ArrayBufferWriter<byte> output, byte id, ReadOnlySpan<byte> payload) {
        output.WriteByte(value: id); output.WriteVarUInt(value: checked((uint)payload.Length)); output.WriteBytes(value: payload);
    }
}
