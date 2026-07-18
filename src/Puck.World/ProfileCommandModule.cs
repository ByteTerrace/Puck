using System.Globalization;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using static Puck.Commands.CommandArgs;

namespace Puck.World;

/// <summary>
/// The profile console surface — the real-time settings verbs. <c>profile.list</c> reads the catalog (marking which
/// profiles are in use and by whom); <c>profile.create</c> adds one and persists it; <c>profile.show</c> reports a
/// player's profile; <c>profile.set</c> mutates a player's profile motion live (move speed, turn speed, look-invert) and
/// persists it. <c>profile.set</c> is TYPED SUGAR over the durable player-document protocol — it composes the seat's
/// whole <c>motion</c> section with one field changed and submits it as a <c>SetPlayerSection(motion)</c> over the
/// <see cref="IServerLink"/> (the same wire the raw <c>profile.section</c> reflection and a future editor drive), so
/// there is ONE validate-and-persist path, not a second one here. Simulation-affecting profile edits land on the next
/// tick snapshot.
/// </summary>
internal sealed class ProfileCommandModule(WorldProfiles profiles, PlayerRoster roster, IServerLink link) : ICommandModule {
    private readonly WorldProfiles m_profiles = profiles;
    private readonly PlayerRoster m_roster = roster;
    private readonly IServerLink m_link = link;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            name: "profile.list",
            description: "Lists every stored profile — name, color, and its settings — marking which are in use and by which player. The catalog persists locally (cloud-ready behind the same storage seam).",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: DescribeCatalog())
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "profile.create",
            description: "Creates a profile and persists it: profile.create <name> [#RRGGBB]. Without a color it takes the next distinct golden-ratio hue (skipping any already in the catalog). The name must be unique (case-insensitive).",
            handler: CreateHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "profile.show",
            description: "Shows a player's profile — name, color, and every setting: profile.show [n] (optional player index 1..4, default 1).",
            handler: ShowHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "profile.set",
            description: "Sets a player's profile setting LIVE and persists it: profile.set <key> <value> [n] — key is speed (0.5..20), turn-speed (0.5..10), or invert-look (on/off); the optional player index is 1..4 (default 1). Echoes old → new. The change lands on the next frame with no restart.",
            handler: SetHandler,
            routing: CommandRouting.Simulation
        );
    }

    private CommandResult CreateHandler(CommandContext context, string[] args) {
        if (args.Length is not (1 or 2)) {
            return new CommandResult(Output: "[profile.create: expected a name plus an optional #RRGGBB color — profile.create <name> [#hex]]");
        }

        var colorHex = ((args.Length == 2) ? args[1] : NextDistinctColor());

        if (m_profiles.Create(name: args[0], colorHex: colorHex) is not { } profile) {
            return new CommandResult(Output: $"[profile.create: a profile named '{args[0]}' already exists]");
        }

        return new CommandResult(Output: $"[profile.create: '{profile.Name}' {profile.ColorHex}] {DescribeCatalog()}");
    }
    private CommandResult ShowHandler(CommandContext context, string[] args) {
        if (args.Length > 1) {
            return new CommandResult(Output: "[profile.show: expected at most 1 value — an optional player index]");
        }

        if (!TryResolveIndex(args: args, at: 0, index: out var index)) {
            return new CommandResult(Output: $"[profile.show: player index must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        if (m_roster.ProfileAt(slot: PlayerRoster.SlotFromDisplay(number: index)) is not { } profile) {
            return new CommandResult(Output: $"[profile.show: player {index} is not joined — see world.players]");
        }

        return new CommandResult(Output: $"[profile.show: player {index} — {DescribeProfile(profile: profile)}]");
    }
    private CommandResult SetHandler(CommandContext context, string[] args) {
        if (args.Length is not (2 or 3)) {
            return new CommandResult(Output: "[profile.set: expected <key> <value> plus an optional player index — key is speed, turn-speed, or invert-look]");
        }

        if (!TryResolveIndex(args: args, at: 2, index: out var index)) {
            return new CommandResult(Output: $"[profile.set: player index must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        if (m_roster.ProfileAt(slot: PlayerRoster.SlotFromDisplay(number: index)) is not { } profile) {
            return new CommandResult(Output: $"[profile.set: player {index} is not joined — see world.players]");
        }

        var key = args[0];
        var value = args[1];
        var slot = PlayerRoster.SlotFromDisplay(number: index);

        return (key.ToLowerInvariant() switch {
            "speed" => SetFloat(profile: profile, slot: slot, key: "speed", raw: value, min: 0.5f, max: 20f, read: static p => p.MoveSpeed, build: static (p, v) => MotionOf(profile: p) with { MoveSpeed = v }, index: index),
            "turn-speed" => SetFloat(profile: profile, slot: slot, key: "turn-speed", raw: value, min: 0.5f, max: 10f, read: static p => p.TurnSpeed, build: static (p, v) => MotionOf(profile: p) with { TurnSpeed = v }, index: index),
            "invert-look" => SetInvert(profile: profile, slot: slot, raw: value, index: index),
            _ => new CommandResult(Output: $"[profile.set: unknown key '{key}' — expected speed, turn-speed, or invert-look]"),
        });
    }
    private CommandResult SetFloat(WorldProfile profile, int slot, string key, string raw, float min, float max, Func<WorldProfile, float> read, Func<WorldProfile, float, WorldPlayerMotion> build, int index) {
        if (!TryParseFloat(text: raw, value: out var parsed)) {
            return new CommandResult(Output: $"[profile.set: could not parse '{raw}' as a number]");
        }

        var clamped = Math.Clamp(value: parsed, min: min, max: max);
        var old = read(arg: profile);

        if (!SubmitMotion(slot: slot, profile: profile, motion: build(arg1: profile, arg2: clamped), reason: out var reason)) {
            return new CommandResult(Output: $"[profile.set: {reason}]") {
                IsError = true,
            };
        }

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[profile.set: {profile.Name} {key} {old:0.##} → {clamped:0.##} (player {index})]"));
    }
    private CommandResult SetInvert(WorldProfile profile, int slot, string raw, int index) {
        bool on;

        if (string.Equals(a: raw, b: "on", comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: raw, b: "true", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            on = true;
        } else if (string.Equals(a: raw, b: "off", comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: raw, b: "false", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            on = false;
        } else {
            return new CommandResult(Output: $"[profile.set: invert-look expects on/off, got '{raw}']");
        }

        var old = profile.InvertLookX;

        if (!SubmitMotion(slot: slot, profile: profile, motion: (MotionOf(profile: profile) with { InvertLookX = on }), reason: out var reason)) {
            return new CommandResult(Output: $"[profile.set: {reason}]") {
                IsError = true,
            };
        }

        return new CommandResult(Output: $"[profile.set: {profile.Name} invert-look {(old ? "on" : "off")} → {(on ? "on" : "off")} (player {index})]");
    }

    // The seat's whole current motion section (the SetPlayerSection grain is one whole section), so profile.set patches
    // one field and submits the rest unchanged.
    private static WorldPlayerMotion MotionOf(WorldProfile profile) {
        return new WorldPlayerMotion(MoveSpeed: profile.MoveSpeed, TurnSpeed: profile.TurnSpeed, InvertLookX: profile.InvertLookX);
    }

    // Submit the seat's whole motion section over the durable protocol (the server validates, updates the shared handle,
    // and persists — the ONE persist path). Loopback applies synchronously at submit, so on return the live handle
    // already carries the change and the echo reads truthfully.
    private bool SubmitMotion(int slot, WorldProfile profile, WorldPlayerMotion motion, out string reason) {
        var reply = m_link.SubmitSession(request: new SessionRequest.SetPlayerSection(
            Principal: WorldPrincipal.Seat(slot: slot),
            ProfileId: profile.Id,
            Section: WorldPlayerSection.Motion,
            Payload: WorldPlayerJson.SerializeMotion(motion: motion)
        ));

        reason = reply.Reason;

        return reply.Accepted;
    }

    // The catalog line for profile.list / an echo tail: each profile with its color, settings, and (if active) owner.
    private string DescribeCatalog() {
        var segments = m_profiles.All.Select(selector: profile => {
            var owner = m_roster.ActiveSlotUsing(profile: profile);
            var use = ((owner >= 0) ? $" in-use(p{PlayerRoster.DisplayNumber(slot: owner)})" : string.Empty);

            return $"{DescribeProfile(profile: profile)}{use}";
        });

        return $"[profile.list: {string.Join(separator: " | ", values: segments)}]";
    }

    // The next auto-color for a color-less profile.create: golden-ratio hue stepping seeded off the catalog count,
    // skipping any hex already present so a fifth profile never silently reuses an earlier hue.
    private string NextDistinctColor() {
        var existing = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var profile in m_profiles.All) {
            _ = existing.Add(item: profile.ColorHex);
        }

        var count = m_profiles.All.Count;

        // Bounded scan over the shared golden-ratio walk (WorldColor.IndexColorHex), seeded off the catalog count so a
        // fresh color continues the sequence. The cap only guards the all-colors-taken case, where any hue will do.
        for (var attempt = 0; (attempt < 64); attempt++) {
            var hex = WorldColor.IndexColorHex(index: (count + attempt));

            if (!existing.Contains(item: hex)) {
                return hex;
            }
        }

        return WorldColor.IndexColorHex(index: (count + 64));
    }
    private static string DescribeProfile(WorldProfile profile) {
        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{profile.Name} {profile.ColorHex} speed={profile.MoveSpeed:0.##} turn={profile.TurnSpeed:0.##} invert-look={(profile.InvertLookX ? "on" : "off")}"
        );
    }

    // Resolve an optional trailing player index at args[at] (default player 1) through the shared world-index parser —
    // false only on a present-but-malformed or out-of-range index; an absent index yields player 1.
    private static bool TryResolveIndex(string[] args, int at, out int index) =>
        WorldArgs.TryParseIndex(args: args, at: at, min: 1, max: PlayerRoster.MaxSlots, fallback: 1, value: out index);
}
