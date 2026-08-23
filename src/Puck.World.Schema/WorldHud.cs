using System.Globalization;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>Where a <see cref="WorldHudPanel"/> draws relative to the five first-party overlay writers (the console
/// panel, binding bars, gizmos, editor HUD, and toast) — the banded pipeline's ordering key.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldHudLayer>))]
public enum WorldHudLayer : byte {
    /// <summary>Draws before the first-party writers (document order among <see cref="Under"/> panels).</summary>
    Under,

    /// <summary>Draws after the first-party writers, or after the replace panels when any are live (document order
    /// among <see cref="Over"/> panels) — always the topmost band.</summary>
    Over,

    /// <summary>Takes the base slot the five first-party writers would otherwise occupy: while at least one live
    /// panel declares <see cref="Replace"/>, every replace panel renders itself there (document order) and the five
    /// first-party writers do not run. Removing the last replace panel restores them.</summary>
    Replace,
}
/// <summary>A <see cref="WorldHudElement"/>'s rendered kind — the schema→render expansion cost differs per kind (see
/// <see cref="WorldHudCapacity"/>).</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldHudElementKind>))]
public enum WorldHudElementKind : byte {
    /// <summary>A filled rect — 1 render element, no binding.</summary>
    Rect,

    /// <summary>A fixed-cell text run — the authored <see cref="WorldHudElement.Text"/> string, or (when
    /// <see cref="WorldHudElement.Binding"/> names one) the live-resolved binding's text form.</summary>
    Text,

    /// <summary>A fill-bar readout of a bound binding's normalized 0..1 value (0 when unbound).</summary>
    Gauge,

    /// <summary>A sampled frame: the element rect shows the frame a <see cref="WorldFrameSource"/> produces
    /// (camera/view/probe/capture) — the picture-in-picture element kind (e.g. a face-cam overlay).</summary>
    Frame,
}
/// <summary>How a <see cref="WorldHudElementKind.Frame"/> element maps its sampled frame's aspect ratio onto its own
/// rect — the same uv-mapping choice a screen material or a UI image element makes.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldHudFrameFit>))]
public enum WorldHudFrameFit : byte {
    /// <summary>Scales the frame to fill the rect, cropping whichever axis overflows.</summary>
    Cover,

    /// <summary>Scales the frame to fit entirely within the rect, letterboxing whichever axis falls short.</summary>
    Contain,

    /// <summary>Scales each axis independently to fill the rect exactly, distorting the frame's aspect ratio.</summary>
    Stretch,
}
/// <summary>A <see cref="WorldHudPanel"/>'s chrome recipe — the authored twin of <c>Puck.Overlays.OverlayPanelStyle</c>
/// (Puck.World.Schema must not reference Puck.Overlays; the renderer maps this token to the concrete style).</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldHudPanelStyle>))]
public enum WorldHudPanelStyle : byte {
    /// <summary>The full panel scrim.</summary>
    Panel,

    /// <summary>The strip scrim (a thinner readout band).</summary>
    Strip,

    /// <summary>The chip scrim (a small badge-sized panel).</summary>
    Chip,
}
/// <summary>A <see cref="WorldHudElement"/>'s color role — a curated authored subset of <c>Puck.Overlays.OverlayColorRole</c>
/// (Puck.World.Schema must not reference Puck.Overlays; the renderer maps this token to the concrete role).</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldHudStyleToken>))]
public enum WorldHudStyleToken : byte {
    /// <summary>The primary text/fill tone.</summary>
    Primary,

    /// <summary>The muted/dim tone.</summary>
    Dim,

    /// <summary>The accent tone.</summary>
    Accent,

    /// <summary>The positive/healthy tone.</summary>
    Positive,

    /// <summary>The warning tone.</summary>
    Warning,

    /// <summary>The danger tone.</summary>
    Danger,
}
/// <summary>A normalized rect (origin top-left, Y down) — a <see cref="WorldHudPanel"/>'s rect is in screen space
/// [0, 1] × [0, 1]; a <see cref="WorldHudElement"/>'s rect is in its owning panel's local [0, 1] × [0, 1] space.</summary>
/// <param name="X">The rect's left edge, normalized.</param>
/// <param name="Y">The rect's top edge, normalized.</param>
/// <param name="Width">The rect's width, normalized — must be positive.</param>
/// <param name="Height">The rect's height, normalized — must be positive.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct WorldHudRect(float X, float Y, float Width, float Height);
/// <summary>One ranked source candidate of a <see cref="WorldHudElementKind.Frame"/> element: the frame shown while
/// <paramref name="When"/> holds. Candidates are walked in authored order every frame and the first holding one wins;
/// a <see langword="null"/> predicate always holds, so the last row is the default.</summary>
/// <param name="Source">The sampled frame while this candidate wins.</param>
/// <param name="When">The condition, evaluated for the panel's seat.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldHudFrameCandidate(WorldFrameSource Source, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverlayPredicate? When = null);
/// <summary>One HUD element row inside a <see cref="WorldHudPanel"/> — a stable id (unique within the owning panel),
/// its kind, its local rect, its color role, an authored literal string (meaningful for <see cref="WorldHudElementKind.Text"/>),
/// and an optional binding into the closed <see cref="HudBindingVocabulary"/> (meaningful for
/// <see cref="WorldHudElementKind.Text"/> and <see cref="WorldHudElementKind.Gauge"/> — a bound text element's live
/// value replaces the authored literal; a bound gauge element's live value drives its fill; an unbound gauge draws
/// empty).</summary>
/// <param name="Id">The element's stable id (unique within the owning panel — the <c>world.row.set hud.panels</c>/
/// <c>.remove</c> mutation address).</param>
/// <param name="Kind">The element's rendered kind.</param>
/// <param name="Rect">The element's rect, normalized to the owning panel's local space.</param>
/// <param name="Style">The element's color role.</param>
/// <param name="Text">The authored literal string a <see cref="WorldHudElementKind.Text"/> element draws when neither
/// <paramref name="Binding"/> nor <paramref name="Template"/> is set; ignored for <see cref="WorldHudElementKind.Rect"/>
/// and <see cref="WorldHudElementKind.Gauge"/>. Omitted from the wire when null.</param>
/// <param name="Binding">A closed <see cref="HudBindingVocabulary"/> token, or <see langword="null"/> for an unbound
/// element. Refused alongside <paramref name="Template"/> — exactly one live-value source, never both. Omitted from
/// the wire when null.</param>
/// <param name="Template">A <see cref="WorldHudElementKind.Text"/> element's template string — authored literal text
/// interleaved with <c>{token}</c> placeholders, each a closed <see cref="HudBindingVocabulary"/> token resolved
/// through the same operand path <paramref name="Binding"/> uses (see <see cref="HudTemplate"/> for the brace/escape
/// grammar). A richer binding source than <paramref name="Binding"/> — many live facts composed into one string
/// instead of one — never both on the same element. Ignored for <see cref="WorldHudElementKind.Rect"/> and
/// <see cref="WorldHudElementKind.Gauge"/> (a gauge's fill is one fraction; it has no composed string to show).
/// Omitted from the wire when null.</param>
/// <param name="Source">A <see cref="WorldHudElementKind.Frame"/> element's sampled frame — the same
/// <see cref="WorldFrameSource"/> vocabulary a screen row or probe socket plugs into (camera/view/probe/capture).
/// Required for <see cref="WorldHudElementKind.Frame"/>; refused on every other kind. Omitted from the wire when
/// null.</param>
/// <param name="Fit">A <see cref="WorldHudElementKind.Frame"/> element's aspect-fit policy. Ignored for every other
/// kind. Omitted from the wire at its default (<see cref="WorldHudFrameFit.Cover"/>).</param>
/// <param name="Mirror">Whether a <see cref="WorldHudElementKind.Frame"/> element flips its sampled frame
/// horizontally — a face cam is conventionally mirrored. Ignored for every other kind. Omitted from the wire at its
/// default (<see langword="false"/>).</param>
/// <param name="Radius">A <see cref="WorldHudElementKind.Frame"/> element's corner-rounding radius, in pixels. Must
/// be finite and non-negative. Ignored for every other kind. Omitted from the wire at its default (0).</param>
/// <param name="Opacity">A <see cref="WorldHudElementKind.Frame"/> element's sampled-frame alpha, in [0, 1]. Ignored
/// for every other kind.</param>
/// <param name="Sources">A <see cref="WorldHudElementKind.Frame"/> element's ranked source candidates — a portrait that
/// shows the speaking character, else the webcam when the player chose it, else the seat's own avatar. Refused beside
/// <paramref name="Source"/>: a bare <paramref name="Source"/> is exactly a one-entry list with no condition. At most
/// <see cref="WorldHudCapacity.MaxFrameCandidatesPerElement"/> entries, every one counted toward
/// <see cref="WorldHudCapacity.MaxFrameSources"/>.</param>
/// <param name="FadeSeconds">How long a <see cref="WorldHudElementKind.Frame"/> element cross-fades when its winning
/// candidate changes; 0 cuts. Finite and non-negative.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldHudElement(
    string Id,
    WorldHudElementKind Kind,
    WorldHudRect Rect,
    WorldHudStyleToken Style,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Binding = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Template = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldFrameSource? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] WorldHudFrameFit Fit = WorldHudFrameFit.Cover,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Mirror = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float Radius = 0f,
    float Opacity = 1f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldHudFrameCandidate>? Sources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float FadeSeconds = 0f
) {
    /// <summary>Gets the frame candidates in rank order — <see cref="Sources"/>, else the bare <see cref="Source"/>
    /// as one unconditional entry, else none. The one shape every reader of a frame element's sources takes.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldHudFrameCandidate> FrameCandidates =>
        (Sources
            ?? ((Source is { } single)
                ? [new WorldHudFrameCandidate(Source: single)]
                : []));
}
/// <summary>One HUD panel row — a stable id (unique within the section), a normalized viewport rect in screen space,
/// which band it draws in, its chrome style, and its child elements. <c>WorldMutation.UpsertHudPanel</c> carries
/// the whole row (elements included) as one cross-row transaction boundary; <c>WorldMutation.UpsertHudElement</c>/
/// <c>WorldMutation.RemoveHudElement</c> read-modify-write a single element within an already-declared panel.</summary>
/// <param name="Id">The panel's stable id (unique within the section — the <c>world.row.set hud.panels</c>/<c>world.row.remove hud.panels</c>
/// mutation address).</param>
/// <param name="Rect">The panel's viewport rect, normalized to screen space.</param>
/// <param name="Layer">Which band the panel draws in.</param>
/// <param name="Style">The panel's chrome recipe.</param>
/// <param name="Elements">The panel's child elements (default empty), each a whole-row unit under
/// <see cref="WorldHudCapacity.MaxElementsPerPanel"/>.</param>
/// <param name="Visible">The panel's visibility condition over presentation facts, or <see langword="null"/> for always.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldHudPanel(string Id, WorldHudRect Rect, WorldHudLayer Layer, WorldHudPanelStyle Style, IReadOnlyList<WorldHudElement> Elements, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverlayPredicate? Visible = null) {
    private readonly IReadOnlyList<WorldHudElement> m_elements = (Elements ?? []);

    /// <summary>Gets the panel's child elements. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="WorldMotionModel.Grounded.Response"/>'s does.</summary>
    public IReadOnlyList<WorldHudElement> Elements {
        get => m_elements;
        init => m_elements = (value ?? []);
    }
}
/// <summary>The bare (non-hovering) drawn cursor's palette role — the authored twin of the overlay color roles the
/// renderer maps this token onto (Puck.World.Schema must not reference Puck.Overlays). Hover always lights the accent
/// tier regardless, so accent here makes the bare cursor and its hover state indistinguishable — legal, but a
/// world that wants hover to read should pick another role.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldHudCursorRole>))]
public enum WorldHudCursorRole : byte {
    /// <summary>The primary text hue.</summary>
    TextPrimary,

