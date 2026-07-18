using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The runtime handle for a catalog profile: a mutable in-memory view of one <see cref="WorldPlayerProfile"/> that
/// participants reference by identity. The stable <see cref="Id"/> is immutable; the display <see cref="Name"/>/
/// <see cref="ColorHex"/> identity (through <see cref="SetIdentity"/>), the three motion settings, the optional
/// <see cref="Bindings"/> section, and the <see cref="Preferences"/> bag are all mutable, so a live edit (a
/// <c>profile.set</c> verb, a folded <c>profile.save</c>, a <c>SetPlayerSection</c> identity/motion/preferences write)
/// changes them and every participant sharing the handle is retuned together. The color is (re)parsed only when the
/// identity is set, so the hot render/advance path never re-parses hex — a seated participant reads the live
/// <see cref="Color"/> off this handle (<c>PlayerRoster.BodyColor</c>), so refreshing the handle refreshes the avatar.
/// </summary>
internal sealed class WorldProfile {
    /// <summary>The body-to-nose color factor: the snout is the same hue, darkened, so the facing reads legibly.</summary>
    public const float NoseFactor = 0.35f;

    /// <summary>Initializes a new instance of the <see cref="WorldProfile"/> class from a stored catalog entry.</summary>
    /// <param name="profile">The stored profile to view.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    public WorldProfile(WorldPlayerProfile profile) {
        ArgumentNullException.ThrowIfNull(argument: profile);

        Bindings = profile.Bindings;
        Color = ParseColor(hex: profile.Identity.Color);
        ColorHex = profile.Identity.Color;
        Id = profile.Id;
        InvertLookX = profile.Motion.InvertLookX;
        MoveSpeed = profile.Motion.MoveSpeed;
        Name = profile.Identity.Name;
        Preferences = profile.Extensions;
        TurnSpeed = profile.Motion.TurnSpeed;
    }

    /// <summary>The stable profile id — the edit/mutation address, immutable identity (distinct from the display name).</summary>
    public string Id { get; }
    /// <summary>The unique (case-insensitive) profile name — the participant's identity on screen and over the pipe.
    /// Set live through <see cref="SetIdentity"/> (a durable identity edit), so every reader of the handle updates.</summary>
    public string Name { get; private set; }
    /// <summary>The body color as the stored <c>#RRGGBB</c> hex string (persisted verbatim). Set live through
    /// <see cref="SetIdentity"/>.</summary>
    public string ColorHex { get; private set; }
    /// <summary>The body color parsed to linear-ish RGB in <c>[0, 1]</c> — (re)parsed only when the identity is set, so
    /// the hot path never re-parses hex. A seated participant renders this live off the handle.</summary>
    public Vector3 Color { get; private set; }
    /// <summary>The derived nose color — the body hue darkened by <see cref="NoseFactor"/>.</summary>
    public Vector3 NoseColor => WorldColor.Nose(body: Color);
    /// <summary>Locomotion speed in world units per second (mutable — <c>profile.set speed</c> changes it live).</summary>
    public float MoveSpeed {
        get => (float)(double)m_moveSpeed;
        set => m_moveSpeed = FixedQ4816.FromDouble(value: value);
    }
    /// <summary>The deterministic locomotion rate consumed by the simulation.</summary>
    public FixedQ4816 FixedMoveSpeed => m_moveSpeed;
    /// <summary>Turn speed in radians per second (mutable — <c>profile.set turn-speed</c> changes it live).</summary>
    public float TurnSpeed {
        get => (float)(double)m_turnSpeed;
        set => m_turnSpeed = FixedQ4816.FromDouble(value: value);
    }
    /// <summary>The deterministic angular rate consumed by the simulation.</summary>
    public FixedQ4816 FixedTurnSpeed => m_turnSpeed;
    /// <summary>Whether the look-stick X axis is inverted at consumption (mutable — <c>profile.set invert-look</c>).</summary>
    public bool InvertLookX { get; set; }
    /// <summary>The profile's binding overrides layered beneath live session rebinds (<see langword="null"/> = inherit
    /// the engine default). Set by a folded <c>profile.save</c> through the server's player-document edit path.</summary>
    public BindingProfileDocument? Bindings { get; set; }
    /// <summary>The open preferences bag carried verbatim across a round-trip (<see langword="null"/> when empty).</summary>
    public IDictionary<string, JsonElement>? Preferences { get; set; }

    private FixedQ4816 m_moveSpeed;
    private FixedQ4816 m_turnSpeed;

    /// <summary>Sets the display identity LIVE (a durable <c>SetPlayerSection(identity)</c> edit): updates the
    /// <see cref="Name"/> and <see cref="ColorHex"/> and re-parses <see cref="Color"/> once. Every reader of the shared
    /// handle — a seated participant's rendered color (<c>PlayerRoster.BodyColor</c>), <c>profile.show</c>,
    /// <c>world.players</c> — picks up the change, so no stale cached identity survives the edit.</summary>
    /// <param name="name">The new display name.</param>
    /// <param name="colorHex">The new <c>#RRGGBB</c> hex color.</param>
    public void SetIdentity(string name, string colorHex) {
        Name = name;
        ColorHex = colorHex;
        Color = ParseColor(hex: colorHex);
    }

    /// <summary>Snapshots this runtime handle back into a serializable catalog entry (the persistence path).</summary>
    /// <returns>An entry carrying the handle's current id, identity, motion, bindings, and preferences.</returns>
    public WorldPlayerProfile ToProfile() {
        return new WorldPlayerProfile(
            Id: Id,
            Identity: new WorldPlayerIdentity(Name: Name, Color: ColorHex),
            Motion: new WorldPlayerMotion(MoveSpeed: MoveSpeed, TurnSpeed: TurnSpeed, InvertLookX: InvertLookX),
            Bindings: Bindings
        ) {
            Extensions = Preferences,
        };
    }

    /// <summary>Parses a <c>#RRGGBB</c> (or bare <c>RRGGBB</c>) hex color to a <see cref="Vector3"/> in <c>[0, 1]</c>.
    /// A malformed value falls back to a neutral gray rather than throwing, so a hand-edited typo never blacks out an
    /// avatar.</summary>
    /// <param name="hex">The hex color string.</param>
    /// <returns>The parsed color, or a neutral gray when the string is malformed.</returns>
    public static Vector3 ParseColor(string hex) {
        var span = (hex ?? string.Empty).AsSpan().Trim();

        if ((span.Length > 0) && (span[0] == '#')) {
            span = span[1..];
        }

        if ((span.Length == 6) &&
            byte.TryParse(s: span[..2], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out var r) &&
            byte.TryParse(s: span[2..4], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out var g) &&
            byte.TryParse(s: span[4..6], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out var b)) {
            return new Vector3(x: (r / 255f), y: (g / 255f), z: (b / 255f));
        }

        return new Vector3(value: 0.549f);
    }
}
