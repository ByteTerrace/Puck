using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.Launcher.Release;

/// <summary>One content-addressed object this document names: a path relative to the official tree's root (always
/// <c>objects/sha256/&lt;hex[0..2]&gt;/&lt;hex64&gt;</c>), its full <c>sha256/&lt;hex64&gt;</c> hash (the
/// <see cref="Puck.Assets.ContentAddressedStore"/> ref form — a client fetches this exact object, hashes the bytes,
/// and refuses by name on mismatch), its exact byte size, and the MIME type a static server answers it with.</summary>
/// <param name="Path">The object's path, relative to the official tree's root.</param>
/// <param name="Hash">The object's content hash, as <c>sha256/&lt;hex64&gt;</c>.</param>
/// <param name="Size">The object's exact byte size.</param>
/// <param name="ContentType">The MIME type a server answers this object with.</param>
public sealed record OfficialObjectRef(string Path, string Hash, long Size, string ContentType);
/// <summary>What this manifest was built from: the commit of the tree its worlds were read from, whether that tree
/// differs from the commit, the generator string the world schema stamps into its own <c>x-puck.generator</c>, and
/// the world document schema this build's documents and composed worlds declare. The generator's own build commit is
/// the world schema bundle's <c>x-puck.commit</c>, a separate fact.</summary>
/// <param name="Commit">The HEAD commit of the git checkout the worlds directory was read from, or
/// <see cref="NoCommit"/> when that directory is not inside a checkout or its HEAD names no commit.</param>
/// <param name="Dirty">Whether the worlds directory differs from <paramref name="Commit"/>: a tracked file modified,
/// staged or deleted, or an untracked or ignored file present, anywhere under it. Always <see langword="true"/> with
/// <see cref="NoCommit"/>, since no commit holds what was read.</param>
/// <param name="Generator">The generator string <c>puck schema</c> stamps into <c>x-puck.generator</c>.</param>
/// <param name="WorldSchema">The world document schema (<c>puck.world.definition.v1</c>).</param>
public sealed record OfficialBuildInfo(string Commit, bool Dirty, string Generator, string WorldSchema) {
    /// <summary>The <see cref="Commit"/> of a build whose worlds no commit holds.</summary>
    public const string NoCommit = "none";
}
/// <summary>One file of the shipped engine's AppBundle (a browser-wasm publish output), by name relative to the
/// AppBundle root (forward-slash separated, e.g. <c>_framework/dotnet.js</c>).</summary>
/// <param name="Name">The file's path relative to the AppBundle root.</param>
/// <param name="Path">The object's path, relative to the official tree's root.</param>
/// <param name="Hash">The object's content hash, as <c>sha256/&lt;hex64&gt;</c>.</param>
/// <param name="Size">The object's exact byte size.</param>
/// <param name="ContentType">The MIME type a server answers this object with.</param>
public sealed record OfficialEngineFile(string Name, string Path, string Hash, long Size, string ContentType);
/// <summary>The shipped engine: its entry file name (relative to the AppBundle root, always <c>main.mjs</c> for the
/// browser-wasm host) and every file the AppBundle carries.</summary>
/// <param name="Entry">The AppBundle's entry file name.</param>
/// <param name="Files">Every AppBundle file this build ships.</param>
public sealed record OfficialEngine(string Entry, IReadOnlyList<OfficialEngineFile> Files);
/// <summary>One <c>imports[]</c> entry as a world document authors it: the imported document's path (resolved
/// against the importing document's own directory) and its optional alias.</summary>
/// <param name="Document">The imported document's path, exactly as authored.</param>
/// <param name="As">The alias the import composes under, or <see langword="null"/> for an unaliased import.</param>
public sealed record OfficialImportRef(string Document, string? As = null);
/// <summary>One authoring-workspace file under the worlds directory, published byte for byte so a client can mount
/// the workspace and compile it exactly as on disk: every <c>.puck</c> source, the sidecar locks a compile reads
/// beside a source (<c>&lt;stem&gt;.embeddings.json</c>, <c>&lt;stem&gt;.assets.json</c>), and every
/// <c>.world.json</c> document that has no <c>.puck</c> source.</summary>
/// <param name="Name">The file's path relative to the worlds directory, forward-slash separated
/// (<c>games/klondike.puck</c>).</param>
/// <param name="Path">The object's path, relative to the official tree's root.</param>
/// <param name="Hash">The object's content hash, as <c>sha256/&lt;hex64&gt;</c> — over the file's own bytes.</param>
/// <param name="Size">The object's exact byte size.</param>
/// <param name="ContentType">The MIME type a server answers this object with: <see cref="OfficialSourceContentTypes.Puck"/>
/// for a <c>.puck</c> source, <see cref="OfficialSourceContentTypes.Json"/> for every JSON file.</param>
public sealed record OfficialSourceEntry(string Name, string Path, string Hash, long Size, string ContentType);
/// <summary>The MIME types a <see cref="OfficialSourceEntry"/> is served with.</summary>
public static class OfficialSourceContentTypes {
    /// <summary>Every JSON workspace file: a sourceless <c>.world.json</c> document or a sidecar lock.</summary>
    public const string Json = "application/json";
    /// <summary>A <c>.puck</c> source.</summary>
    public const string Puck = "text/x-puck; charset=utf-8";
}
/// <summary>One world document under the worlds directory: its role in the composition graph, the identity and
/// composition facts read off its own JSON (never a full parsed/validated <c>WorldDefinition</c> — a fragment
/// document is not, on its own, a loadable one), the workspace file that authors it, and its object-store
/// location.</summary>
/// <param name="Name">The document's name (<c>WorldDocumentName</c>), relative to the worlds directory and
/// forward-slash separated, with no file suffix — the spelling a <c>basis</c> or <c>imports[].document</c> uses
/// (<c>games/klondike</c>, <c>puck</c>, <c>shards/quilt-ne</c>).</param>
/// <param name="Source">The <see cref="OfficialSourceEntry.Name"/> of the <see cref="OfficialManifest.Sources"/> file
/// that authors this document: its <c>.puck</c> source when it has one (<c>games/klondike.puck</c>), else its own
/// <c>.world.json</c> file. A composition source authors one document per world it declares, each named beside
/// the source by its declared world name.</param>
/// <param name="Role">One of <c>world</c>, <c>basis</c>, <c>fragment</c>, <c>shard</c> — see
/// <see cref="OfficialDocumentRoles"/>.</param>
/// <param name="DocumentId">The document's own <c>documentId</c>, or <see langword="null"/> when it authors
/// none.</param>
/// <param name="Imports">The document's own <c>imports[]</c> list, exactly as authored (empty when it imports
/// nothing).</param>
/// <param name="Exports">Every name the document's own <c>exports</c> member admits, across all three facets,
/// deduplicated and sorted (empty when it carries no <c>exports</c> member).</param>
/// <param name="Path">The object's path, relative to the official tree's root.</param>
/// <param name="Hash">The object's content hash, as <c>sha256/&lt;hex64&gt;</c> — over the document's JSON: its
/// source's compiled output, or a sourceless document's own file bytes.</param>
/// <param name="Size">The object's exact byte size.</param>
/// <param name="ContentType">The MIME type a server answers this object with.</param>
/// <param name="Pin">The <c>sha256-64/&lt;hex16&gt;</c> content-address pin
/// <c>WorldDefinitionFileSource.ComputeContentHash</c> mints for this document's own bytes, when
/// the document declares neither <c>basis</c> nor <c>imports</c> (the one case that pin covers); <see
/// langword="null"/> for a document the engine would instead pin via a basis/imports composition chain, which this
/// entry does not compute (see <see cref="OfficialComposedEntry.Pin"/> for the one document this tree does compose
/// and pin that way).</param>
public sealed record OfficialDocumentEntry(
    string Name,
    string Source,
    string Role,
    string? DocumentId,
    IReadOnlyList<OfficialImportRef> Imports,
    IReadOnlyList<string> Exports,
    string Path,
    string Hash,
    long Size,
    string ContentType,
    string? Pin
);
/// <summary>One fully-composed world this tree ships — today exactly the root document <c>puck</c>, resolved
/// through its whole basis-and-imports graph, parsed, migrated, and validated, then re-serialized to canonical
/// bytes: the exact document a client boots.</summary>
/// <param name="DocumentId">The composed world's <c>documentId</c>.</param>
/// <param name="Name">The root document's name (<c>puck</c>), spelled as <see cref="OfficialDocumentEntry.Name"/> is.</param>
/// <param name="Path">The object's path, relative to the official tree's root.</param>
/// <param name="Hash">The object's content hash, as <c>sha256/&lt;hex64&gt;</c> — over the canonical composed
/// bytes.</param>
/// <param name="Size">The object's exact byte size.</param>
/// <param name="ContentType">The MIME type a server answers this object with.</param>
/// <param name="Pin">The <c>sha256-64/&lt;hex16&gt;</c> content-address pin
/// <c>WorldDefinitionFileSource.ComputeContentHash</c> mints over the composed, canonical
/// bytes.</param>
/// <param name="Identity">The <c>world.identify</c> payload string
/// (<c>puck:world/&lt;documentId&gt;?schema=&lt;schema&gt;&amp;hash=&lt;pin&gt;</c>) for this composed world, or
/// <see langword="null"/> when the document carries no <c>documentId</c> (the one case that payload refuses to
/// mint).</param>
public sealed record OfficialComposedEntry(
    string DocumentId,
    string Name,
    string Path,
    string Hash,
    long Size,
    string ContentType,
    string? Pin,
    JsonNode? Identity
);
/// <summary>One off-disk asset a shipped document references by row (a music, table, tune, or patch row today — see
/// <see cref="AssetRowFamilies"/>): the row's own name, its authored source path, the raw file's object-store
/// location, and the row family's own canonical document pin (recomputed and checked against the row's declared
/// hash — a mismatch refuses the build by name, exactly as <c>WorldDefinitionValidator</c> refuses a live
/// load).</summary>
/// <param name="Family">The asset family — see <see cref="AssetRowFamilies"/>.</param>
/// <param name="Name">The row's stable name, as authored.</param>
/// <param name="Source">The row's source path, exactly as authored.</param>
/// <param name="Path">The object's path, relative to the official tree's root.</param>
/// <param name="Hash">The object's content hash, as <c>sha256/&lt;hex64&gt;</c> — over the referenced document's raw
/// file bytes.</param>
/// <param name="Size">The object's exact byte size.</param>
/// <param name="ContentType">The MIME type a server answers this object with.</param>
/// <param name="Pin">The row family's own canonical document hash (e.g. <c>MusicCanonicalizer.Canonicalize</c>'s
/// hash for a music row), as <c>sha256/&lt;hex64&gt;</c> — the same identity the row's own declared <c>hash</c>
/// field is checked against at a live load, promoted to a <see cref="Puck.Assets.ContentAddressedStore"/> ref by
/// prefixing it (see that hash's own remarks). <see langword="null"/> only when the row could not be resolved at
/// all (the build already refused by then).</param>
public sealed record OfficialAssetEntry(
    string Family,
    string Name,
    string Source,
    string Path,
    string Hash,
    long Size,
    string ContentType,
    string? Pin
);
/// <summary>
/// The <c>puck.official.manifest.v1</c> document: a local official tree's mutable channel pointer — the shipped browser-wasm
/// engine, the world schema bundle, the authoring workspace under the worlds directory, the world documents it
/// authors, the one fully-composed root world, and every off-disk asset those documents reference — all
/// content-addressed and hash-checked, so a client verifies what it fetches without trusting the transport. Unsigned
/// in this package: publishing (signing, upload, a GitHub workflow) is a separate, later concern — <see cref="Signature"/> is always <see langword="null"/> here.
/// </summary>
/// <param name="Schema">The document version tag (<see cref="CurrentSchema"/>).</param>
/// <param name="Channel">The channel this manifest belongs to (<c>dev</c> for a local dry run; <c>stable</c>/<c>next</c>
/// in production).</param>
/// <param name="Build">What built this manifest.</param>
/// <param name="WorldSchemaBundle">The single-file world schema bundle (<c>puck schema --bundle</c>'s output) this
/// build ships.</param>
/// <param name="Engine">The shipped browser-wasm engine.</param>
/// <param name="Sources">Every authoring-workspace file under the worlds directory, recursively.</param>
/// <param name="Documents">Every world document the authoring workspace authors, recursively.</param>
/// <param name="Composed">Every fully-composed world this tree ships.</param>
/// <param name="Assets">Every off-disk asset the shipped documents reference by row.</param>
/// <param name="Signature">Always <see langword="null"/> in this package — an unsigned draft.</param>
public sealed record OfficialManifest(
    string? Schema,
    string Channel,
    OfficialBuildInfo Build,
    OfficialObjectRef WorldSchemaBundle,
    OfficialEngine Engine,
    IReadOnlyList<OfficialSourceEntry> Sources,
    IReadOnlyList<OfficialDocumentEntry> Documents,
    IReadOnlyList<OfficialComposedEntry> Composed,
    IReadOnlyList<OfficialAssetEntry> Assets,
    JsonNode? Signature
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.official.manifest.v1";

    /// <summary>Gets or sets the unknown members preserved across a round-trip. Null when the document carries none.
    /// A settable (not <c>init</c>) accessor is required: System.Text.Json appends to it during deserialization.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}
/// <summary>The closed <see cref="OfficialDocumentEntry.Role"/> vocabulary: <c>shard</c> for a document under the
/// worlds directory's <c>shards/</c> subdirectory (checked first — a shard also carries a <c>documentId</c> and a
/// <c>basis</c>, which would otherwise read as <c>world</c>); <c>basis</c> for the one document named
/// <c>standard</c>; <c>world</c> for a document declaring a non-empty <c>documentId</c>; <c>fragment</c>
/// for everything else (a document imported into a host — most declare <c>exports</c>, but a handful of unaliased,
/// unexported fragments merge wholesale and still fall here, since they are neither a standalone world nor named nor
/// shard-placed).</summary>
public static class OfficialDocumentRoles {
    /// <summary>The one document named <c>standard</c>.</summary>
    public const string Basis = "basis";
    /// <summary>Everything else — imported into a host, whether or not it declares <c>exports</c>.</summary>
    public const string Fragment = "fragment";
    /// <summary>A document under <c>shards/</c>.</summary>
    public const string Shard = "shard";
    /// <summary>A document declaring a non-empty <c>documentId</c> (and not already classified <see cref="Shard"/>).</summary>
    public const string World = "world";

    /// <summary>Every recognized role.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(comparer: StringComparer.Ordinal) { Shard, Basis, World, Fragment };
}
