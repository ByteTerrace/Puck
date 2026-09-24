using System.Buffers;

namespace Puck.Assets;

/// <summary>A four-character code naming a chunk and the derivation that wrote it (<c>DEFN</c>, <c>TILE</c>): four
/// printable ASCII characters, stored in the order they are written.</summary>
public readonly record struct ChunkCode {
    /// <summary>The number of characters, and bytes, a code carries.</summary>
    public const int Length = 4;

    private ChunkCode(uint value) {
        Value = value;
    }

    /// <summary>Gets the code's four bytes packed little-endian, the first character in the lowest byte.</summary>
    public uint Value { get; }

    private static bool IsPrintable(int character) =>
        (character is >= 0x20 and <= 0x7e);

    /// <summary>Parses a code from its four characters.</summary>
    /// <param name="text">The four printable ASCII characters.</param>
    /// <returns>The code.</returns>
    /// <exception cref="FormatException"><paramref name="text"/> is not four printable ASCII characters.</exception>
    public static ChunkCode Parse(string text) => (TryParse(
        code: out var code,
        text: text
    )
        ? code
        : throw new FormatException(message: $"'{text}' is not a chunk code: a code is {Length} printable ASCII characters."));
    /// <summary>Reads a code from the four bytes a container stores it as.</summary>
    /// <param name="bytes">The four bytes.</param>
    /// <param name="code">The code, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the bytes are four printable ASCII characters.</returns>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out ChunkCode code) {
        code = default;

        if (bytes.Length != Length) {
            return false;
        }

        var value = 0U;

        for (var index = 0; (index < Length); ++index) {
            if (!IsPrintable(character: bytes[index])) {
                return false;
            }

            value |= (((uint)bytes[index]) << (8 * index));
        }

        code = new ChunkCode(value: value);
        return true;
    }
    /// <summary>Parses a code from its four characters.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="code">The code, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is four printable ASCII characters.</returns>
    public static bool TryParse(string? text, out ChunkCode code) {
        code = default;

        if (text is not { Length: Length }) {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Length];

        for (var index = 0; (index < Length); ++index) {
            if (!IsPrintable(character: text[index])) {
                return false;
            }

            bytes[index] = ((byte)text[index]);
        }

        return TryRead(
            bytes: bytes,
            code: out code
        );
    }
    /// <summary>Writes the code's four bytes.</summary>
    /// <param name="destination">The destination; at least four bytes long.</param>
    public void WriteTo(Span<byte> destination) {
        for (var index = 0; (index < Length); ++index) {
            destination[index] = ((byte)(Value >> (8 * index)));
        }
    }
    /// <summary>Returns the code's four characters.</summary>
    /// <returns>The code as text.</returns>
    public override string ToString() => string.Create(
        length: Length,
        state: Value,
        action: static (span, value) => {
            for (var index = 0; (index < Length); ++index) {
                span[index] = ((char)((byte)(value >> (8 * index))));
            }
        }
    );
}
/// <summary>One input a chunk's derivation read beyond what its container's header keys: an asset file, a neighbour
/// document, a machine identity. A chunk holds while every input still reads as recorded.</summary>
/// <param name="Name">The input's name, as the derivation that reads it spells it.</param>
/// <param name="Hash">The content hash of what the derivation read, or <see langword="null"/> when the input was
/// absent.</param>
public sealed record ChunkInput(string Name, AssetContentHash? Hash);
/// <summary>One chunk of a <see cref="ChunkContainer"/>: its code, the version of the derivation that wrote it, the
/// inputs that derivation read, and the payload with its content hash.</summary>
public sealed class ContainerChunk {
    /// <summary>Initializes a new instance of the <see cref="ContainerChunk"/> class and hashes its payload.</summary>
    /// <param name="code">The chunk's code.</param>
    /// <param name="version">The version of the derivation that wrote the payload.</param>
    /// <param name="payload">The payload; the chunk holds it by reference and never copies it.</param>
    /// <param name="inputs">The inputs the derivation read, in ordinal order of their names, each name once; or
    /// <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentException">An input is <see langword="null"/>, or the input names are not in strictly
    /// ascending ordinal order.</exception>
    public ContainerChunk(ChunkCode code, uint version, ReadOnlyMemory<byte> payload, IReadOnlyList<ChunkInput>? inputs = null) {
        inputs ??= [];

        for (var index = 0; (index < inputs.Count); ++index) {
            if (inputs[index] is null) {
                throw new ArgumentException(
                    message: $"chunk '{code}' input {index} is null.",
                    paramName: nameof(inputs)
                );
            }

            if (
                (index > 0) &&
                (string.CompareOrdinal(
                strA: inputs[(index - 1)].Name,
                strB: inputs[index].Name
            ) >= 0)
            ) {
                throw new ArgumentException(
                    message: $"chunk '{code}' inputs must be named in strictly ascending ordinal order; '{inputs[index].Name}' follows '{inputs[(index - 1)].Name}'.",
                    paramName: nameof(inputs)
                );
            }
        }

        Code = code;
        Hash = AssetContentHash.Compute(content: payload.Span);
        Inputs = inputs;
        Payload = payload;
        Version = version;
    }

