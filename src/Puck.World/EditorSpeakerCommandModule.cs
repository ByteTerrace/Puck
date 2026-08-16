using System.Globalization;
using Puck.Commands;
using Puck.World.Client;
using static Puck.World.WorldCommandDefinition;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The speaker authoring numeric twin — <c>editor.speaker.place</c>, a name-addressed whole-row
/// <see cref="WorldMutation.UpsertSpeaker"/> act beside the selection-driven channel (speakers also select, drag,
/// move, and delete through the selection/drag machinery). Console-only: every chord slot on the editor place page
/// is already spoken for (grab/stamp/cancel/snap on the face diamond, spawn ghosts + creation cycling on the
/// D-pad), so it rides the console/binding-data seam rather than evicting an existing act. Field edits after
/// placement (move/gain/channel/radius) go through the document speaker-row verb, never a twin here. A separate
/// module for the analyzer ceilings.
/// </summary>
/// <remarks><c>editor.speaker.place</c> routes <see cref="CommandRouting.Simulation"/> (the stdin barrier serializes
/// a following <c>world.speakers</c>/<c>speaker.state</c> read-after-write). It carries the acting seat principal and
/// requires the seat in editor mode (the focus point places, and grant denials land on the seat that asked). Caution:
/// the focus point rides the drifting avatar pose — never treat it as a shared prerequisite between two boots.</remarks>
internal sealed class EditorSpeakerCommandModule(WorldEditorSession session, IServerLink link) : ICommandModule {
    private readonly WorldEditorSession m_session = session;
    private readonly IServerLink m_link = link;

    private static WorldSpeakerSource? ParseSource(ReadOnlySpan<char> token) {
        if (token.Equals(
            comparisonType: StringComparison.Ordinal,
            other: "none"
        )) {
            return new WorldSpeakerSource.None();
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "machine:"
        ) &&
            int.TryParse(
            s: token[("machine:".Length)..],
            provider: CultureInfo.InvariantCulture,
            result: out var screenIndex
        )
        ) {
            return new WorldSpeakerSource.Machine(ScreenIndex: screenIndex);
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "tune:"
        ) &&
            (token.Length > "tune:".Length)
        ) {
            return new WorldSpeakerSource.Tune(TuneId: token[("tune:".Length)..].ToString());
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "synth:"
        ) &&
            (token.Length > "synth:".Length)
        ) {
            return new WorldSpeakerSource.Synth(PatchId: token[("synth:".Length)..].ToString());
        }

        return null;
    }
    private CommandResult PlaceHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 2 or > 4)) {
            return CommandResult.Error(output: "[editor.speaker.place: expected <name> <none|machine:<slot>|tune:<id>|synth:<patchId>> [radius] [seat]]");
        }

        // The optional third token is a radius when numeric; the seat then rides fourth.
        var hasRadius = ((args.Count >= 3) && float.TryParse(
            s: args[2],
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out _
        ));

        var (slot, slotError) = EditorCommandModule.ResolveSlot(
            args: args,
            at: (hasRadius
            ? 3
            : 2),
            context: context,
            verb: "editor.speaker.place"
        );

        if (slotError is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(
            slot: slot,
            verb: "editor.speaker.place"
        ) is { } guard) {
            return guard;
        }

        if (ParseSource(token: args[1]) is not { } source) {
            return CommandResult.Error(output: $"[editor.speaker.place: unknown source '{args[1].ToString()}' — none|machine:<slot>|tune:<id>|synth:<patchId>]");
        }

        WorldSpeakerAttenuation? attenuation = null;

        if (hasRadius) {
            if (
                !float.TryParse(
                s: args[2],
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out var radius
            ) ||
                !float.IsFinite(f: radius) ||
                (radius <= 0f)
            ) {
                return CommandResult.Error(output: $"[editor.speaker.place: bad radius '{args[2].ToString()}']");
            }

            attenuation = new WorldSpeakerAttenuation(
                Curve: null,
                Radius: radius
            );
        }

        var focus = m_session.Focus(slot: slot);
        var speaker = new WorldSpeaker.Fixed(
            Name: args[0].ToString(),
            Position: focus,
            Feed: new WorldSpeakerFeed(
                Channel: WorldSpeakerFeed.ChannelMix,
                Gain: 1f,
                Source: source
            ),
            Attenuation: attenuation
        );

        // The acting principal is the one this dispatch's ingress door stamped (see WorldPrincipalMapping) — a chord
        // act carries the pressing seat's own claim, a typed line carries Console; the handler never re-derives it.
        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSpeaker(
            Principal: context.ActingPrincipal(),
            Speaker: speaker
        ));

        return EditorSculptCommandModule.Echo(
            slot: slot,
            verb: "editor.speaker.place",
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"speaker '{speaker.Name}' at ({focus.X:0.00}, {focus.Y:0.00}, {focus.Z:0.00}) — one mutation submitted"
            )
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Simulation(
            description: "Places a NEW fixed speaker at the editor focus point as one mutation: editor.speaker.place <name> <none|machine:<slot>|tune:<id>|synth:<patchId>> [radius] [seat]. The feed boots mix-channel at unity gain; radius sets an explicit attenuation (else the audio defaults coalesce). Tune/patch/screen references validate at apply. Field edits after placement (move/gain/channel/radius) go through the document speaker-row verb, not a twin here.",
            handler: PlaceHandler,
            name: "editor.speaker.place"
        );
    }

}