    /// <summary>The dimmed text hue.</summary>
    TextDim,

    /// <summary>The accent hue (also the hover tier).</summary>
    Accent,

    /// <summary>The console's phosphor hue.</summary>
    Phosphor,
}
/// <summary>The drawn pointer cursor's per-world presentation policy — feel numbers each world plausibly wants
/// different (a fast world reaches farther, an underwater world reads better with a larger glow), so they are
/// document fields on the hud defaults row, never engine constants. Presentation-only: the cursor, its hover
/// highlight, and its label never touch simulation state.</summary>
/// <param name="HoverRadius">The cursor ray's hover reach in world units — how far into the world a pointer resolves
/// a hovered row.</param>
/// <param name="SizePx">The drawn cursor's ring radius, pixels.</param>
/// <param name="Role">The bare cursor's palette role; hover lights the accent tier regardless.</param>
/// <param name="Visible">The drawn cursor's visibility condition, or <see langword="null"/> for always.</param>
public sealed record WorldHudCursor(float HoverRadius, float SizePx, WorldHudCursorRole Role, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverlayPredicate? Visible = null);
/// <summary>The <c>hud</c> document section's defaults row (the <c>WorldMutation.SetHudDefaults</c> mutation
/// target).</summary>
/// <param name="Enabled">Whether the world-scope HUD panels render at all — a world-level kill switch independent of
/// any individual panel's row (a diegetic reveal gate can flip this without editing every panel).</param>
/// <param name="Cursor">The drawn pointer cursor's presentation policy, or <see langword="null"/> for no drawn
/// cursor at all — the engine draws no cursor of its own; the standard policy is AUTHORED, in
/// <c>Assets/worlds/standard.world.json</c>. Whole-row replace semantics apply: a <c>SetHudDefaults</c> authored
/// without it clears any earlier authored policy back to hidden.</param>
/// <param name="Visible">The visibility condition every world-scope panel is gated by, beside its own, or
/// <see langword="null"/> for always.</param>
public sealed record WorldHudDefaults(bool Enabled, WorldHudCursor? Cursor = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverlayPredicate? Visible = null);
/// <summary>The <c>hud</c> document section: the world-scope defaults plus the authored panel rows. A required section
/// every document carries; an empty panel list draws nothing (the built-in default).</summary>
/// <param name="Defaults">The section defaults.</param>
/// <param name="Panels">The authored world-scope panels (default empty), capped at
/// <see cref="WorldHudCapacity.MaxWorldPanels"/>, each capped at <see cref="WorldHudCapacity.MaxElementsPerPanel"/>
/// elements.</param>
public sealed record WorldHudSection(WorldHudDefaults Defaults, IReadOnlyList<WorldHudPanel> Panels) {
    private readonly IReadOnlyList<WorldHudPanel> m_panels = (Panels ?? []);

