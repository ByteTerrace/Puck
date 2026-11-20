using Puck.Confinement;
using System.Security.Cryptography;
using System.Text;
using Puck.Assets;
using Puck.Hosting.Jobs;

namespace Puck.World.Transpiler;

public sealed partial class WorldCompileSession {
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

            if ((reader.ReadString() != CompilerIdentity) || (reader.ReadString() != key)) { return null; }
            var value = read(reader);

            if (stream.Position != stream.Length) { return null; }
            // A served entry is used now, so the directory's retention keeps it over entries no read wants.
            Puck.Abstractions.CacheRetention.Stamp(path: path);
            return value;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException or OverflowException)) {
            return null;
        }
    }
    // Hash while writing the atomic file; no entry-sized staging buffer or copy is needed.
    private void WritePersisted(string key, Action<BinaryWriter> write) {
        if (EntryPath(key: key) is not { } path) { return; }
        try {
            AtomicFile.Write(path: path, write: stream => {
                using var hashing = new Puck.Abstractions.Sha256Stream(inner: stream);

                using (var writer = new BinaryWriter(hashing, Encoding.UTF8, leaveOpen: true)) {
                    writer.Write(buffer: Magic);
                    writer.Write(value: CompilerIdentity);
                    writer.Write(value: key);
                    write(writer);
                }
                stream.Write(buffer: hashing.TakeHash());
            });
            // The bound holds the most recently used entries, the one just written among them, and listing the
            // directory also removes the temporaries a killed write abandoned.
            _ = Puck.Abstractions.CacheRetention.EnforceOnWrite(
                bound: new Puck.Abstractions.CacheBound(MaxBytes: MaxPersistedBytes, MaxEntries: MaxPersistedEntries),
                directory: Path.GetDirectoryName(path: path)!,
                inUse: path,
                isEntry: static name => name.EndsWith(comparisonType: StringComparison.Ordinal, value: EntryExtension)
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // A refused write only costs a future derivation.
        }
    }

    // A deferring session's composed images (DeferCompositionWrites) are written here, off the thread that composed
    // them, one at a time in the order they came: a reload's recomposition holds its image in memory at once
    // (WorldDefinitionFileSource) and never waits for the disk. A read of a composition waits for the writes queued
    // before it, and the process waits for them as it ends.
    private static readonly TimeSpan CompositionWritesExitDrain = TimeSpan.FromSeconds(seconds: 10);
    private static readonly BoundedWorkQueue<Action> CompositionWrites = BoundedWorkQueue<Action>.DrainedAtExit(concurrency: 1, exitDrain: CompositionWritesExitDrain, run: static write => write());

    /// <inheritdoc />
    public WorldComposedDocument? ReadComposition(string key) {
        if (CompositionWrites.Unfinished > 0) { _ = CompositionWrites.WaitIdle(timeout: CompositionWritesExitDrain); }
        return ReadPersisted(key: ("composition\0" + key), read: ReadImage);
    }
    private static WorldComposedDocument ReadImage(BinaryReader reader) {
        var inputs = new CompileInput[ReadCount(reader: reader)];

        for (var index = 0; (index < inputs.Length); index++) {
            inputs[index] = new CompileInput(reader.ReadString(), ((CompileInputKind)reader.ReadByte()), reader.ReadString());
        }
        var reach = reader.ReadInt32();
        var json = ReadBytes(reader: reader);
        var links = new WorldComposedLink[ReadCount(reader: reader)];

        for (var index = 0; (index < links.Length); index++) {
            var path = reader.ReadString();
            var digest = reader.ReadBytes(count: 32);

            links[index] = new WorldComposedLink(bytes: (reader.ReadBoolean() ? ReadBytes(reader: reader) : null), digest: digest, path: path);
        }
        if ((links.Length > 0) && (links[0].Bytes is null)) { throw new InvalidDataException(message: "A composition's own document has no bytes."); }
        if ((links.Length == 0) || (reach < 0) || (reach >= WorldDocumentBasis.MaxChainDepth)) { throw new InvalidDataException(message: "Invalid composition reach or chain."); }
        return new WorldComposedDocument(Chain: links, ComposedJson: json, Inputs: inputs, Reach: reach);
    }
    /// <inheritdoc />
    /// <remarks>A session that defers its composition writes (<see cref="DeferCompositionWrites"/>) queues the image and
    /// writes it off the calling thread; <see cref="ReadComposition"/> waits for it.</remarks>
    public void WriteComposition(string key, WorldComposedDocument image) {
        ArgumentNullException.ThrowIfNull(argument: image);

        var inputs = (image.Inputs ?? throw new ArgumentException(message: "A persistent composition requires its input facts.", paramName: nameof(image)));
        void Write() => WritePersisted(key: ("composition\0" + key), write: writer => WriteImage(image: image, inputs: inputs, writer: writer));

        if (Volatile.Read(location: ref m_state.DeferCompositionWrites)) {
            CompositionWrites.Enqueue(item: Write);
        } else {
            Write();
        }
    }
    private static void WriteImage(WorldComposedDocument image, IReadOnlyList<CompileInput> inputs, BinaryWriter writer) {
        writer.Write(value: inputs.Count);
        foreach (var input in inputs) {
            writer.Write(value: input.Path);
            writer.Write(value: ((byte)input.Kind));
            writer.Write(value: input.ContentHash);
        }
        writer.Write(value: image.Reach);
        writer.Write(value: image.ComposedJson.Length);
        writer.Write(buffer: image.ComposedJson);
        // Every link is kept by its digest, which the content pin folds; only the image's own document keeps its
        // bytes, which a reader compares with its own (the input facts prove every other read).
        writer.Write(value: image.Chain.Count);
        for (var index = 0; (index < image.Chain.Count); index++) {
            var link = image.Chain[index];

            writer.Write(value: link.Path);
            writer.Write(buffer: link.Digest);
            writer.Write(value: (index == 0));
            if (index == 0) {
                writer.Write(value: link.Bytes!.Length);
                writer.Write(buffer: link.Bytes);
            }
        }
    }

    private static int ReadCount(BinaryReader reader) {
        var count = reader.ReadInt32();

        if ((count < 0) || (count > (reader.BaseStream.Length - reader.BaseStream.Position))) { throw new InvalidDataException(message: "Invalid cache length."); }
        return count;
    }
    private static byte[] ReadBytes(BinaryReader reader) => reader.ReadBytes(count: ReadCount(reader: reader));
}
