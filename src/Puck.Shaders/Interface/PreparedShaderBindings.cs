using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Assets;

namespace Puck.Shaders;

/// <summary>
/// The descriptor bindings a build reflected from compiled bytecode, shipped beside a player in its shipping cache
/// (<see cref="DerivedBytes.ShippingDirectories"/>) so a player reflects a prewarmed DXIL container without
/// <c>dxcompiler.dll</c>. An entry is named by the bytecode's own SHA-256 and this codec's version, so it can only ever
/// describe the exact bytes it was reflected from; it is a cache like the bytecode it rides with, never the truth: an
/// absent, damaged or unreadable entry is a miss, and the reader then reflects through the compiler library as before.
/// </summary>
public static class PreparedShaderBindings {
    // The store kind, which carries the codec's version: a change to the encoding or to what a reader reports names
    // another kind, so an older entry is a miss rather than a misreading.
    private const string Kind = "shader-bindings.v1";

    /// <summary>Writes the bindings reflected from some bytecode into a shipping cache.</summary>
    /// <param name="bytecode">The bytecode the bindings were reflected from.</param>
    /// <param name="bindings">The bindings, as <see cref="ShaderBytecodeReflector.Read"/> returned them.</param>
    /// <param name="directory">The shipping cache directory (<see cref="DerivedBytes.ShippingDirectoryName"/> beside the
    /// player).</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> or <paramref name="directory"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="IOException">The shipping cache cannot be written.</exception>
    public static void Export(ReadOnlySpan<byte> bytecode, IReadOnlyList<ShaderInterfaceBinding> bindings, string directory) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(directory);

        var store = new ContentAddressedStore(root: directory);

        store.SetDerived(kind: Kind, inputHash: PinOf(bytecode: bytecode), outputHash: store.Put(content: Encode(bindings: bindings)));
    }
    /// <summary>Reads the bindings a shipping cache holds for some bytecode.</summary>
    /// <param name="bytecode">The bytecode.</param>
    /// <param name="bindings">When this method returns <see langword="true"/>, the bindings, ordered as
    /// <see cref="ShaderInterfaceLayout.Ordered"/> orders them; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a shipping cache holds a readable entry for exactly these bytes.</returns>
    public static bool TryRead(ReadOnlySpan<byte> bytecode, out IReadOnlyList<ShaderInterfaceBinding>? bindings) {
        bindings = null;

        var pin = PinOf(bytecode: bytecode);

        foreach (var directory in DerivedBytes.ShippingDirectories()) {
            if (!Directory.Exists(path: directory)) { continue; }
            try {
                var store = new ContentAddressedStore(root: directory);

                if (store.TryResolveDerived(kind: Kind, inputHash: pin, hash: out var output) && store.TryGet(pin: output, content: out var encoded)) {
                    bindings = Decode(encoded: encoded);

                    return true;
                }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException) {
            }
        }

        return false;
    }

    private static ContentPin PinOf(ReadOnlySpan<byte> bytecode) => ContentPin.FromDigest(digest: SHA256.HashData(source: bytecode));

    private static byte[] Encode(IReadOnlyList<ShaderInterfaceBinding> bindings) {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(output: stream, encoding: Encoding.UTF8, leaveOpen: true)) {
            writer.Write(value: bindings.Count);
            foreach (var binding in bindings) {
                writer.Write(value: binding.Name);
                writer.Write(value: binding.Set);
                writer.Write(value: binding.Binding);
                writer.Write(value: (int)binding.Kind);
                writer.Write(value: binding.Pushed);
                writer.Write(value: binding.ElementStride);
                writer.Write(value: binding.Count);
                writer.Write(value: binding.Members.Count);
                foreach (var member in binding.Members) {
                    writer.Write(value: member.Name);
                    writer.Write(value: member.Offset);
                    writer.Write(value: (int)member.Type);
                    writer.Write(value: member.Length);
                }
            }
        }

        return stream.ToArray();
    }
    private static IReadOnlyList<ShaderInterfaceBinding> Decode(byte[] encoded) {
        using var reader = new BinaryReader(input: new MemoryStream(buffer: encoded, writable: false), encoding: Encoding.UTF8);
        var bindings = new ShaderInterfaceBinding[Count(reader: reader)];

        for (var index = 0; (index < bindings.Length); index++) {
            var name = reader.ReadString();
            var set = reader.ReadUInt32();
            var binding = reader.ReadUInt32();
            var kind = Defined<GpuBindingKind>(value: reader.ReadInt32());
            var pushed = reader.ReadBoolean();
            var stride = reader.ReadUInt32();
            var count = reader.ReadUInt32();
            var members = new ShaderInterfaceBlockMember[Count(reader: reader)];

            for (var member = 0; (member < members.Length); member++) {
                members[member] = new ShaderInterfaceBlockMember(Name: reader.ReadString(), Offset: reader.ReadUInt32(), Type: Defined<ShaderValueType>(value: reader.ReadInt32()), Length: reader.ReadUInt32());
            }
            bindings[index] = new ShaderInterfaceBinding(Binding: binding, Count: count, ElementStride: stride, Kind: kind, Members: members, Name: name, Pushed: pushed, Set: set);
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length) {
            throw new InvalidDataException(message: "A prepared bindings entry holds bytes past its last binding.");
        }

        return ShaderInterfaceLayout.Ordered(bindings: bindings);
    }
    // A count the remaining bytes could hold: each element takes at least one byte.
    private static int Count(BinaryReader reader) {
        var count = reader.ReadInt32();

        return (((count < 0) || (count > (reader.BaseStream.Length - reader.BaseStream.Position)))
            ? throw new InvalidDataException(message: "A prepared bindings entry's count is out of range.")
            : count);
    }
    private static T Defined<T>(int value) where T : struct, Enum => ((Enum.ToObject(enumType: typeof(T), value: value) is T defined) && Enum.IsDefined(value: defined)
        ? defined
        : throw new InvalidDataException(message: $"A prepared bindings entry names no {typeof(T).Name} {value}."));
}
