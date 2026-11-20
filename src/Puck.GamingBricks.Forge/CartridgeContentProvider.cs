using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Assets;
using Puck.Assets.Documents;

namespace Puck.GamingBricks.Forge;

/// <summary>Adapts cartridge validation and native compilation to the neutral machine-content contract.</summary>
/// <remarks>A prepared cartridge is a derived product (<see cref="DerivedBytes"/>): its image, source identity and symbols
/// are a pure function of the canonical cartridge JSON and the code that compiles it, so each native compiler keeps one
/// cache keyed by the JSON's content pin and that code (the compiler's <c>Compile</c> and everything it reaches, and this
/// type's parse, symbol table and product encoding). A miss compiles and is counted; a damaged entry derives again. A
/// boot pointed at a prewarmed cache root (a player build's <c>--cache-dir</c>) therefore binds every cartridge without
/// compiling it.</remarks>
public static class CartridgeContentProvider {
    // Symbol names and source identities are short; the ceiling only bounds a damaged entry's allocation.
    private const int MaxTextBytes = (1 << 16);
    private static readonly ConcurrentDictionary<Type, DerivedBytes> Caches = new();

    /// <summary>Validates JSON and compiles its executable image and exported symbols, or reads them from the derived
    /// cache when the same compiler code prepared the same document before.</summary>
    /// <param name="compiler">The native target compiler.</param>
    /// <param name="content">The canonical cartridge JSON.</param>
    /// <returns>The executable image and source metadata.</returns>
    public static PreparedMachineContent Prepare(ICartridgeCompiler compiler, ReadOnlyMemory<byte> content) {
        ArgumentNullException.ThrowIfNull(argument: compiler);

        var cache = Caches.GetOrAdd(
            key: compiler.GetType(),
            valueFactory: static (type, engine) => new DerivedBytes(
                name: $"cartridge-{engine}",
                roots: [
                    (type.Assembly.GetName().Name!, type.FullName!, nameof(ICartridgeCompiler.Compile)),
                    (typeof(CartridgeContentProvider).Assembly.GetName().Name!, typeof(CartridgeContentProvider).FullName!, null),
                ]
            ),
            factoryArgument: compiler.EngineId
        );

        try {
            return Decode(product: cache.Get(
                derive: () => Encode(prepared: Compile(compiler: compiler, content: content)),
                inputs: ContentPin.Compute(content: content.Span).ToString()
            ));
        } catch (Exception exception) when ((exception is JsonException or DocumentValidationException or ArgumentException or InvalidOperationException or CartridgeCapacityException)) {
            throw new MachineContentException(
                message: exception.Message.ReplaceLineEndings(replacementText: " "),
                innerException: exception
            );
        }
    }

    private static PreparedMachineContent Compile(ICartridgeCompiler compiler, ReadOnlyMemory<byte> content) {
        var compilation = compiler.Compile(document: CartridgeDocuments.Parse(utf8: content.Span));
        var symbols = new Dictionary<string, MachineContentSymbol>(comparer: StringComparer.Ordinal);

        foreach (var (name, address) in compilation.Variables) {
            symbols.Add(key: name, value: new MachineContentSymbol(Address: address, Space: "bus"));
        }
        foreach (var (name, address) in compilation.Arrays) {
            symbols.Add(key: name, value: new MachineContentSymbol(Address: address, Space: "bus"));
        }

        return new PreparedMachineContent(
            Image: compilation.Rom,
            SourceHash: compilation.SourceHash,
            Symbols: symbols,
            VerifiedSourceFormat: CartridgeDocument.SchemaId
        );
    }
    // The product: source identity, image, then the symbols in ordinal order. Every prepared cartridge carries the
    // cartridge schema as its verified format, so the decode restores it rather than storing it.
    private static byte[] Encode(PreparedMachineContent prepared) {
        var writer = new ArrayBufferWriter<byte>();

        writer.WriteText(value: prepared.SourceHash);
        writer.WriteVarUInt(value: checked((uint)prepared.Image.Length));
        writer.WriteBytes(value: prepared.Image);
        writer.WriteVarUInt(value: checked((uint)prepared.Symbols.Count));
        foreach (var (name, symbol) in prepared.Symbols.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            writer.WriteText(value: name);
            writer.WriteText(value: symbol.Space);
            writer.WriteUInt64(value: symbol.Address);
        }

        return writer.WrittenSpan.ToArray();
    }
    private static PreparedMachineContent Decode(byte[] product) {
        var reader = new CanonicalBinaryReader(content: product);
        var sourceHash = reader.ReadText(maximumByteCount: MaxTextBytes);
        var image = reader.ReadBytes(count: reader.ReadBoundedInt(maximum: product.Length)).ToArray();
        var count = reader.ReadBoundedInt(maximum: product.Length);
        var symbols = new Dictionary<string, MachineContentSymbol>(capacity: count, comparer: StringComparer.Ordinal);

        for (var index = 0; (index < count); index++) {
            var name = reader.ReadText(maximumByteCount: MaxTextBytes);
            var space = reader.ReadText(maximumByteCount: MaxTextBytes);

            symbols.Add(key: name, value: new MachineContentSymbol(Address: reader.ReadUInt64(), Space: space));
        }
        reader.ExpectEnd();

        return new PreparedMachineContent(
            Image: image,
            SourceHash: sourceHash,
            Symbols: symbols,
            VerifiedSourceFormat: CartridgeDocument.SchemaId
        );
    }
}
