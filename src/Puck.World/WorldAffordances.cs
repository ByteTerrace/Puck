using Puck.Commands;
using Puck.Scripting;
using Puck.Scripting.Simulation;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The process's command vocabulary, installed once by the composition root after the <see cref="CommandRegistry"/>
/// exists and consulted by every surface a binding document enters through (the live rebind probe, the per-seat
/// recompose, the two document validators), feeding the shared <see cref="BindingVocabularyCheck"/> so a typo'd
/// command name is refused with the same words at every door instead of resolving to a silently dead key.
/// </summary>
/// <remarks><para>The command registry is ambient by necessity, not preference: the document validators are static
/// and also run where no registry can exist (the offline replay rehydrator, a pre-container boot parse), so it is a
/// lookup that is present once the composition root installs it and absent elsewhere. Absent means the command half
/// of validation is skipped — structural validation still runs — never that it passed; the composition root's
/// post-build sweep (<see cref="WorldSeatBindings.ValidateAffordancesLoudly"/>) re-covers the documents that entered
/// before install. It is also genuinely process-scoped: there is exactly one console, so every world instance in this
/// process dispatches through the same verb set.</para>
/// <para>The channel vocabulary is deliberately not ambient, and the asymmetry is the point. A channel table is a
/// pure function of the document that declares it, so it is a required parameter of <see cref="Validate"/>: a
/// document's binding overlays are always checked against that document's own channels. Holding it in a static
/// installed once from the boot world let a second world's document validate against the boot world's table, which
/// both refused a self-consistent document and — the direction that actually loses state — admitted a document
/// binding a channel it never declares, because some other world happened to declare it.</para>
/// <para>Source kinds resolve through <see cref="AddonSourceCatalog"/> (derived from <c>InputSources</c>' own
/// attributes); a source outside that catalog — a motion source, a future device — skips the kind half of the check
/// only.</para></remarks>
internal static class WorldAffordances {
    private static volatile CommandRegistry? s_registry;

    /// <summary>Whether the command vocabulary has been installed — validators skip the command half (never the
    /// structural half, never the channel half) while this is <see langword="false"/>.</summary>
    public static bool Installed => (s_registry is not null);

    /// <summary>Installs the command registry the vocabulary reads through. Called once by the composition root,
    /// after the container is built.</summary>
    /// <param name="registry">The live command registry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
    public static void Install(CommandRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        s_registry = registry;
    }

    /// <summary>Whether <paramref name="command"/> names a command (or alias) THIS composition's registry declares —
    /// the exact registration FACT <see cref="Validate"/>'s command half checks, exposed standalone so a caller can
    /// key a decision on registration itself (never on boot shape) instead of parsing a refusal string. Always
    /// <see langword="true"/> while no registry is installed (<see cref="Installed"/> is <see langword="false"/>) —
    /// the command half is skipped everywhere then, so nothing should read as unregistered yet.</summary>
    /// <param name="command">The command name (or alias) to look up.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public static bool IsCommandRegistered(string command) {
        ArgumentNullException.ThrowIfNull(argument: command);

        var registry = s_registry;

        return ((registry is null) || registry.TryGetMetadata(name: command, metadata: out _));
    }

    /// <summary>Runs the vocabulary check over <paramref name="document"/>, appending refusal lines to
    /// <paramref name="errors"/>. The command half is a no-op while no registry is installed; the channel half runs
    /// against <paramref name="channels"/> unconditionally, and the context-family admission half
    /// (<see cref="WorldContextFamilies"/>, an engine constant needing no runtime registry) always runs.</summary>
    /// <param name="document">The binding document to check.</param>
    /// <param name="channels">The channel table THIS document is authored against — the declaring world's own
    /// compiled table, never another world's.</param>
    /// <param name="errors">The list refusal lines are appended to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channels"/> is <see langword="null"/>.</exception>
    public static void Validate(BindingProfileDocument document, WorldChannelTable channels, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: channels);
        ValidateContexts(document: document, errors: errors);

        // An absent registry withholds the COMMAND lookup and nothing else. Returning early here instead made the
        // channel half — a pure function of the document's own table, needing no registry at all — conditional on
        // whether a container happened to exist yet, so one document was refused as world.instance.start and admitted
        // as --world. Whether a document is valid must not depend on which door it walked through.
        var registry = s_registry;

        BindingVocabularyCheck.Validate(
            command: ((registry is null) ? null : name => (registry.TryGetMetadata(name: name, metadata: out var metadata) ? metadata : null)),
            document: document,
            sourceKind: SourceKind,
            errors: errors,
            channel: reference => channels.TryGetOrdinal(reference: reference, ordinal: out _),
            channelBinary: reference => (channels.TryGetOrdinal(reference: reference, ordinal: out var ordinal) && (channels.Shape(ordinal: ordinal) == ChannelShape.Binary))
        );
    }

    // The context-row admission half: family and state must come from the engine's published registry
    // (WorldContextFamilies — a closed, compile-time vocabulary, so this check never waits on Install). Empty
    // members and null rows are the structural gate's findings (BindingProfile.Compile), not this one's; every
    // refusal here names the offending row.
    private static void ValidateContexts(BindingProfileDocument document, List<string> errors) {
        var rows = (document.Contexts ?? []);

        for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
            if ((rows[rowIndex] is not { } row) || string.IsNullOrEmpty(value: row.Family) || string.IsNullOrEmpty(value: row.State)) {
                continue;
            }

            if (WorldContextFamilies.StatesOf(family: row.Family) is not { } states) {
                errors.Add(item: $"contexts row {rowIndex} names family \"{row.Family}\", which is not an admitted context family (admitted: {string.Join(separator: ", ", values: WorldContextFamilies.Families)})");
            } else if (!states.Contains(value: row.State, comparer: StringComparer.Ordinal)) {
                errors.Add(item: $"contexts row {rowIndex} (family \"{row.Family}\") names state \"{row.State}\", which that family never publishes (states: {string.Join(separator: ", ", values: states)})");
            }
        }
    }

    // The physical source's declared kind, via the engine's one reflection-derived source catalog. Unknown and
    // catalog-unaddressable sources (motion sensors, text) answer null — the kind check is skipped for them, the
    // existence check never runs on sources at all (that is a different defect class than a dead command).
    private static CommandValueKind? SourceKind(string source) {
        if (!AddonSourceCatalog.TryResolve(sourceId: source, shape: out var shape)) {
            return null;
        }

        return shape switch {
            AddonSourceShape.Digital => CommandValueKind.Digital,
            AddonSourceShape.Axis1D => CommandValueKind.Axis1D,
            AddonSourceShape.Axis2D => CommandValueKind.Axis2D,
            _ => null,
        };
    }
}
