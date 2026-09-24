using Puck.Assets;
using Puck.World;
using Puck.World.Machines;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Transpiler;

internal static partial class CompileCommand {
    private const int MaximumReasonLength = 240;

    // One catalog per process: the compiled world's header records its fingerprint, and a tree run compiles many
    // documents under it.
    private static readonly Lazy<(WorldMachineCatalog Catalog, string Fingerprint)> CompiledWorldMachines = new(valueFactory: static () => {
        var catalog = CliWorldVocabulary.EnsureInstalled();

        return (catalog, CliWorldVocabulary.Fingerprint(catalog: catalog));
    });

    // The bake pack a run writes: where it lies, whether it keeps what an earlier run left there, and the keys this run's
    // compiled worlds name. A tree run writes one at the root of its output holding exactly its keys; a run without a
    // tree writes one beside each compiled world and keeps the outcomes an earlier compile into that directory left, so
    // compiling documents one at a time into one directory loses none of them.
    internal sealed class BakePackPlan(string path, bool keepsEarlier) {
        public HashSet<ContentPin> Keys { get; } = [];
        public bool KeepsEarlier { get; } = keepsEarlier;
        public string Path { get; } = System.IO.Path.GetFullPath(path: path);
    }

    // `puck compile <name>.world.json`: a document has nothing to lower, so compiling it writes only its compiled
    // world. `besidePath` is where the document stands for the compiled world's name: the document itself, or its
    // mirrored place under a tree run's output.
    private static int CompileDocument(string documentPath, string besidePath, IDictionary<string, string>? written, BakePackPlan? pack) {
        byte[] document;

        try {
            document = File.ReadAllBytes(path: documentPath);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"error: Could not read document '{documentPath}': {exception.Message}");
            return 2;
        }

        return WriteCompiledWorld(
            besidePath: besidePath,
            composeAt: documentPath,
            document: document,
            pack: pack,
            written: written
        );
    }
    // Writes the bake pack holding every key the plan names, from the outcomes this process's compiled worlds left in
    // WorldSourceLoader.Bakes, with the outcomes an earlier run left when the plan keeps them. A plan naming no key
    // writes nothing.
    private static int WriteBakePack(BakePackPlan pack, IDictionary<string, string>? written, string owner) {
        if (pack.Keys.Count == 0) {
            return 0;
        }

        var outcomes = new Dictionary<ContentPin, ReadOnlyMemory<byte>>();

        try {
            if (
                pack.KeepsEarlier &&
                File.Exists(path: pack.Path) &&
                WorldBakePack.TryDecode(content: File.ReadAllBytes(path: pack.Path), pack: out var earlier, reason: out _)
            ) {
                foreach (var key in earlier.Keys) {
                    _ = earlier.TryGet(key: key, outcome: out var outcome);
                    outcomes[key] = outcome;
                }
            }

            foreach (var key in pack.Keys) {
                if (!WorldSourceLoader.Bakes.TryGetHeld(key: key, outcome: out var outcome)) {
                    Console.Error.WriteLine(value: $"error: no outcome for bake key {key.Hex} was derived in this run, so the bake pack '{pack.Path}' cannot hold it.");
                    return 2;
                }

                outcomes[key] = outcome;
            }

            var bytes = WorldBakePack.Encode(outcomes: outcomes);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: pack.Path)!);
            AtomicFile.WriteAllBytes(
                bytes: bytes,
                path: pack.Path
            );

            if (written is not null) {
                written[pack.Path] = owner;
            }

            Console.WriteLine(value: $"Wrote bake pack '{pack.Path}' ({outcomes.Count:N0} outcomes, {bytes.Length:N0} bytes; this process baked {WorldSourceLoader.Bakes.Baked:N0} creations and refused {WorldSourceLoader.Bakes.Refused:N0} in {WorldSourceLoader.Bakes.FieldEvaluations:N0} field evaluations).");
            return 0;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"error: Could not write bake pack '{pack.Path}': {exception.Message}");
            return 2;
        }
    }
    // Writes the compiled world of `document`, composed as the file `composeAt` (where its basis and imports resolve),
    // beside `besidePath`, naming its bakes in `pack`, or in a pack beside it written at once when there is no plan. A
    // document that does not draw as a world on its own, such as a module fragment, has no compiled world; that is
    // reported and is not a failure.
    private static int WriteCompiledWorld(string composeAt, byte[] document, string besidePath, IDictionary<string, string>? written, BakePackPlan? pack) {
        var (catalog, fingerprint) = CompiledWorldMachines.Value;
        var destination = CompiledWorld.Beside(documentPath: besidePath);
        var plan = (pack ?? new BakePackPlan(
            keepsEarlier: true,
            path: Path.Combine(
                path1: Path.GetDirectoryName(path: destination)!,
                path2: WorldBakePack.FileName
            )
        ));

        if (!WorldSourceLoader.TryCompileWorld(
            bakePack: WorldBakePack.Reference(documentPath: destination, packPath: plan.Path),
            catalog: catalog,
            catalogFingerprint: fingerprint,
            compiledWorld: out var bytes,
            document: document,
            path: composeAt,
            reason: out var reason
        )) {
            // A fragment's refusal can quote the whole payload it could not parse; its head names the cause.
            var cause = reason.ReplaceLineEndings(replacementText: " ");

            Console.WriteLine(value: $"No compiled world for '{besidePath}': {((cause.Length > MaximumReasonLength) ? (cause[..MaximumReasonLength] + "...") : cause)}");
            return 0;
        }

        try {
            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            AtomicFile.WriteAllBytes(
                bytes: bytes,
                path: destination
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"error: Could not write compiled world '{destination}': {exception.Message}");
            return 2;
        }

        if (written is not null) {
            written[destination] = composeAt;
        }

        Console.WriteLine(value: $"Compiled world '{destination}' ({bytes.Length:N0} bytes).");

        if (
            CompiledWorld.TryDecode(container: out var container, content: bytes, header: out _, reason: out _) &&
            container.TryFind(chunk: out var bakes, code: WorldBakeChunk.BakeCode) &&
            WorldBakeChunk.TryRead(keys: out var keys, packReference: out _, payload: bakes.Payload.Span, reason: out _)
        ) {
            plan.Keys.UnionWith(other: keys);
        }

        return ((pack is null)
            ? WriteBakePack(owner: composeAt, pack: plan, written: written)
            : 0);
    }
}
