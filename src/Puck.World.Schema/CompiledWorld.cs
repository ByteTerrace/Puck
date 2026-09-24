using System.Buffers;
using System.Reflection;
using System.Text;
using Puck.Assets;

namespace Puck.World;

/// <summary>What keys a whole compiled world: the build that derived it, the machine catalog it composed under, the
/// authored definition it was derived from, and the instance its draws were seeded for. A compiled world whose header
/// differs from the one a boot computes is not a compiled world for that boot and is ignored whole.</summary>
/// <param name="EngineBuild">The engine build's identity (<see cref="CompiledWorld.EngineBuild"/>).</param>
/// <param name="CatalogFingerprint">The composition fingerprint of the machine catalog the definition composed
/// under.</param>
/// <param name="DefinitionHash">The content hash of the authored definition's canonical JSON bytes
/// (<see cref="WorldDefinitionSerialization.Serialize"/>), the same <c>sha256-64</c> pin every other door computes over
/// a definition.</param>
/// <param name="InstanceIdentity">The instance identity the draws were seeded for.</param>
public sealed record CompiledWorldHeader(string EngineBuild, string CatalogFingerprint, AssetContentHash DefinitionHash, string InstanceIdentity);
/// <summary>
/// A compiled world: one world's derived products, stored as a <see cref="ChunkContainer"/> with the magic
/// <c>PWLD</c> beside the document it was derived from (<c>&lt;name&gt;.puckb</c>) or in a boot's cache. Its header is
/// a <see cref="CompiledWorldHeader"/>, and each chunk is one registered derivation's product
/// (<see cref="CompiledWorldChunks"/>), keyed by that derivation's version and the inputs it read beyond the
/// definition.
/// <para>It is a cache with a strong key, never a source of truth: a boot takes from it only what its key still
/// names, re-derives a chunk whose version or inputs moved, keeps the rest, and ignores a file whose header differs.
/// Nothing in it is repaired or adapted. Live and per-device products are never stored: GPU objects, machine
/// instances, mounted addons, adjacency projections and neighbour solids, and the live scene program.</para>
/// </summary>
public static class CompiledWorld {
    /// <summary>The extension of a compiled world's file, which sits beside its document as <c>&lt;name&gt;.puckb</c>.</summary>
    public const string Extension = ".puckb";
    /// <summary>The version of the compiled-world format: its header layout and the rule that each code appears at
    /// most once.</summary>
    public const uint FormatVersion = 1;

    private const int MaximumTextBytes = 4096;

    private static readonly Lazy<string> Build = new(valueFactory: ComputeEngineBuild);

    /// <summary>Gets the magic a compiled world's container opens with.</summary>
    public static ReadOnlySpan<byte> Magic => "PWLD"u8;
    /// <summary>Gets the identity of the running engine build: the <c>sha256-64</c> pin over the module version id of
    /// every <c>Puck.*</c> assembly the document model's assembly reaches, so any change to code a derivation can run
    /// moves it.</summary>
    public static string EngineBuild => Build.Value;

    private static string ComputeEngineBuild() {
        var modules = new SortedDictionary<string, Guid>(comparer: StringComparer.Ordinal);
        var pending = new Stack<Assembly>();

        pending.Push(item: typeof(WorldDefinition).Assembly);

        while (pending.TryPop(result: out var assembly)) {
            var name = (assembly.GetName().Name ?? string.Empty);

            if (!modules.TryAdd(
                key: name,
                value: assembly.ManifestModule.ModuleVersionId
            )) {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies()) {
                if (
                    (reference.Name is { } referenced) &&
                    referenced.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "Puck."
                ) &&
                    !modules.ContainsKey(key: referenced)
                ) {
                    pending.Push(item: Assembly.Load(assemblyRef: reference));
                }
            }
        }

        var text = new StringBuilder();

        foreach (var (name, moduleVersionId) in modules) {
            _ = text.Append(value: name).Append(value: ':').Append(value: moduleVersionId.ToString(format: "N")).Append(value: '\n');
        }

