using System.Security.Cryptography;
using System.Text;
using Puck.Assets;

namespace Puck.World.Transpiler.Composition;

public sealed partial class WorldCompileCache {
    private T? ReadPersisted<T>(string key, Func<BinaryReader, T> read) where T : class {
        if (EntryPath(key: key) is not { } path) { return null; }
        try {
            if (!File.Exists(path: path)) { return null; }
            var bytes = File.ReadAllBytes(path: path);

            if (bytes.Length < (Magic.Length + 32)) { return null; }
            var body = bytes.AsSpan(0, (bytes.Length - 32));
            Span<byte> digest = stackalloc byte[32];

            SHA256.HashData(destination: digest, source: body);
            if (!digest.SequenceEqual(other: bytes.AsSpan(start: body.Length)) || !body.StartsWith(value: Magic)) { return null; }
            using var stream = new MemoryStream(bytes, Magic.Length, (body.Length - Magic.Length), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if ((reader.ReadString() != FormatShapes.WorldCompileCacheMagic) || (reader.ReadString() != CompilerIdentity) || (reader.ReadString() != key)) { return null; }
            var value = read(reader);

            return ((stream.Position == stream.Length) ? value : null);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException or OverflowException)) {
            return null;
        }
    }
    // Hash while writing the atomic file; no entry-sized staging buffer or copy is needed.
    private void WritePersisted(string key, Action<BinaryWriter> write) {
        if (EntryPath(key: key) is not { } path) { return; }
        try {
            AtomicFile.Write(path: path, write: stream => {
                using var hash = SHA256.Create();

                using (var hashing = new CryptoStream(stream, hash, CryptoStreamMode.Write, leaveOpen: true)) {
                    using var writer = new BinaryWriter(hashing, Encoding.UTF8, leaveOpen: true);

                    writer.Write(buffer: Magic);
                    writer.Write(value: FormatShapes.WorldCompileCacheMagic);
                    writer.Write(value: CompilerIdentity);
                    writer.Write(value: key);
                    write(writer);
                    writer.Flush();
                    hashing.FlushFinalBlock();
                }
                stream.Write(buffer: hash.Hash!);
            });
            Trim(directory: Path.GetDirectoryName(path: path)!);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // A refused write only costs a future derivation.
        }
    }

    /// <inheritdoc />
    public WorldComposedDocument? ReadComposition(string key) => ReadPersisted(key: ("composition\0" + key), read: static reader => {
        var inputs = new CompileInput[ReadCount(reader: reader)];

        for (var index = 0; (index < inputs.Length); index++) {
            inputs[index] = new CompileInput(reader.ReadString(), ((CompileInputKind)reader.ReadByte()), reader.ReadString());
        }
        var reach = reader.ReadInt32();
        var json = ReadBytes(reader: reader);
        var links = new WorldComposedLink[ReadCount(reader: reader)];

        for (var index = 0; (index < links.Length); index++) {
            links[index] = new WorldComposedLink(reader.ReadString(), ReadBytes(reader: reader));
        }
        if ((links.Length == 0) || (reach < 0) || (reach >= WorldDocumentBasis.MaxChainDepth)) { throw new InvalidDataException(message: "Invalid composition reach or chain."); }
        return new WorldComposedDocument(Chain: links, ComposedJson: json, Inputs: inputs, Reach: reach);
    });
    /// <inheritdoc />
    public void WriteComposition(string key, WorldComposedDocument image) => WritePersisted(key: ("composition\0" + key), write: writer => {
        var inputs = (image.Inputs ?? throw new ArgumentException(message: "A persistent composition requires its input facts.", paramName: nameof(image)));

        writer.Write(value: inputs.Count);
        foreach (var input in inputs) {
            writer.Write(value: input.Path);
            writer.Write(value: ((byte)input.Kind));
            writer.Write(value: input.ContentHash);
        }
        writer.Write(value: image.Reach);
        writer.Write(value: image.ComposedJson.Length);
        writer.Write(buffer: image.ComposedJson);
        writer.Write(value: image.Chain.Count);
        foreach (var link in image.Chain) {
            writer.Write(value: link.Path);
            writer.Write(value: link.Bytes.Length);
            writer.Write(buffer: link.Bytes);
        }
    });

    private static int ReadCount(BinaryReader reader) {
        var count = reader.ReadInt32();

        if ((count < 0) || (count > (reader.BaseStream.Length - reader.BaseStream.Position))) { throw new InvalidDataException(message: "Invalid cache length."); }
        return count;
    }
    private static byte[] ReadBytes(BinaryReader reader) => reader.ReadBytes(count: ReadCount(reader: reader));
}