    /// <summary>Gets the inert absence: HUD disabled, no cursor, no panels. The engine holds no HUD policy of its
    /// own — the standard enabled-with-cursor row is AUTHORED, in <c>Assets/worlds/standard.world.json</c>, and a
    /// world inherits it by naming that document as its basis.</summary>
    public static WorldHudSection Absent { get; } = new(
        Defaults: new WorldHudDefaults(Enabled: false),
        Panels: []
    );
    /// <summary>Gets the authored world-scope panels. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="WorldMotionModel.Grounded.Response"/>'s does.</summary>
    public IReadOnlyList<WorldHudPanel> Panels {
        get => m_panels;
        init => m_panels = (value ?? []);
    }
}
/// <summary>The document contract's HUD ceilings — what a world may author at world scope and what an identity-owned
/// world may author at seat scope — read by <see cref="WorldDefinitionValidator"/> for both. The presentation derives
/// its overlay reservations from these same constants through the composition root (they cross the layering as
/// constructor data, never restated); the render cost each authored element expands into is the overlay writer's own
/// constant.</summary>
public static class WorldHudCapacity {
    /// <summary>The number of structurally unique frame sources one HUD section may name. Repeated elements naming
    /// the same source share one presentation slot. A cross-layer law pins this schema constant to the overlay's
    /// independently declared fixed shader-slot count.</summary>
    public const int MaxFrameSources = 8;
    /// <summary>The per-panel element-row ceiling (world scope).</summary>
    public const int MaxElementsPerPanel = 24;
    /// <summary>The most ranked source candidates one frame element carries.</summary>
    public const int MaxFrameCandidatesPerElement = 4;
    /// <summary>The per-seat player-scope panel's element-row ceiling — capped smaller than the world scope's
    /// <see cref="MaxElementsPerPanel"/> because it is confined to a single seat's viewport rather than the whole
    /// screen.</summary>
    public const int MaxElementsPerSeatPanel = 12;
    /// <summary>The seat-scope panel-row ceiling: how many <c>hud.panels</c> rows an identity-owned world may author.
    /// <see cref="WorldIdentity.Hud"/> is that one panel.</summary>
    public const int MaxSeatPanels = 1;
    /// <summary>The world-scope panel-row ceiling.</summary>
    public const int MaxWorldPanels = 4;
}
/// <summary>The approved closed v1 binding vocabulary a <see cref="WorldHudElement.Binding"/> names — validated at
/// document validation (refuse-unknown by name) and resolved render-side, once per frame, by the writer.</summary>
public enum HudBindingKind : byte {
    /// <summary>The live server tick counter.</summary>
    WorldTick,

