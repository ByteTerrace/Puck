using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Puck.Assets;

/// <summary>
/// The Puck code a root assembly can run: the root and every <c>Puck.*</c> assembly it references, directly or
/// transitively, keyed by name in ordinal order. It identifies a derivation by the code that makes it, so a key built on
/// it moves whenever that code does and needs no recorded fingerprint.
/// </summary>
public static class AssemblyReach {
    private static bool IsPuck(string? name) =>
        ((name is not null) && name.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck."));

    /// <summary>Returns <paramref name="root"/> and every <c>Puck.*</c> assembly it reaches through its references.</summary>
    /// <param name="root">The assembly the walk starts from.</param>
    /// <returns>The reached assemblies, keyed by name in ordinal order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    public static SortedDictionary<string, Assembly> Of(Assembly root) {
        ArgumentNullException.ThrowIfNull(argument: root);

        var reached = new SortedDictionary<string, Assembly>(comparer: StringComparer.Ordinal);
        var pending = new Stack<Assembly>();

        pending.Push(item: root);

        while (pending.TryPop(result: out var assembly)) {
            if (!reached.TryAdd(key: (assembly.GetName().Name ?? string.Empty), value: assembly)) {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies()) {
                if (IsPuck(name: reference.Name) && !reached.ContainsKey(key: reference.Name!)) {
                    pending.Push(item: Assembly.Load(assemblyRef: reference));
                }
            }
        }

        return reached;
    }
    /// <summary>Returns the SHA-256 over the compiled content of every assembly <paramref name="root"/> reaches
    /// (<see cref="Of"/>), each named and taken in ordinal order of name. An assembly's content is its metadata with the
    /// module version id and the mapped-field addresses zeroed, its method bodies and its managed resources: what the
    /// compiler made of the source, without the debug directory, whose program-database path names the checkout, and
    /// without what that path moves. Two checkouts that build one source in one configuration agree; any change to the
    /// reached code moves it. An assembly loaded from no file contributes its module version id instead.</summary>
    /// <param name="root">The assembly the walk starts from.</param>
    /// <returns>Sixty-four lowercase hexadecimal digits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    public static string ContentFingerprint(Assembly root) {
        using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

        foreach (var (name, assembly) in Of(root: root)) {
            hash.AppendData(data: Encoding.UTF8.GetBytes(s: $"{name}\n"));
            AppendContent(
                assembly: assembly,
                hash: hash
            );
        }

        return Convert.ToHexStringLower(inArray: hash.GetHashAndReset());
    }

    private static void AppendContent(IncrementalHash hash, Assembly assembly) {
        if (string.IsNullOrEmpty(value: assembly.Location)) {
            hash.AppendData(data: assembly.ManifestModule.ModuleVersionId.ToByteArray());
            return;
        }

        using var stream = File.OpenRead(path: assembly.Location);
        using var image = new PEReader(peStream: stream);
        var reader = image.GetMetadataReader();
        var metadata = image.GetMetadata().GetContent().ToArray();
        var mvid = reader.GetGuid(handle: reader.GetModuleDefinition().Mvid).ToByteArray();
        var guids = reader.GetHeapMetadataOffset(heapIndex: HeapIndex.Guid);
        var at = metadata.AsSpan(start: guids).IndexOf(value: mvid);

        if (at >= 0) {
            metadata.AsSpan(length: mvid.Length, start: (guids + at)).Clear();
        }

        // Mapped field data sits after the debug data in the image, so its addresses move with the database path's length;
        // each row's address column, its first four bytes, is zeroed, and the compiler names such a field by its data's hash.
        var fieldRows = reader.GetTableRowCount(tableIndex: TableIndex.FieldRva);
        var fieldTable = reader.GetTableMetadataOffset(tableIndex: TableIndex.FieldRva);
        var fieldRowSize = reader.GetTableRowSize(tableIndex: TableIndex.FieldRva);

        for (var row = 0; (row < fieldRows); row++) {
            metadata.AsSpan(length: sizeof(int), start: (fieldTable + (row * fieldRowSize))).Clear();
        }

        hash.AppendData(data: metadata);

        foreach (var handle in reader.MethodDefinitions) {
            var address = reader.GetMethodDefinition(handle: handle).RelativeVirtualAddress;

            if (address != 0) {
                var body = image.GetMethodBody(relativeVirtualAddress: address);

                hash.AppendData(data: image.GetSectionData(relativeVirtualAddress: address).GetContent(length: body.Size, start: 0).AsSpan());
            }
        }

        if (image.PEHeaders.CorHeader is { ResourcesDirectory: { Size: > 0 } resources }) {
            hash.AppendData(data: image.GetSectionData(relativeVirtualAddress: resources.RelativeVirtualAddress).GetContent(length: resources.Size, start: 0).AsSpan());
        }
    }
}