    /// <summary>Gets the chunk's code.</summary>
    public ChunkCode Code { get; }
    /// <summary>Gets the content hash of <see cref="Payload"/>.</summary>
    public AssetContentHash Hash { get; }
    /// <summary>Gets the inputs the derivation read, in ordinal order of their names.</summary>
    public IReadOnlyList<ChunkInput> Inputs { get; }
    /// <summary>Gets the payload.</summary>
    public ReadOnlyMemory<byte> Payload { get; }
    /// <summary>Gets the version of the derivation that wrote the payload.</summary>
    public uint Version { get; }
}
/// <summary>
/// The one chunk container Puck's binary products share: a four-byte magic naming the format, a format version, a
/// header the format owns, then chunks. Every integer is canonical (<see cref="CanonicalBinaryWriterExtensions"/>), so
/// equal containers encode to equal bytes.
/// <para>A chunk is its four-byte <see cref="ChunkCode"/>, the variable-width version of the derivation that wrote it,
/// its inputs (a variable-width count, then per input its UTF-8 name, a presence byte of 0 or 1, and the input's
/// 64-bit content hash when present, in strictly ascending ordinal order of name), the variable-width payload length,
/// the payload's 64-bit content hash (<see cref="AssetContentHash"/>, little-endian), zero bytes up to the next
/// multiple of <see cref="PayloadAlignment"/> counted from the container's first byte, and the payload. The order of
/// chunks is the writer's, and a code may repeat; a format that allows each code once refuses repeats itself.</para>
/// <para>A decode refuses, with an <see cref="InvalidDataException"/>, any byte it cannot account for: a wrong magic,
/// a noncanonical integer, nonzero padding, a payload whose hash disagrees, unordered inputs, a count or length the
/// remaining bytes cannot hold, or trailing bytes. A container is read whole or not at all, and no decode allocates
/// more than the bytes it was given can justify.</para>
/// </summary>
public sealed class ChunkContainer {
    /// <summary>The default largest container a decode accepts, in bytes.</summary>
    public const int DefaultMaximumBytes = ((1024 * 1024) * 1024);
    /// <summary>The length, in bytes, of a container's magic.</summary>
    public const int MagicLength = 4;
    /// <summary>The alignment, in bytes, of every payload's first byte relative to the container's first byte.</summary>
    public const int PayloadAlignment = 8;

    // The fewest bytes a chunk takes (code, version, input count, payload length, payload hash) and an input takes
    // (name length, presence), so a count is bounded by the bytes that remain before anything is allocated for it.
    private const int MinimumChunkBytes = ((ChunkCode.Length + 3) + sizeof(ulong));
    private const int MinimumInputBytes = 2;

    /// <summary>Initializes a new instance of the <see cref="ChunkContainer"/> class.</summary>
    /// <param name="formatVersion">The version of the format the magic names.</param>
    /// <param name="header">The header the format owns; the container holds it by reference.</param>
    /// <param name="chunks">The chunks, in the order they are written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="chunks"/> or one of its entries is
    /// <see langword="null"/>.</exception>
    public ChunkContainer(uint formatVersion, ReadOnlyMemory<byte> header, IReadOnlyList<ContainerChunk> chunks) {
        ArgumentNullException.ThrowIfNull(argument: chunks);

        foreach (var chunk in chunks) {
            ArgumentNullException.ThrowIfNull(argument: chunk, paramName: nameof(chunks));
        }

        Chunks = chunks;
        FormatVersion = formatVersion;
        Header = header;
    }

    /// <summary>Gets the chunks, in the order they were written.</summary>
    public IReadOnlyList<ContainerChunk> Chunks { get; }
    /// <summary>Gets the version of the format the magic names.</summary>
    public uint FormatVersion { get; }
    /// <summary>Gets the header the format owns.</summary>
    public ReadOnlyMemory<byte> Header { get; }

    private static void RequireMagic(ReadOnlySpan<byte> magic) {
        if (magic.Length != MagicLength) {
            throw new ArgumentException(
                message: $"a container magic is {MagicLength} bytes, not {magic.Length}.",
                paramName: nameof(magic)
            );
        }
    }

