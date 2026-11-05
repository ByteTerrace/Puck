using Puck.Commands;

namespace Puck.World;

public sealed partial class WorldBindingCommandModule {
    private CommandResult BarPreferenceHandler(CommandContext context, WireArgs args) {
        const string Verb = "player.binding-bar";

        if (args.Count is < 2 or > 3) {
            return CommandResult.Usage(form: BindingBarForm, verb: Verb);
        }
        var (slot, error) = SeatCommandArgs.ResolveJoinedSeat(m_roster, context, in args, 2, Verb);
        if (error is { } refusal) {
            return refusal;
        }
        var session = (m_seatBindings.SessionRebind(slot: slot) ?? new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion, Modifiers: [], Chords: []));
        var preferences = (session.BindingBar ?? new BindingBarPreferences());
        var value = args[1].ToString();
        var clear = (value == "auto");

        if (args.Is(index: 0, value: "layout")) {
            m_seatBindings.GetRoutedState(definition: out var definition, slot: slot, state: out _);
            var authored = WorldBindingBarAuthoring.Resolve(m_roster.ProfileAt(slot: slot)?.Document, definition, out _);

            if (!clear && ((authored.Layouts is null) || !authored.Layouts.ContainsKey(key: value))) {
                return CommandResult.Error(output: $"[{Verb}: unknown authored layout '{value}']");
            }
            preferences = preferences with { Layout = (clear ? null : value) };
        } else if (args.Is(index: 0, value: "model")) {
            preferences = preferences with { Model = (clear ? null : value) };
        } else if (args.Is(index: 0, value: "scale")) {
            float? scale = null;

            if (!clear) {
                if (!float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || !float.IsFinite(f: parsed) || (parsed <= 0)) {
                    return CommandResult.Error(output: $"[{Verb}: scale must be finite and positive]");
                }
                scale = parsed;
            }
            preferences = preferences with { Scale = scale };
        } else {
            return CommandResult.Usage(form: BindingBarForm, got: args[0].ToString(), verb: Verb);
        }
        m_seatBindings.SetSessionRebind(slot, session with { BindingBar = preferences });
        return SeatCommandArgs.Echo(slot, Verb, $"{args[0].ToString()}={value}");
    }
}
