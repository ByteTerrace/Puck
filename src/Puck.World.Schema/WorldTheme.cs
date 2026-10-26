using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>One of the theme's core color roles, by name: what a component style (<see cref="WorldThemeStyleColor"/>)
/// and the drawn cursor (<see cref="WorldHudCursor.Role"/>) name to take a core color rather than a literal. Each names
/// a purpose, never a feature, so it still means something with any one component deleted.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldThemeRole>))]
public enum WorldThemeRole : byte {
    /// <summary>The deepest surface (a held control's seated plate).</summary>
    SurfaceBase,
    /// <summary>A raised surface (a control's resting plate).</summary>
    SurfaceRaised,
    /// <summary>A recessed surface (a well inside a panel).</summary>
    SurfaceInset,
    /// <summary>The panel scrim.</summary>
    ScrimPanel,
    /// <summary>The strip scrim.</summary>
    ScrimStrip,
    /// <summary>The chip scrim.</summary>
    ScrimChip,
    /// <summary>The hairline rule.</summary>
    LineHair,
    /// <summary>The soft rule (a band divider).</summary>
    LineSoft,
    /// <summary>The primary text.</summary>
    TextPrimary,
    /// <summary>The dimmed text.</summary>
    TextDim,
    /// <summary>The muted text.</summary>
    TextMute,
    /// <summary>The accent (also the hover and selected tier).</summary>
    Accent,
    /// <summary>The quiet accent fill.</summary>
    AccentQuiet,
    /// <summary>The ink drawn on the accent.</summary>
    AccentInk,
    /// <summary>The positive status.</summary>
    Positive,
    /// <summary>The warning status.</summary>
    Warning,
    /// <summary>The danger status.</summary>
    Danger,
}
/// <summary>One scrim's fill color plus its own alpha, split apart so a world can retheme opacity independent of
/// hue — the two knobs a scrim (a translucent panel/strip/chip backing) actually varies. A player's high-contrast
/// preference raises a scrim to <see cref="WorldThemeCapacity.HighContrastScrimAlpha"/> when it authors less.</summary>
/// <param name="Color">The scrim's opaque fill color.</param>
/// <param name="Alpha">The scrim's opacity, in <c>[0, 1]</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeScrim(BindableColor Color, BindableScalar Alpha);
/// <summary>The theme's core color roles (<see cref="WorldThemeRole"/>). Every field is a <see cref="BindableColor"/>:
/// a hex literal, or a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> binding.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeColor(
    BindableColor SurfaceBase,
    BindableColor SurfaceRaised,
    BindableColor SurfaceInset,
    WorldThemeScrim ScrimPanel,
    WorldThemeScrim ScrimStrip,
    WorldThemeScrim ScrimChip,
    BindableColor LineHair,
    BindableColor LineSoft,
    BindableColor TextPrimary,
    BindableColor TextDim,
    BindableColor TextMute,
    BindableColor Accent,
    BindableColor AccentQuiet,
    BindableColor AccentInk,
    BindableColor Positive,
    BindableColor Warning,
    BindableColor Danger
);
/// <summary>The theme's spacing steps on a 4px grid, px.</summary>
/// <param name="Space2">Two steps (8px in the default theme).</param>
/// <param name="Space3">Three steps.</param>
/// <param name="Space8">Eight steps (the stage margin).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeSpace(float Space2, float Space3, float Space8);
/// <summary>The theme's 3-step radius scale, px.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeRadius(float Radius1, float Radius2, float Radius3);
/// <summary>The theme's type sizes, px. <see cref="WorldThemeCapacity.TypeAbsoluteFloorSize"/> is enforced at the
/// validator, never authored here.</summary>
/// <param name="MicroSize">The small label size.</param>
/// <param name="MonoSize">The monospace body size.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeType(float MicroSize, float MonoSize);
/// <summary>The theme's stroke widths, px.</summary>
/// <param name="Hairline">The hairline outline width.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeStroke(float Hairline);
/// <summary>The theme's durations, ms.</summary>
/// <param name="DurMed">The medium duration (a transient's fade).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeMotion(float DurMed);
/// <summary>The theme's procedural icon feel (the anti-alias ramps stay engine-side rendering-correctness constants,
/// never authored).</summary>
/// <param name="StrokeHalfWidth">The hairline stroke half-width every procedural glyph/icon draws with, in glyph-local
/// units.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeIcon(float StrokeHalfWidth);
/// <summary>A component style's color: a core role by name (<see cref="Role"/>, so the component follows the world's
/// palette), or its own literal, binding or keys (<see cref="Color"/>). Exactly one is authored.</summary>
/// <param name="Role">The core role the component takes, or <see langword="null"/> when it authors its own color.
/// Omitted from the wire when null.</param>
/// <param name="Color">The component's own color, or <see langword="null"/> when it names a role. Omitted from the wire
/// when null.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeStyleColor(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldThemeRole? Role = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? Color = null
);
/// <summary>The lit-state glow every component's held, selected or echoed state draws: a ring at the shape's edge
/// plus a halo falling off outside it, in the state's own hue (the keycap's held and accent tiers, a toast's ring, a
/// wheel piece's selection). The neutral tier is the held keycap's.</summary>
/// <param name="HaloBlur">The halo's reach, px.</param>
/// <param name="RingAlpha">The ring's alpha.</param>
/// <param name="HaloAlpha">The halo's alpha.</param>
/// <param name="NeutralRingAlpha">The neutral tier's ring alpha.</param>
/// <param name="NeutralHaloAlpha">The neutral tier's halo alpha.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeGlowStyle(float HaloBlur, BindableScalar RingAlpha, BindableScalar HaloAlpha, BindableScalar NeutralRingAlpha, BindableScalar NeutralHaloAlpha);
/// <summary>The keycap: the icon plate the binding bar, the wheel and the chord hints draw, with a physical-button badge
/// at its corner.</summary>
/// <param name="ReferenceSize">The plate height the theme's px tokens are measured at, px: a plate of another size
/// scales its hairline, radius and glow by its own size over this one.</param>
/// <param name="RestOpacity">A resting plate's opacity.</param>
/// <param name="Badge">The badge disc's backing.</param>
/// <param name="BadgeInk">The badge's glyph.</param>
/// <param name="ToggleMarch">The seconds one march of a latched toggle's border takes around the plate; 0 draws the
/// border still.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeKeycapStyle(float ReferenceSize, float RestOpacity, WorldThemeStyleColor Badge, WorldThemeStyleColor BadgeInk, float ToggleMarch);
/// <summary>The binding bar's opacities.</summary>
/// <param name="LabelAlpha">The page name's opacity.</param>
/// <param name="HintAlpha">A chord hint's opacity.</param>
/// <param name="QuietAlpha">An unbound slot's and an unheld modifier's opacity.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeBarStyle(float LabelAlpha, float HintAlpha, float QuietAlpha);
/// <summary>The console panel.</summary>
/// <param name="Echo">The color of an echoed input line and of the prompt.</param>
/// <param name="HeadHeight">The title band's height, px.</param>
/// <param name="SelectionAlpha">The selected input's highlight opacity.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeConsoleStyle(WorldThemeStyleColor Echo, float HeadHeight, float SelectionAlpha);
/// <summary>The drawn pointer cursor (its size, reach and role are the <c>hud</c> section's
/// <see cref="WorldHudCursor"/>).</summary>
/// <param name="Alpha">The ring's opacity.</param>
/// <param name="DotRatio">The center dot's half-extent, as a fraction of the ring radius.</param>
/// <param name="DotMaxHalf">The center dot's half-extent ceiling, px.</param>
/// <param name="LabelGap">The hover label's clearance outside the ring, px.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeCursorStyle(float Alpha, float DotRatio, float DotMaxHalf, float LabelGap);
/// <summary>The radial wheel.</summary>
/// <param name="RingAlpha">A non-active ring's and the active ring label's opacity.</param>
/// <param name="ActiveRingAlpha">The active ring's and the hub's opacity.</param>
/// <param name="LabelAlpha">The hovered sector label's opacity.</param>
/// <param name="HubLabelGap">The gap between the hub's two labels, px.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeWheelStyle(float RingAlpha, float ActiveRingAlpha, float LabelAlpha, float HubLabelGap);
/// <summary>The toast.</summary>
/// <param name="MinHeight">The chip's least height, px; zero shows no toast.</param>
/// <param name="IconSize">The status square's side, px.</param>
/// <param name="RailWidth">The status rail's width, px.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeToastStyle(float MinHeight, float IconSize, float RailWidth);
/// <summary>The authored HUD panels' own chrome.</summary>
/// <param name="TrackAlpha">A gauge track's opacity.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeHudStyle(float TrackAlpha);
/// <summary>The component styles: each component's own geometry, opacities and recipe, in literals and references to
/// the core tokens, so the core names only purposes and a component's look stays the component's.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeStyles(
    WorldThemeGlowStyle Glow,
    WorldThemeKeycapStyle Keycap,
    WorldThemeBarStyle Bar,
    WorldThemeConsoleStyle Console,
    WorldThemeCursorStyle Cursor,
    WorldThemeWheelStyle Wheel,
    WorldThemeToastStyle Toast,
    WorldThemeHudStyle Hud
);
/// <summary>
/// The <c>theme</c> document section: the look the engine's overlay surfaces (the console, the binding bar, the
/// radial wheel, toasts, HUD panels, the drawn cursor) draw with. A keyless section — one theme per document: the
/// core tokens (color roles, spacing, radii, type, stroke, motion, icon) name purposes only, and
/// <see cref="Styles"/> holds each component's own look over them. A document that authors no theme draws the engine's
/// neutral <see cref="Default"/>; a world's own look is a skin it imports.
/// </summary>
/// <param name="Color">The core color roles.</param>
/// <param name="Space">The spacing steps.</param>
/// <param name="Radius">The radius scale.</param>
/// <param name="Type">The type sizes.</param>
/// <param name="Stroke">The stroke widths.</param>
/// <param name="Motion">The durations.</param>
/// <param name="Icon">The procedural icon feel.</param>
/// <param name="Styles">The component styles.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeSection(
    WorldThemeColor Color,
    WorldThemeSpace Space,
    WorldThemeRadius Radius,
    WorldThemeType Type,
    WorldThemeStroke Stroke,
    WorldThemeMotion Motion,
    WorldThemeIcon Icon,
    WorldThemeStyles Styles
) {
    /// <summary>Gets the engine's neutral theme, which a document that authors none draws: greys, a blue accent and
    /// the usual status hues, every component usable and legible over bright and dark scenes alike.</summary>
    public static WorldThemeSection Default { get; } = new(
        Color: new WorldThemeColor(
            Accent: new BindableColor(Raw: "#4C8DFF"),
            AccentInk: new BindableColor(Raw: "#06101F"),
            AccentQuiet: new BindableColor(Raw: "#4C8DFF2E"),
            Danger: new BindableColor(Raw: "#F05252"),
            LineHair: new BindableColor(Raw: "#FFFFFF1F"),
            LineSoft: new BindableColor(Raw: "#FFFFFF8C"),
            Positive: new BindableColor(Raw: "#3FB966"),
            ScrimChip: new WorldThemeScrim(
                Alpha: new BindableScalar(literal: 0.92f),
                Color: new BindableColor(Raw: "#1A1C20")
            ),
            ScrimPanel: new WorldThemeScrim(
                Alpha: new BindableScalar(literal: 0.9f),
                Color: new BindableColor(Raw: "#141518")
            ),
            ScrimStrip: new WorldThemeScrim(
                Alpha: new BindableScalar(literal: 0.86f),
                Color: new BindableColor(Raw: "#141518")
            ),
            SurfaceBase: new BindableColor(Raw: "#101114"),
            SurfaceInset: new BindableColor(Raw: "#0A0B0D"),
            SurfaceRaised: new BindableColor(Raw: "#24262B"),
            TextDim: new BindableColor(Raw: "#A3A7AE"),
            TextMute: new BindableColor(Raw: "#686C73"),
            TextPrimary: new BindableColor(Raw: "#F1F2F4"),
            Warning: new BindableColor(Raw: "#E0A526")
        ),
        Icon: new WorldThemeIcon(StrokeHalfWidth: 0.08f),
        Motion: new WorldThemeMotion(DurMed: 180f),
        Radius: new WorldThemeRadius(
            Radius1: 3f,
            Radius2: 6f,
            Radius3: 9f
        ),
        Space: new WorldThemeSpace(
            Space2: 8f,
            Space3: 12f,
            Space8: 32f
        ),
        Stroke: new WorldThemeStroke(Hairline: 1f),
        Styles: new WorldThemeStyles(
            Bar: new WorldThemeBarStyle(
                HintAlpha: 0.6f,
                LabelAlpha: 0.9f,
                QuietAlpha: 0.7f
            ),
            Console: new WorldThemeConsoleStyle(
                Echo: new WorldThemeStyleColor(Role: WorldThemeRole.Accent),
                HeadHeight: 38f,
                SelectionAlpha: 0.7f
            ),
            Cursor: new WorldThemeCursorStyle(
                Alpha: 0.9f,
                DotMaxHalf: 4f,
                DotRatio: 0.22f,
                LabelGap: 5f
            ),
            Glow: new WorldThemeGlowStyle(
                HaloAlpha: new BindableScalar(literal: 0.3f),
                HaloBlur: 12f,
                NeutralHaloAlpha: new BindableScalar(literal: 0.15f),
                NeutralRingAlpha: new BindableScalar(literal: 0.3f),
                RingAlpha: new BindableScalar(literal: 0.6f)
            ),
            Hud: new WorldThemeHudStyle(TrackAlpha: 0.7f),
            Keycap: new WorldThemeKeycapStyle(
                Badge: new WorldThemeStyleColor(Role: WorldThemeRole.SurfaceInset),
                BadgeInk: new WorldThemeStyleColor(Role: WorldThemeRole.TextPrimary),
                ReferenceSize: 40f,
                RestOpacity: 0.62f,
                ToggleMarch: 0f
            ),
            Toast: new WorldThemeToastStyle(
                IconSize: 24f,
                MinHeight: 40f,
                RailWidth: 2f
            ),
            Wheel: new WorldThemeWheelStyle(
                ActiveRingAlpha: 0.95f,
                HubLabelGap: 2f,
                LabelAlpha: 1f,
                RingAlpha: 0.55f
            )
        ),
        Type: new WorldThemeType(
            MicroSize: 11f,
            MonoSize: 12f
        )
    );
}
/// <summary>The theme's engine-side perceptual floors — never authored. A type size below its floor refuses at boot;
/// the scrim floor is a player's high-contrast preference, applied as the theme resolves.</summary>
public static class WorldThemeCapacity {
    /// <summary>The scrim opacity a player's high-contrast preference raises every scrim to, so text on a scrim stays
    /// legible over whatever bright or busy scene lies under it.</summary>
    public const float HighContrastScrimAlpha = 0.84f;
    /// <summary>The type-size floor, px — below it, MSDF glyph coverage degrades over a moving world.</summary>
    public const float TypeAbsoluteFloorSize = 11f;
}
