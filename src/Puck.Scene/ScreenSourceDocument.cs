using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// One entry of the document's diegetic screen-source table: which provider feeds the screen surface a scene
/// <see cref="ScreenSlabObject"/> declared at <see cref="ScreenIndex"/>. The renderer side of the seam already
/// exists (<c>SdfWorldEngine.SetScreenSource</c>); this is the data that says WHICH source a screen samples. A
/// declared screen surface with no table entry (or a provider with nothing to show) falls back to the flat/
/// procedural screen material — the same dark-pane default an unbooted overworld stand shows.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScreenSourceDocument {
    /// <summary>The screen surface slot (0..3) this entry feeds — matches a scene screen slab's
    /// <see cref="ScreenSlabObject.ScreenIndex"/>.</summary>
    public int ScreenIndex { get; init; }
    /// <summary>The provider that supplies the screen's pixels.</summary>
    public ScreenSourceProvider? Source { get; init; }

    internal void Validate(string path, IReadOnlyList<Viewport> viewports, ValidationErrors errors) {
        if ((ScreenIndex < 0) || (ScreenIndex > 3)) {
            errors.Add(path: $"{path}.screenIndex", message: $"a screen index must be 0..3; found {ScreenIndex}");
        }

        if (Source is null) {
            errors.Add(path: $"{path}.source", message: "a screen-source entry requires a source provider");

            return;
        }

        Source.Validate(errors: errors, path: $"{path}.source", viewports: viewports);
    }
}

/// <summary>
/// A screen-source provider, authored polymorphically like a viewport's <c>source</c>: the <c>$type</c> string
/// selects where the screen's pixels come from. Today's one provider is <see cref="ViewportScreenSource"/> (a
/// gaming-brick viewport's NATIVE framebuffer); live-camera and named sources are future discriminators.
/// </summary>
[JsonDerivedType(typeof(ViewportScreenSource), typeDiscriminator: "viewport")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ScreenSourceProvider {
    internal abstract void Validate(string path, IReadOnlyList<Viewport> viewports, ValidationErrors errors);
}

/// <summary>The screen samples the child at a viewport slot — for a <see cref="GamingBrickSource"/> viewport that is
/// the machine's NATIVE (unresampled) framebuffer, exactly what an overworld stand's screen slab shows: the screen seam
/// samples the source itself, so the child's pane-extent resample is neither needed nor wanted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ViewportScreenSource : ScreenSourceProvider {
    /// <summary>The viewport slot (an index into the document's <c>viewports</c>) whose child feeds this screen.</summary>
    public int Slot { get; init; }

    internal override void Validate(string path, IReadOnlyList<Viewport> viewports, ValidationErrors errors) {
        if ((Slot < 0) || (Slot >= viewports.Count)) {
            errors.Add(path: $"{path}.slot", message: $"slot {Slot} is outside the document's {viewports.Count} viewport(s)");

            return;
        }

        if (viewports[Slot]?.Source is not GamingBrickSource) {
            errors.Add(path: $"{path}.slot", message: $"viewport[{Slot}] is not a gaming-brick source (the one provider a viewport screen source supports today)");
        }
    }
}
