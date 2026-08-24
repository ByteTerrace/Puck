using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>How a <see cref="WorldScreenSource.Session"/> face's destination render projects onto the face — an
/// ordinary head-on camera image, or a WINDOW whose image shears with the viewer's own eye so the destination scene
/// parallaxes against the aperture the way a real opening would.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldScreenProjection>))]
public enum WorldScreenProjection : byte {
    /// <summary>The destination renders through its own declared (or default) camera, unaffected by the viewer's
    /// position — today's behavior, unchanged for every session facet authored before this member existed.</summary>
    Camera,

    /// <summary>The destination renders through an off-axis frustum fitted to THIS face's aperture, apexed at the
    /// viewer's eye mapped through the border pair's isometry (see <c>Puck.World.Client.WorldWindowProjectionMath</c>,
    /// downstream of this project — see docs/project-map.md's layering) — requires this same face's
    /// <see cref="WorldPlacementFace.Portal"/> to author
    /// <see cref="WorldPortalArrival.Mapped"/> with a <see cref="WorldPlacementPortal.Counterpart"/> (refused by name
    /// otherwise — see <see cref="WorldDefinitionValidator"/>), since the aperture and the mapping both come from the
    /// SAME border pair. <see cref="WorldScreenSource.Session.CameraName"/> is ignored under this mode — the camera
    /// is derived every produced frame, never an authored row.</summary>
    Window,
}
/// <summary>An offscreen session render's requested pixel resolution — <c>[width, height]</c> on the wire, the same
/// two-element-array convention <see cref="Puck.Assets.Documents.Vector3JsonConverter"/> establishes for a coordinate
/// triple. Null (the
/// default absent value on <see cref="WorldScreenSource.Session"/>) keeps <c>Puck.SdfVm.Views.WorldSessionView</c>'s
/// existing fixed panel size, so a session facet authored before this member existed renders byte-identically.</summary>
/// <param name="Width">The render width in pixels.</param>
/// <param name="Height">The render height in pixels.</param>
[JsonConverter(typeof(WorldScreenResolutionJsonConverter))]
public readonly record struct WorldScreenResolution(int Width, int Height);
/// <summary>Reads and writes a <see cref="WorldScreenResolution"/> as a two-element JSON array <c>[width, height]</c>
/// — the same array shape <see cref="Puck.Assets.Documents.Vector3JsonConverter"/> uses for a coordinate, applied here directly at the
/// type's own declaration (a struct this project owns, unlike <see cref="System.Numerics.Vector3"/>) rather than a
/// central source-gen registration.</summary>
public sealed class WorldScreenResolutionJsonConverter : JsonConverter<WorldScreenResolution> {
    /// <inheritdoc/>
    public override WorldScreenResolution Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException(message: "a resolution must be a two-element [width, height] array.");
        }

        var width = JsonComponentReader.ReadInt(reader: ref reader, notNumberMessage: "a resolution element must be an integer.");
        var height = JsonComponentReader.ReadInt(reader: ref reader, notNumberMessage: "a resolution element must be an integer.");

        if (
            !reader.Read() ||
            (reader.TokenType != JsonTokenType.EndArray)
        ) {
            throw new JsonException(message: "a resolution array must contain exactly two elements.");
        }

        return new WorldScreenResolution(
            Height: height,
            Width: width
        );
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WorldScreenResolution value, JsonSerializerOptions options) {
        writer.WriteStartArray();
        writer.WriteNumberValue(value: value.Width);
        writer.WriteNumberValue(value: value.Height);
        writer.WriteEndArray();
    }
}
