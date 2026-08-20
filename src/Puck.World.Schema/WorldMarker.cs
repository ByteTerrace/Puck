using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>Which document rows a <see cref="WorldMarkerRow"/> projects one chip per — the source vocabulary. A new
/// kind is a new derived record plus its <see cref="JsonDerivedTypeAttribute"/> line; the fan-out (one chip per
/// tracked row, or one chip for a literal point) is the marker producer's own concern, never the schema's.</summary>
[JsonDerivedType(typeof(WorldMarkerSource.Speakers), typeDiscriminator: "speakers")]
[JsonDerivedType(typeof(WorldMarkerSource.Point), typeDiscriminator: "point")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldMarkerSource {
    private WorldMarkerSource() {
    }

    /// <summary>Tracks every declared <c>speakers</c> row — one chip per row, at the audio director's own resolved
    /// pose (the same pose the mix hears). <see cref="WorldMarkerRing"/>'s <c>radius</c> field reads a tracked row's
    /// <see cref="WorldSpeaker.Bed.Radius"/> when it is a bed; every other speaker kind draws no ring.</summary>
    public sealed record Speakers() : WorldMarkerSource;
    /// <summary>One static world-space chip at an authored position — tracks no document section.</summary>
    /// <param name="Position">The marker's world position.</param>
    public sealed record Point(DocumentVector3 Position) : WorldMarkerSource;
}
/// <summary>A marker's ring policy: a translucent hairline circle at a radius read from a named field of the tracked
/// source row. <see langword="null"/> on <see cref="WorldMarkerRow.Ring"/> draws no ring at all.</summary>
/// <param name="Field">The source row field the ring radius reads. The only field <see cref="WorldMarkerSource.Speakers"/>
/// admits is <c>radius</c> (<see cref="WorldSpeaker.Bed.Radius"/>); a row that is not a bed draws no ring under this
/// policy without refusing (v1's one closed field name — a future source kind names its own).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldMarkerRing(string Field) {
    /// <summary>The only <see cref="Field"/> value <see cref="WorldMarkerSource.Speakers"/> admits.</summary>
    public const string SpeakerRadiusField = "radius";
}
/// <summary>A marker row's look: the icon chip's opacity and plate size, and — only when the row declares a
/// <see cref="WorldMarkerRow.Ring"/> — the ring's color and opacity. Alpha fields are <see cref="BindableScalar"/>
/// (a marker's live-selected emphasis is exactly the kind of dynamism worth binding); the ring color is
/// <see cref="BindableColor"/>, so a world can drive it from live state the same way a theme token can.</summary>
/// <param name="ChipAlpha">The icon chip's opacity, in <c>[0, 1]</c>.</param>
/// <param name="Size">The icon chip's plate half-extent, px. Positive.</param>
/// <param name="RingColor">The ring's stroke color. Required exactly when <see cref="WorldMarkerRow.Ring"/> is
/// authored; omitted otherwise.</param>
/// <param name="RingAlpha">The ring's opacity, in <c>[0, 1]</c>. Required exactly when <see cref="WorldMarkerRow.Ring"/>
/// is authored; omitted otherwise.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldMarkerStyle(
    BindableScalar ChipAlpha,
    float Size,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? RingColor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? RingAlpha = null
);
/// <summary>
/// One row of the <c>markers</c> document section — a producerless world-space overlay chip vocabulary: what
/// projects markers (<see cref="Source"/>), which authored icon draws (<see cref="Icon"/>, resolved through the
/// icon table like every other icon), whether it carries a radius ring (<see cref="Ring"/>), and its look
/// (<see cref="Style"/>). Puck.Overlays owns only the drawing MECHANISM (a chip plate plus an optional ring); every
/// row here supplies the MEANING.
/// </summary>
/// <param name="Id">The row's stable id, unique within the section.</param>
/// <param name="Source">Which rows (or literal point) this row projects a chip per.</param>
/// <param name="Icon">The icon name, resolved through <c>icons.icons</c>.</param>
/// <param name="Ring">The radius-ring policy, or <see langword="null"/> for no ring.</param>
/// <param name="Style">The row's look.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldMarkerRow(
    string Id,
    WorldMarkerSource Source,
    string Icon,
    WorldMarkerStyle Style,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMarkerRing? Ring = null
);
/// <summary>The <see cref="WorldMarkerRow"/> ceilings <see cref="WorldDefinitionValidator"/> enforces. The
/// presentation derives its marker-channel reservation from <see cref="MaxChipsPerSeat"/> through the composition
/// root (<c>WorldOverlayCapacity.FromSchema</c>), never restating the number — the render cost one admitted chip
/// expands into (a plate plus an optional ring) is the writer's own constant.</summary>
public static class WorldMarkerCapacity {
    /// <summary>The projected-chip ceiling one seat draws in a frame — the host admits the nearest rows to the
    /// camera up to this count; anything past it is refused at the marker channel's own boundary, attributed.</summary>
    public const int MaxChipsPerSeat = 16;
    /// <summary>The section's declared row-count ceiling.</summary>
    public const int MaxRows = 32;
}