    /// <summary>Decodes a container from untrusted bytes.</summary>
    /// <param name="content">The container's bytes; decoded headers and payloads are slices of them, never copies.</param>
    /// <param name="magic">The four bytes the format's containers open with.</param>
    /// <param name="maximumBytes">The largest container accepted, in bytes.</param>
    /// <returns>The container.</returns>
    /// <exception cref="ArgumentException"><paramref name="magic"/> is not four bytes.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a canonical container of this format, or exceed
    /// <paramref name="maximumBytes"/>.</exception>
    public static ChunkContainer Decode(ReadOnlyMemory<byte> content, ReadOnlySpan<byte> magic, int maximumBytes = DefaultMaximumBytes) {
        RequireMagic(magic: magic);

        if (content.Length > maximumBytes) {
            throw new InvalidDataException(message: "the container exceeds its byte ceiling");
        }

        var reader = new CanonicalBinaryReader(content: content.Span);

        reader.Expect(value: magic);

        var formatVersion = reader.ReadVarUInt();
        var headerLength = reader.ReadBoundedInt(maximum: reader.Remaining);
        var header = content.Slice(
            length: headerLength,
            start: reader.Offset
        );

        _ = reader.ReadBytes(count: headerLength);

        var chunkCount = reader.ReadBoundedInt(maximum: (reader.Remaining / MinimumChunkBytes));
        var chunks = new ContainerChunk[chunkCount];

        for (var index = 0; (index < chunkCount); ++index) {
            if (!ChunkCode.TryRead(
                bytes: reader.ReadBytes(count: ChunkCode.Length),
                code: out var code
            )) {
                throw new InvalidDataException(message: $"chunk {index} does not open with a code of printable ASCII characters");
            }

            var version = reader.ReadVarUInt();
            var inputCount = reader.ReadBoundedInt(maximum: (reader.Remaining / MinimumInputBytes));
            var inputs = new ChunkInput[inputCount];

            for (var input = 0; (input < inputCount); ++input) {
                var name = reader.ReadText(maximumByteCount: reader.Remaining);

                inputs[input] = (reader.ReadByte() switch {
                    0 => new ChunkInput(
                        Hash: null,
                        Name: name
                    ),
                    1 => new ChunkInput(
                        Hash: new AssetContentHash(Value: reader.ReadUInt64()),
                        Name: name
                    ),
                    _ => throw new InvalidDataException(message: $"chunk '{code}' input '{name}' has an invalid presence byte"),
                });
            }

            var payloadLength = reader.ReadBoundedInt(maximum: reader.Remaining);
            var recordedHash = new AssetContentHash(Value: reader.ReadUInt64());

            reader.ExpectZeros(count: Padding(offset: reader.Offset));

            var payloadStart = reader.Offset;

            _ = reader.ReadBytes(count: payloadLength);

            var payload = content.Slice(
                length: payloadLength,
                start: payloadStart
            );

            ContainerChunk chunk;

            try {
                chunk = new ContainerChunk(
                    code: code,
                    inputs: inputs,
                    payload: payload,
                    version: version
                );
            } catch (ArgumentException exception) {
                throw new InvalidDataException(
                    innerException: exception,
                    message: exception.Message
                );
            }

            if (chunk.Hash != recordedHash) {
                throw new InvalidDataException(message: $"chunk '{code}' payload does not match its content hash");
            }

            chunks[index] = chunk;
        }

        reader.ExpectEnd();
        return new ChunkContainer(
            chunks: chunks,
            formatVersion: formatVersion,
            header: header
        );
    }
    /// <summary>Returns the number of zero bytes that align <paramref name="offset"/> to <see cref="PayloadAlignment"/>.</summary>
    /// <param name="offset">The offset, in bytes, from the container's first byte.</param>
    /// <returns>A padding length from zero to seven.</returns>
    public static int Padding(int offset) =>
        ((PayloadAlignment - (offset % PayloadAlignment)) % PayloadAlignment);
    /// <summary>Encodes the container in its canonical form.</summary>
    /// <param name="magic">The four bytes the format's containers open with.</param>
    /// <returns>The container's bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="magic"/> is not four bytes, or an input name has no UTF-8
    /// spelling.</exception>
    public byte[] Encode(ReadOnlySpan<byte> magic) {
        RequireMagic(magic: magic);

        var writer = new ArrayBufferWriter<byte>();
        Span<byte> code = stackalloc byte[ChunkCode.Length];

        writer.WriteBytes(value: magic);
        writer.WriteVarUInt(value: FormatVersion);
        writer.WriteVarUInt(value: checked((uint)Header.Length));
        writer.WriteBytes(value: Header.Span);
        writer.WriteVarUInt(value: checked((uint)Chunks.Count));

        foreach (var chunk in Chunks) {
            chunk.Code.WriteTo(destination: code);
            writer.WriteBytes(value: code);
            writer.WriteVarUInt(value: chunk.Version);
            writer.WriteVarUInt(value: checked((uint)chunk.Inputs.Count));

            foreach (var input in chunk.Inputs) {
                writer.WriteText(value: input.Name);

                if (input.Hash is { } hash) {
                    writer.WriteByte(value: 1);
                    writer.WriteUInt64(value: hash.Value);
                } else {
                    writer.WriteByte(value: 0);
                }
            }

            writer.WriteVarUInt(value: checked((uint)chunk.Payload.Length));
            writer.WriteUInt64(value: chunk.Hash.Value);
            writer.WriteZeros(count: Padding(offset: writer.WrittenCount));
            writer.WriteBytes(value: chunk.Payload.Span);
        }

        return writer.WrittenSpan.ToArray();
    }
    /// <summary>Finds the first chunk carrying <paramref name="code"/>.</summary>
    /// <param name="code">The code to find.</param>
    /// <param name="chunk">The chunk, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a chunk carries the code.</returns>
    public bool TryFind(ChunkCode code, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out ContainerChunk? chunk) {
        foreach (var candidate in Chunks) {
            if (candidate.Code == code) {
                chunk = candidate;
                return true;
            }
        }

        chunk = null;
        return false;
    }
}
