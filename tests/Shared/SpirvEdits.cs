using System.Buffers.Binary;
using System.Text;

namespace Puck.Testing;

// Edits a compiled SPIR-V module the way another compile would have differed, so a law can hand a host kernels it did not
// build: another generator word (other bytes, the same bindings), another debug name, or two variables' bindings
// exchanged. Each edit follows the Khronos instruction encoding: a word holding the word count above the opcode, then
// the operands, a literal string being its UTF-8 bytes, a terminating zero, and zero padding to a whole word.
internal static class SpirvEdits {
    private const uint DecorationBinding = 33;
    private const int HeaderWords = 5;
    private const uint OpDecorate = 71;
    private const uint OpName = 5;

    // The module with its header's generator word replaced.
    public static byte[] WithGenerator(ReadOnlySpan<byte> module, uint generator) {
        var bytes = module.ToArray();

        BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: 8), value: generator);

        return bytes;
    }
    // The module with the debug name of the one id named from renamed to.
    public static byte[] Renamed(ReadOnlySpan<byte> module, string from, string to) {
        var words = Words(module: module);
        var output = new List<uint>(capacity: words.Length);
        var found = 0;

        output.AddRange(collection: words[..HeaderWords]);

        foreach (var (start, count, opcode) in Instructions(words: words)) {
            if ((opcode == OpName) && (Literal(words: words.AsSpan(length: (count - 2), start: (start + 2))) == from)) {
                var name = Encode(text: to);

                output.Add(item: (((uint)(name.Length + 2)) << 16) | OpName);
                output.Add(item: words[(start + 1)]);
                output.AddRange(collection: name);
                found++;

                continue;
            }

            output.AddRange(collection: words.AsSpan(length: count, start: start).ToArray());
        }

        if (found != 1) {
            throw new InvalidOperationException(message: $"The module names {found} ids '{from}'.");
        }

        return Bytes(words: [.. output]);
    }
    // The module with the Binding decorations of the variables named first and second exchanged.
    public static byte[] BindingsSwapped(ReadOnlySpan<byte> module, string first, string second) {
        var words = Words(module: module);
        var firstId = IdOf(name: first, words: words);
        var secondId = IdOf(name: second, words: words);
        int? firstAt = null;
        int? secondAt = null;

        foreach (var (start, count, opcode) in Instructions(words: words)) {
            if ((opcode == OpDecorate) && (count == 4) && (words[(start + 2)] == DecorationBinding)) {
                if (words[(start + 1)] == firstId) {
                    firstAt = (start + 3);
                } else if (words[(start + 1)] == secondId) {
                    secondAt = (start + 3);
                }
            }
        }

        var (a, b) = ((firstAt ?? throw new InvalidOperationException(message: $"'{first}' carries no binding.")), (secondAt ?? throw new InvalidOperationException(message: $"'{second}' carries no binding.")));

        (words[a], words[b]) = (words[b], words[a]);

        return Bytes(words: words);
    }

    private static byte[] Bytes(uint[] words) {
        var bytes = new byte[(words.Length * 4)];

        for (var index = 0; (index < words.Length); index++) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: (index * 4)), value: words[index]);
        }

        return bytes;
    }
    private static uint[] Encode(string text) {
        var utf8 = Encoding.UTF8.GetBytes(s: text);
        var padded = new byte[(((utf8.Length / 4) + 1) * 4)];

        utf8.CopyTo(array: padded, index: 0);

        return Words(module: padded);
    }
    private static uint IdOf(string name, uint[] words) {
        foreach (var (start, count, opcode) in Instructions(words: words)) {
            if ((opcode == OpName) && (Literal(words: words.AsSpan(length: (count - 2), start: (start + 2))) == name)) {
                return words[(start + 1)];
            }
        }

        throw new InvalidOperationException(message: $"The module names no id '{name}'.");
    }
    private static IEnumerable<(int Start, int Count, uint Opcode)> Instructions(uint[] words) {
        for (var start = HeaderWords; (start < words.Length);) {
            var count = ((int)(words[start] >> 16));

            if (count == 0) {
                throw new InvalidDataException(message: $"The instruction at word {start} is empty.");
            }

            yield return (start, count, words[start] & 0xFFFFu);
            start += count;
        }
    }
    private static string Literal(ReadOnlySpan<uint> words) {
        var bytes = new byte[(words.Length * 4)];

        for (var index = 0; (index < words.Length); index++) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: (index * 4)), value: words[index]);
        }

        var end = Array.IndexOf(array: bytes, value: ((byte)0));

        return Encoding.UTF8.GetString(bytes: bytes, index: 0, count: ((end < 0) ? bytes.Length : end));
    }
    private static uint[] Words(ReadOnlySpan<byte> module) {
        var words = new uint[(module.Length / 4)];

        for (var index = 0; (index < words.Length); index++) {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(source: module[(index * 4)..]);
        }

        return words;
    }
}
