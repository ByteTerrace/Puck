using System.Text;
using System.Text.Json;

namespace Puck.World;

/// <summary>One credited author on a <see cref="WorldMetadataSection"/>.</summary>
/// <param name="Name">The author's display name.</param>
/// <param name="Oid">The author's Entra object id, when the author chooses to attach one — see
/// <see cref="WorldEntraObjectId"/>. Authored, not authenticated: nothing here proves the name behind the id.</param>
public sealed record WorldMetadataAuthor(string Name, string? Oid = null);
/// <summary>
/// The <c>metadata</c> document section — an optional, boot-authored-only bag of author-facing facts. Nothing in
/// the engine reads or dispatches on any member here, the same "content, never a case the engine branches on"
/// posture <see cref="WorldPropertyRegistrySection"/> already carries for a property name. Unlike
/// <see cref="WorldDefinition.Extensions"/> — which exists to catch a misspelled top-level section name and
/// refuses any key it does not recognize — this section IS the named, validated home for whatever else an author
/// wants to attach.
/// </summary>
/// <remarks>
/// <para><see cref="Custom"/> is not unconditionally free-form once a document acquires a <c>basis</c>: a delta's
/// compose pass refuses any member literally named <see cref="WorldDocumentBasis.DropMemberName"/> or
/// <see cref="WorldDocumentBasis.ReplaceMemberName"/> at any object depth, including inside <see cref="Custom"/> —
/// refused here, at validation, rather than surfacing later as a compose-time exception the first time a derived
/// world composes. A JSON <see langword="null"/> authored under a <see cref="Custom"/> key in a delta document
/// deletes the inherited key at compose time, the same rule every other nested object in a basis chain follows;
/// it is never stored as a literal JSON null in the composed document.</para>
/// </remarks>
/// <param name="Title">The world's author-facing display name, distinct from its filename.</param>
/// <param name="Description">A free-form author description.</param>
/// <param name="Authors">The credited authors.</param>
/// <param name="Tags">Free author vocabulary (genre, theme, whatever an author wants) — no built-in enum, the same
/// posture <see cref="WorldPropertyRegistrySection.Names"/> already carries for a property name.</param>
/// <param name="Custom">A free-form author bag — see the type remarks for its two compose-time carve-outs.</param>
public sealed record WorldMetadataSection(
    string? Title = null,
    string? Description = null,
    IReadOnlyList<WorldMetadataAuthor>? Authors = null,
    IReadOnlyList<string>? Tags = null,
    IDictionary<string, JsonElement>? Custom = null
) {
    /// <summary>Gets the total UTF-8 byte size of <paramref name="custom"/> — every key plus each value's raw JSON
    /// text — the single measurement the <see cref="WorldMetadataCapacity.MaxCustomBytes"/> cap is compared
    /// against.</summary>
    public static long CustomUtf8ByteCount(IDictionary<string, JsonElement>? custom) {
        if (custom is null) {
            return 0;
        }

        var bytes = 0L;

        foreach (var (key, value) in custom) {
            bytes += Encoding.UTF8.GetByteCount(s: key);
            bytes += Encoding.UTF8.GetByteCount(s: value.GetRawText());
        }

        return bytes;
    }
}
/// <summary>Capacity constants for <see cref="WorldMetadataSection"/> — a made-up, sensible fixture ceiling (see
/// <see cref="WorldPropertyCapacity"/>'s own remarks on this style of constant).</summary>
public static class WorldMetadataCapacity {
    /// <summary>The maximum <see cref="WorldMetadataAuthor.Name"/> length, in UTF-16 code units.</summary>
    public const int MaxAuthorNameLength = 128;
    /// <summary>The maximum declared <see cref="WorldMetadataSection.Authors"/> rows.</summary>
    public const int MaxAuthors = 16;
    /// <summary>The maximum total UTF-8 byte size of <see cref="WorldMetadataSection.Custom"/> (keys plus each
    /// value's raw JSON text).</summary>
    public const int MaxCustomBytes = 8192;
    /// <summary>The maximum <see cref="WorldMetadataSection.Description"/> length, in UTF-16 code units.</summary>
    public const int MaxDescriptionLength = 1024;
    /// <summary>The maximum length of one tag, in UTF-16 code units.</summary>
    public const int MaxTagLength = 32;
    /// <summary>The maximum declared <see cref="WorldMetadataSection.Tags"/> rows.</summary>
    public const int MaxTags = 16;
    /// <summary>The maximum <see cref="WorldMetadataSection.Title"/> length, in UTF-16 code units.</summary>
    public const int MaxTitleLength = 128;
}