    /// <summary>The live average frames-per-second.</summary>
    WorldFps,

    /// <summary>A local seat's live body position, X component.</summary>
    SeatPositionX,

    /// <summary>A local seat's live body position, Y component.</summary>
    SeatPositionY,

    /// <summary>A local seat's live body position, Z component.</summary>
    SeatPositionZ,

    /// <summary>The live active-population count.</summary>
    PopulationActive,

    /// <summary>A named <c>state</c> row's live value, or one of its cells — see <see cref="HudBinding.StateName"/>/
    /// <see cref="HudBinding.StateCellKey"/>. The binding shape is closed vocabulary: <c>state.&lt;row&gt;</c> binds
    /// the row's own slot cell, and <c>state.&lt;row&gt;.&lt;key&gt;</c> binds one named cell — unambiguous because
    /// neither a row nor a cell name can hold a dot, so the first dot after the <c>state.</c> prefix is always the
    /// grammar separator. Whether the row (and, for the cell form, the key) actually resolves to declared document
    /// data is validated separately: <see cref="WorldDefinitionValidator"/> checks world-scope panels against the
    /// document's own <c>state</c> section, while a seat-scope panel, authored independent of any particular world,
    /// refuses every <c>state.*</c> token instead.</summary>
    StateNamed,
}
/// <summary>One parsed binding — the kind plus (for a <see cref="HudBindingKind.SeatPositionX"/>/Y/Z kind) the
/// 1-based seat index the token named, or (for <see cref="HudBindingKind.StateNamed"/>) the state row name and,
/// for the cell form, the cell key within it.</summary>
/// <param name="Kind">The binding kind.</param>
/// <param name="SeatIndex">The 1-based seat index for a seat-position kind; 0 for every other kind.</param>
/// <param name="StateName">The state row name for a <see cref="HudBindingKind.StateNamed"/> kind;
/// <see langword="null"/> for every other kind.</param>
/// <param name="StateCellKey">The cell key for a <c>state.&lt;row&gt;.&lt;key&gt;</c> token; <see langword="null"/>
/// for a plain <c>state.&lt;row&gt;</c> token (the row's own slot) and for every other kind.</param>
public readonly record struct HudBinding(HudBindingKind Kind, int SeatIndex, string? StateName = null, string? StateCellKey = null);
/// <summary>
/// The closed v1 HUD binding vocabulary: <c>world.tick</c>, <c>world.fps</c>, <c>seat.&lt;n&gt;.position.{x,y,z}</c>
/// (1-based seat index, <c>1..</c><see cref="WorldPopulationLimits.LocalSeatCount"/>), <c>population.active</c>,
/// <c>state.&lt;row&gt;</c>, and <c>state.&lt;row&gt;.&lt;key&gt;</c> (see <see cref="HudBindingKind.StateNamed"/>).
/// A token outside this set refuses by name — the same parse both <see cref="WorldDefinitionValidator"/> (load-time)
/// and the render-side resolver (frame-time) call, so a document can never carry a binding the renderer would
/// silently treat as unbound.
/// </summary>
public static class HudBindingVocabulary {
    private const string PopulationActiveToken = "population.active";
    private const string PositionXSuffix = ".position.x";
    private const string PositionYSuffix = ".position.y";
    private const string PositionZSuffix = ".position.z";
    private const string SeatPrefix = "seat.";
    private const string StatePrefix = "state.";
    private const string WorldFpsToken = "world.fps";
    private const string WorldTickToken = "world.tick";

