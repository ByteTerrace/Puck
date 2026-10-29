using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldBindingCommandModule {
    private CommandResult BarPreferenceHandler(CommandContext context, WireArgs args) {
        const string Verb = "player.binding-bar";
        if (args.Count is < 2 or > 3) {
            return CommandResult.Error(output: $"[{Verb}: expected layout|model|scale <value> [seat]]");
        }
        var (slot, error) = SeatCommandArgs.ResolveJoinedSeat(m_roster, context, in args, 2, Verb);
        if (error is { } refusal) {
            return refusal;
        }
        var session = m_seatBindings.SessionRebind(slot) ?? new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion, Modifiers: [], Chords: []);
        var preferences = session.BindingBar ?? new BindingBarPreferences();
        var value = args[1].ToString();
        var clear = value == "auto";
        if (args.Is(0, "layout")) {
            m_seatBindings.GetRoutedState(slot, out var definition, out _);
            var authored = WorldBindingBarAuthoring.Resolve(m_roster.ProfileAt(slot)?.Document, definition, out _);
            if (!clear && (authored.Layouts is null || !authored.Layouts.ContainsKey(value))) {
                return CommandResult.Error(output: $"[{Verb}: unknown authored layout '{value}']");
            }
            preferences = preferences with { Layout = clear ? null : value };
        } else if (args.Is(0, "model")) {
            preferences = preferences with { Model = clear ? null : value };
        } else if (args.Is(0, "scale")) {
            float? scale = null;
            if (!clear) {
                if (!float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || !float.IsFinite(parsed) || parsed <= 0) {
                    return CommandResult.Error(output: $"[{Verb}: scale must be finite and positive]");
                }
                scale = parsed;
            }
            preferences = preferences with { Scale = scale };
        } else {
            return CommandResult.Error(output: $"[{Verb}: expected layout, model or scale]");
        }
        m_seatBindings.SetSessionRebind(slot, session with { BindingBar = preferences });
        return SeatCommandArgs.Echo(slot, Verb, $"{args[0].ToString()}={value}");
    }
}