        return AssetContentHash.Compute(content: Encoding.UTF8.GetBytes(s: text.ToString())).ToString();
    }

    /// <summary>Returns the file a compiled world of the document at <paramref name="documentPath"/> sits in beside
    /// it: <c>moth.puck</c> and <c>moth.world.json</c> both map to <c>moth.puckb</c>.</summary>
    /// <param name="documentPath">The document's <c>.puck</c> source or <c>.world.json</c> file.</param>
    /// <returns>The absolute path of the compiled world beside it.</returns>
    public static string Beside(string documentPath) => WorldDocumentName.SidecarFile(
        sourcePath: documentPath,
        suffix: Extension
    );
    /// <summary>Encodes a compiled world.</summary>
    /// <param name="header">The header.</param>
    /// <param name="chunks">The chunks, in registry order, each code once.</param>
    /// <returns>The compiled world's bytes.</returns>
    /// <exception cref="ArgumentException">Two chunks carry one code.</exception>
    public static byte[] Encode(CompiledWorldHeader header, IReadOnlyList<ContainerChunk> chunks) {
        ArgumentNullException.ThrowIfNull(argument: header);
        ArgumentNullException.ThrowIfNull(argument: chunks);

        var codes = new HashSet<ChunkCode>();

        foreach (var chunk in chunks) {
            if (!codes.Add(item: chunk.Code)) {
                throw new ArgumentException(
                    message: $"a compiled world carries chunk '{chunk.Code}' at most once.",
                    paramName: nameof(chunks)
                );
            }
        }

        var writer = new ArrayBufferWriter<byte>();

        writer.WriteText(value: header.EngineBuild);
        writer.WriteText(value: header.CatalogFingerprint);
        writer.WriteUInt64(value: header.DefinitionHash.Value);
        writer.WriteText(value: header.InstanceIdentity);

        return new ChunkContainer(
            chunks: chunks,
            formatVersion: FormatVersion,
            header: writer.WrittenSpan.ToArray()
        ).Encode(magic: Magic);
    }
    /// <summary>Returns the header a boot of <paramref name="authored"/> keys its compiled world by.</summary>
    /// <param name="authored">The parsed, composed, undrawn definition.</param>
    /// <param name="catalogFingerprint">The composition fingerprint of the host's machine catalog.</param>
    /// <param name="instanceIdentity">The instance identity the draws are seeded for.</param>
    /// <returns>The header.</returns>
    public static CompiledWorldHeader HeaderFor(WorldDefinition authored, string catalogFingerprint, string instanceIdentity) {
        ArgumentNullException.ThrowIfNull(argument: authored);

        return new CompiledWorldHeader(
            CatalogFingerprint: catalogFingerprint,
            DefinitionHash: AssetContentHash.Compute(content: WorldDefinitionSerialization.Serialize(definition: authored)),
            EngineBuild: EngineBuild,
            InstanceIdentity: instanceIdentity
        );
    }
    /// <summary>Derives every registered chunk fresh and encodes the compiled world: what <c>puck compile</c> and the
    /// build write beside a document.</summary>
    /// <param name="authored">The parsed, composed, undrawn definition.</param>
    /// <param name="catalogFingerprint">The composition fingerprint of the machine catalog.</param>
    /// <param name="instanceIdentity">The instance identity the draws are seeded for.</param>
    /// <param name="sourceName">The origin echoed in refusals.</param>
    /// <param name="bytes">The compiled world, or <see langword="null"/> when a derivation refused.</param>
    /// <param name="reason">The derivation's named refusal, or empty on success.</param>
    /// <param name="chunks">The registered derivations, or <see langword="null"/> for
    /// <see cref="CompiledWorldChunks.Standard"/>.</param>
    /// <returns><see langword="true"/> when every chunk derived.</returns>
    public static bool TryCompile(WorldDefinition authored, string catalogFingerprint, string instanceIdentity, string sourceName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out byte[]? bytes, out string reason, CompiledWorldChunks? chunks = null) {
        bytes = null;
        chunks ??= CompiledWorldChunks.Standard;

        var context = new CompiledWorldContext(
            authored: authored,
            instanceIdentity: instanceIdentity,
            sourceName: sourceName
        );
        var derived = new List<ContainerChunk>(capacity: chunks.Count);

        foreach (var chunk in chunks) {
            if (!chunks.TryDerive(
                chunk: chunk,
                context: context,
                derived: out var product,
                reason: out reason
            )) {
                return false;
            }

            derived.Add(item: product);
        }

        bytes = Encode(
            chunks: derived,
            header: HeaderFor(
                authored: authored,
                catalogFingerprint: catalogFingerprint,
                instanceIdentity: instanceIdentity
            )
        );
        reason = string.Empty;
        return true;
    }
    /// <summary>Decodes a compiled world.</summary>
    /// <param name="content">The file's bytes; decoded payloads are slices of them.</param>
    /// <param name="header">The header, when this returns <see langword="true"/>.</param>
    /// <param name="container">The chunks, when this returns <see langword="true"/>.</param>
    /// <param name="reason">Why the bytes are not a compiled world, or empty on success.</param>
    /// <returns><see langword="true"/> when the bytes are a canonical compiled world of this format version.</returns>
    public static bool TryDecode(ReadOnlyMemory<byte> content,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out CompiledWorldHeader? header,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out ChunkContainer? container, out string reason) {
        header = null;
        container = null;

        try {
            var decoded = ChunkContainer.Decode(
                content: content,
                magic: Magic
            );

            if (decoded.FormatVersion != FormatVersion) {
                reason = $"compiled-world format version {decoded.FormatVersion} is not {FormatVersion}";
                return false;
            }

            var codes = new HashSet<ChunkCode>();

            foreach (var chunk in decoded.Chunks) {
                if (!codes.Add(item: chunk.Code)) {
                    reason = $"chunk '{chunk.Code}' appears more than once";
                    return false;
                }
            }

            var reader = new CanonicalBinaryReader(content: decoded.Header.Span);
            var engineBuild = reader.ReadText(maximumByteCount: MaximumTextBytes);
            var catalogFingerprint = reader.ReadText(maximumByteCount: MaximumTextBytes);
            var definitionHash = new AssetContentHash(Value: reader.ReadUInt64());
            var instanceIdentity = reader.ReadText(maximumByteCount: MaximumTextBytes);

            reader.ExpectEnd();
            header = new CompiledWorldHeader(
                CatalogFingerprint: catalogFingerprint,
                DefinitionHash: definitionHash,
                EngineBuild: engineBuild,
                InstanceIdentity: instanceIdentity
            );
            container = decoded;
            reason = string.Empty;
            return true;
        } catch (InvalidDataException exception) {
            reason = exception.Message;
            return false;
        }
    }
}