    /// <summary>Determines whether <paramref name="token"/> is a recognized binding — the validator's refuse-unknown-by-name check.</summary>
    /// <param name="token">The token.</param>
    /// <returns><see langword="true"/> when recognized.</returns>
    public static bool IsKnown(string? token) => TryParse(
        binding: out _,
        token: token
    );
    /// <summary>Parses a binding token against the closed vocabulary.</summary>
    /// <param name="token">The token (e.g. <c>world.tick</c>, <c>seat.1.position.x</c>, <c>state.row</c>,
    /// <c>state.row.key</c>).</param>
    /// <param name="binding">The parsed binding, on success.</param>
    /// <returns><see langword="true"/> when the token is a recognized binding.</returns>
    public static bool TryParse(string? token, out HudBinding binding) {
        binding = default;

        if (string.IsNullOrEmpty(value: token)) {
            return false;
        }

        if (string.Equals(
            a: token,
            b: WorldTickToken,
            comparisonType: StringComparison.Ordinal
        )) {
            binding = new HudBinding(
                Kind: HudBindingKind.WorldTick,
                SeatIndex: 0
            );

            return true;
        }

        if (string.Equals(
            a: token,
            b: WorldFpsToken,
            comparisonType: StringComparison.Ordinal
        )) {
            binding = new HudBinding(
                Kind: HudBindingKind.WorldFps,
                SeatIndex: 0
            );

            return true;
        }

        if (string.Equals(
            a: token,
            b: PopulationActiveToken,
            comparisonType: StringComparison.Ordinal
        )) {
            binding = new HudBinding(
                Kind: HudBindingKind.PopulationActive,
                SeatIndex: 0
            );

            return true;
        }

        // state.<row> binds the row's own slot cell; state.<row>.<key> binds one named cell. Neither a row name nor a
        // cell name can hold a dot (WorldCellName refuses one at document parse), so the FIRST dot after the prefix is
        // always the grammar separator — a second dot in the remainder names no row/key pair this substrate could
        // ever hold, so it refuses rather than guessing which half is which.
        if (
            token.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: StatePrefix
        ) &&
            (token.Length > StatePrefix.Length)
        ) {
            var rest = token.AsSpan(start: StatePrefix.Length);
            var dot = rest.IndexOf(value: '.');

            if (dot < 0) {
                binding = new HudBinding(
                    Kind: HudBindingKind.StateNamed,
                    SeatIndex: 0,
                    StateName: rest.ToString()
                );

                return true;
            }

            var row = rest[..dot];
            var key = rest[(dot + 1)..];

            if (
                row.IsEmpty ||
                key.IsEmpty ||
                (key.IndexOf(value: '.') >= 0)
            ) {
                return false;
            }

            binding = new HudBinding(
                Kind: HudBindingKind.StateNamed,
                SeatIndex: 0,
                StateName: row.ToString(),
                StateCellKey: key.ToString()
            );

            return true;
        }

