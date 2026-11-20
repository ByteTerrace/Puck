using Puck.Abstractions;
using Puck.Assets;

namespace Puck.World;

public static partial class WorldDefinitionFileSource {
    private static readonly Lazy<CodeClosure> LayerCode = new(static () => CodeClosure.Of(
        directory: AppContext.BaseDirectory,
        roots: [
            ("Puck.World.Schema", "Puck.World.WorldDefinitionFileSource", nameof(ShapeBasis)),
            ("Puck.World.Schema", "Puck.World.WorldDefinitionFileSource", nameof(ShapeImport)),
            ("Puck.World.Schema", "Puck.World.WorldDefinitionFileSource", nameof(ShapeOwn)),
            ("Puck.World.Schema", "Puck.World.WorldDefinitionFileSource", nameof(DecodeJson)),
            ("Puck.World.Schema", "Puck.World.WorldDefinitionFileSource", nameof(HoldLayer)),
            ("Puck.World.Schema", "Puck.World.WorldDefinitionFileSource", nameof(ReadLayer)),
            ("Puck.World.Schema", "Puck.World.WorldDefinitionFileSource", nameof(WriteLayer)),
            ("Puck.World.Schema", "Puck.World.WorldModuleExports", nameof(WorldModuleExports.Walk)),
        ],
        stopTypes: new HashSet<string>(collection: [typeof(CodeClosure).FullName!], comparer: StringComparer.Ordinal)
    ));

    private static string LayerCodeIdentity(IWorldCompositionStore store) {
        if (CodeRevision.Current == 0) {
            return store.GetLayerCodeHash(derive: static () => LayerCode.Value.Hash.ToString(format: "x32"));
        }
        var code = LayerCode.Value;
        return $"{code.Hash:x32}{CodeRevision.AppliedTo(reads: code.Reads)}";
    }

    private static string LayerKey(string code, string kind, string input, string path, string target, string catalog, bool hasCatalog, string? alias, string description, bool walk) {
        // A relative asset reference is shaped against both paths. Keep their spelling, which can reach the JSON.
        // Lengths delimit authored text, including aliases and descriptions containing punctuation.
        return ContentPin.OfText(text: $"{code}:{kind}:{input}:{path.Length}:{path}:{target.Length}:{target}:{hasCatalog}:{catalog.Length}:{catalog}:{alias?.Length ?? -1}:{alias}:{description.Length}:{description}:{walk}").Hex;
    }

    // The path index may be stale. Its layers are suitable as operands of the old merge, and a current slot can
    // reuse one only when it computes that same input and code key. Layer entries go through the compile store's
    // key and integrity checks before the index can supply them.
    internal sealed record LayerIndex(string? BasisName, bool HasImports, (string Document, string? Alias, string Key)[] Imports, string? BasisKey, string OwnKey, Member[] Composed) {
        public ComposedLayers? Load(IWorldCompositionStore store, byte[] ownBytes) {
            var own = ReadCachedLayer(store: store, key: OwnKey);
            var basis = ((BasisKey is null) ? null : ReadCachedLayer(store: store, key: BasisKey));

            if ((own is null) || ((BasisKey is not null) && (basis is null))) { return null; }
            var layers = new ComposedLayer[Imports.Length];
            var entries = new (string Document, string? Alias)[Imports.Length];

            for (var index = 0; index < layers.Length; index++) {
                var (document, alias, key) = Imports[index];

                if (ReadCachedLayer(store: store, key: key) is not { } layer) { return null; }
                layers[index] = layer;
                entries[index] = (document, alias);
            }
            return new ComposedLayers(ownBytes, BasisName, HasImports, entries, basis, layers, own, Composed);
        }
    }

    internal static LayerIndex? ReadLayerIndex(BinaryReader reader, byte[] json) {
        if (!reader.ReadBoolean()) { return null; }

        var basisName = ReadOptional(reader);
        var basisKey = ((basisName is null) ? null : ReadKey(reader));
        var hasImports = reader.ReadBoolean();
        var imports = new (string Document, string? Alias, string Key)[BinaryCacheEntry.ReadCount(reader)];

        for (var index = 0; index < imports.Length; index++) {
            imports[index] = (reader.ReadString(), ReadOptional(reader), ReadKey(reader));
        }
        if (!hasImports && (imports.Length != 0)) { throw new InvalidDataException("A composition index has unlisted imports."); }
        return new LayerIndex(basisName, hasImports, imports, basisKey, ReadKey(reader), ReadMembers(reader, json));
    }

    internal static void WriteLayerIndex(BinaryWriter writer, ComposedLayers? layers) {
        var indexed = layers?.Own.Key is not null;

        writer.Write(indexed);
        if (!indexed) { return; }
        WriteOptional(writer, layers!.BasisName);
        if (layers.Basis is { } basis) { writer.Write(basis.Key!); }
        writer.Write(layers.HasImports);
        writer.Write(layers.Imports.Count);
        for (var index = 0; index < layers.Imports.Count; index++) {
            var (document, alias) = layers.Imports[index];

            writer.Write(document);
            WriteOptional(writer, alias);
            writer.Write(layers.ImportLayers[index].Key!);
        }
        writer.Write(layers.Own.Key!);
        WriteMembers(writer, layers.Composed);
    }

    private static ComposedLayer? ReadCachedLayer(IWorldCompositionStore store, string key) {
        var layer = store.ReadLayer(key: key, read: reader => ReadLayer(reader, key));

        WorldBootWork.Count(kind: layer is null ? WorldBootWork.CompositionLayerMisses : WorldBootWork.CompositionLayerHits);
        return layer;
    }

    private static ComposedLayer ReadLayer(BinaryReader reader, string key) {
        var json = reader.ReadBytes(BinaryCacheEntry.ReadCount(reader));
        var members = ReadMembers(reader, json);
        var path = reader.ReadString();
        var description = reader.ReadString();
        var exports = reader.ReadBoolean() ? new WorldExports(ReadNames(reader), ReadNames(reader), ReadNames(reader)) : null;
        WorldModuleExports.Layer? walk = null;

        if (reader.ReadBoolean()) {
            walk = new WorldModuleExports.Layer(description);
            walk.Declared.UnionWith(ReadNames(reader));
            var count = BinaryCacheEntry.ReadCount(reader);

            for (var index = 0; index < count; index++) {
                var name = reader.ReadString();
                var facet = (WorldExportFacet)reader.ReadByte();

                if (facet > WorldExportFacet.Binding) { throw new InvalidDataException("Invalid cached reference facet."); }
                walk.References.Add((name, facet, null));
            }
        }
        return new ComposedLayer(path, null, key, json, members, walk, exports, description);
    }

    private static void WriteLayer(BinaryWriter writer, ComposedLayer layer) {
        writer.Write(layer.Json.Length);
        writer.Write(layer.Json);
        WriteMembers(writer, layer.Members);
        writer.Write(layer.Path);
        writer.Write(layer.Description);
        writer.Write(layer.Exports is not null);
        if (layer.Exports is { } exports) {
            WriteNames(writer, exports.Of(WorldExportFacet.Read));
            WriteNames(writer, exports.Of(WorldExportFacet.Action));
            WriteNames(writer, exports.Of(WorldExportFacet.Binding));
        }
        writer.Write(layer.Walk is not null);
        if (layer.Walk is { } walk) {
            WriteNames(writer, walk.Declared);
            writer.Write(walk.References.Count);
            foreach (var (name, facet, _) in walk.References) {
                writer.Write(name);
                writer.Write((byte)facet);
            }
        }
    }

    private static string ReadKey(BinaryReader reader) {
        var key = reader.ReadString();
        return ContentPin.TryParseHex(hex: key, pin: out _) ? key : throw new InvalidDataException("Invalid composition layer key.");
    }

    private static string? ReadOptional(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    private static void WriteOptional(BinaryWriter writer, string? value) {
        writer.Write(value is not null);
        if (value is not null) { writer.Write(value); }
    }

    private static string[] ReadNames(BinaryReader reader) {
        var names = new string[BinaryCacheEntry.ReadCount(reader)];
        for (var index = 0; index < names.Length; index++) { names[index] = reader.ReadString(); }
        return names;
    }

    private static void WriteNames(BinaryWriter writer, IReadOnlyCollection<string> names) {
        writer.Write(names.Count);
        foreach (var name in names) { writer.Write(name); }
    }

    private static Member[] ReadMembers(BinaryReader reader, byte[] json) {
        var members = new Member[BinaryCacheEntry.ReadCount(reader)];
        var end = 1;

        for (var index = 0; index < members.Length; index++) {
            var member = new Member(reader.ReadString(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

            if ((member.KeyStart != (index == 0 ? end : (end + 1))) || (member.Start <= member.KeyStart) || (member.Start >= json.Length) ||
                (member.Length <= 0) || (member.Length > (json.Length - member.Start - 1))) {
                throw new InvalidDataException("Invalid cached composition member range.");
            }
            end = member.Start + member.Length;
            members[index] = member;
        }
        if ((json.Length < 2) || (end != (json.Length - 1)) || (json[0] != (byte)'{') || (json[end] != (byte)'}')) {
            throw new InvalidDataException("Invalid cached composition object.");
        }
        return members;
    }

    private static void WriteMembers(BinaryWriter writer, Member[] members) {
        writer.Write(members.Length);
        foreach (var member in members) {
            writer.Write(member.Name);
            writer.Write(member.KeyStart);
            writer.Write(member.Start);
            writer.Write(member.Length);
        }
    }
}