        if (!token.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: SeatPrefix
        )) {
            return false;
        }

        HudBindingKind kind;
        ReadOnlySpan<char> middle;

        if (token.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: PositionXSuffix
        )) {
            kind = HudBindingKind.SeatPositionX;
            middle = token.AsSpan(
                start: SeatPrefix.Length,
                length: ((token.Length - SeatPrefix.Length) - PositionXSuffix.Length)
            );
        } else if (token.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: PositionYSuffix
        )) {
            kind = HudBindingKind.SeatPositionY;
            middle = token.AsSpan(
                start: SeatPrefix.Length,
                length: ((token.Length - SeatPrefix.Length) - PositionYSuffix.Length)
            );
        } else if (token.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: PositionZSuffix
        )) {
            kind = HudBindingKind.SeatPositionZ;
            middle = token.AsSpan(
                start: SeatPrefix.Length,
                length: ((token.Length - SeatPrefix.Length) - PositionZSuffix.Length)
            );
        } else {
            return false;
        }

        if (middle.Length == 0) {
            return false;
        }

        if (!int.TryParse(
            s: middle,
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out var seatIndex
        )) {
            return false;
        }

        if (
            (seatIndex < 1) ||
            (seatIndex > WorldPopulationLimits.LocalSeatCount)
        ) {
            return false;
        }

        binding = new HudBinding(
            Kind: kind,
            SeatIndex: seatIndex
        );

        return true;
    }
}
/// <summary>One parsed run of a <see cref="WorldHudElement.Template"/> string — either a literal text run drawn
/// verbatim, or a placeholder naming one <see cref="HudBindingVocabulary"/> token whose live value replaces it (see
/// <see cref="HudTemplate"/>).</summary>
/// <param name="IsPlaceholder"><see langword="true"/> for a placeholder run; <see langword="false"/> for literal
/// text.</param>
/// <param name="Text">The literal text (when <paramref name="IsPlaceholder"/> is <see langword="false"/>) or the
/// raw placeholder token, brace-delimiters stripped (when <see langword="true"/>) — not yet validated against
/// <see cref="HudBindingVocabulary"/>.</param>
public readonly record struct HudTemplateSegment(bool IsPlaceholder, string Text);
/// <summary>
/// The brace/escape grammar a <see cref="WorldHudElement.Template"/> string speaks, and the one place it is parsed:
/// <c>{token}</c> interpolates one <see cref="HudBindingVocabulary"/> token, resolved through the same operand path
/// a plain <see cref="WorldHudElement.Binding"/> uses; <c>{{</c> and <c>}}</c> escape a literal brace (the
/// <c>string.Format</c>/interpolated-string escaping convention, not a bespoke one). A lone unescaped <c>{</c> with
/// no matching <c>}</c>, an empty <c>{}</c>, or a lone unescaped <c>}</c> is malformed and refused by name rather
/// than guessed at.
/// <para>This is the only parse of the grammar anywhere: document validation calls it
/// (<see cref="WorldDefinitionValidator"/>, which additionally resolves each placeholder against
/// <see cref="HudBindingVocabulary"/> and, for a <c>state.*</c> token, the document's own <c>state</c> section);
/// the <c>world.hud.template</c> console verb calls it against the live document; and
/// <c>Puck.World.WorldHudFeed</c> calls it on the structure rebuild, handing <c>Puck.Overlays</c> the parsed runs.
/// The render path itself never parses a template — <c>Puck.Overlays.HudWriter</c> cannot reference this
/// project.</para>
/// </summary>
public static class HudTemplate {
    /// <summary>Wraps <see cref="TryParse"/> for a caller that only needs the placeholder tokens (document
    /// validation does not need the literal runs back).</summary>
    /// <param name="template">The template text.</param>
    /// <param name="placeholders">Every placeholder token, in left-to-right order, on success.</param>
    /// <param name="error">Why the template was malformed, or empty on success.</param>
    /// <returns><see langword="true"/> when the brace/escape grammar is well formed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is <see langword="null"/>.</exception>
    public static bool TryEnumeratePlaceholders(string template, out IReadOnlyList<string> placeholders, out string error) {
        if (!TryParse(
            error: out error,
            segments: out var segments,
            template: template
        )) {
            placeholders = [];

            return false;
        }

        var tokens = new List<string>(capacity: segments.Count);

        foreach (var segment in segments) {
            if (segment.IsPlaceholder) {
                tokens.Add(item: segment.Text);
            }
        }

        placeholders = tokens;

        return true;
    }
    /// <summary>Parses a template into its literal/placeholder run sequence.</summary>
    /// <param name="template">The template text.</param>
    /// <param name="segments">The parsed runs, in left-to-right document order, on success.</param>
    /// <param name="error">Why the template was malformed, naming the offending position, or empty on success.</param>
    /// <returns><see langword="true"/> when the brace/escape grammar is well formed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is <see langword="null"/>.</exception>
    public static bool TryParse(string template, out IReadOnlyList<HudTemplateSegment> segments, out string error) {
        ArgumentNullException.ThrowIfNull(argument: template);

        var found = new List<HudTemplateSegment>();
        var literal = new System.Text.StringBuilder();
        var index = 0;

        while (index < template.Length) {
            var current = template[index];

            if (current == '{') {
                if (
                    ((index + 1) < template.Length) &&
                    (template[(index + 1)] == '{')
                ) {
                    literal.Append(value: '{');
                    index += 2;

                    continue;
                }

                var close = template.IndexOf(
                    startIndex: (index + 1),
                    value: '}'
                );

                if (close < 0) {
                    segments = [];
                    error = $"carries an unterminated '{{' at position {index} with no matching '}}'";

                    return false;
                }

                var token = template[(index + 1)..close];

                if (token.Length == 0) {
                    segments = [];
                    error = $"carries an empty placeholder '{{}}' at position {index} — a placeholder must name a binding token";

                    return false;
                }

                if (literal.Length > 0) {
                    found.Add(item: new HudTemplateSegment(
                        IsPlaceholder: false,
                        Text: literal.ToString()
                    ));
                    literal.Clear();
                }

                found.Add(item: new HudTemplateSegment(
                    IsPlaceholder: true,
                    Text: token
                ));
                index = (close + 1);

                continue;
            }

            if (current == '}') {
                if (
                    ((index + 1) < template.Length) &&
                    (template[(index + 1)] == '}')
                ) {
                    literal.Append(value: '}');
                    index += 2;

                    continue;
                }

                segments = [];
                error = $"carries an unescaped '}}' at position {index} matching no open '{{' — use '}}}}' for a literal brace";

                return false;
            }

            literal.Append(value: current);
            index++;
        }

        if (literal.Length > 0) {
            found.Add(item: new HudTemplateSegment(
                IsPlaceholder: false,
                Text: literal.ToString()
            ));
        }

        segments = found;
        error = string.Empty;

        return true;
    }
}
