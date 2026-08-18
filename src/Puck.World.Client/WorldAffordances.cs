using Puck.Commands;
using Puck.Input;
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
/// <para>Native binding source kinds resolve through <see cref="InputSourceVocabulary"/>'s full value range. This
/// door feeds <see cref="InputRouter"/> directly, whose <see cref="CommandValue"/> carries Axis3D and Orientation;
/// it must not inherit the narrower two-lane payload limit of a scripted addon input act. A future/unknown source
/// still skips the kind half of the check, while a source explicitly marked unaddressable is refused.</para></remarks>
public static class WorldAffordances {
    private static volatile CommandRegistry? Registry;

    /// <summary>Whether the command vocabulary has been installed — validators skip the command half (never the
    /// structural half, never the channel half) while this is <see langword="false"/>.</summary>
    public static bool Installed => (Registry is not null);

    // The physical source's FULL declared kind, via the engine's one reflection-derived source catalog. Native
    // bindings ride CommandValue and therefore admit its whole range (including Axis3D and Orientation); only addon
    // input records apply AddonSourceVocabulary's narrower payload shape. Unknown and explicitly unaddressable
    // sources answer null — the kind check is skipped for them, while the latter is refused by sourceAddressable.
    private static CommandValueKind? SourceKind(string source) {
        if (
            InputSourceVocabulary.IsExplicitlyUnaddressable(sourceId: source) ||
            !InputSourceVocabulary.TryResolveDeclaredKind(
            kind: out var kind,
            sourceId: source
        )
        ) {
            return null;
        }

        return kind;
    }
    // The context-row admission half: built-in families and their states come from WorldContextFamilies; a
    // state:<row> family is structurally admitted here and resolved against the routed definition's state table at
    // the document/runtime WorldStateBindingContext gate. Empty members and null rows are the binding compiler's
    // findings, not this one's; every refusal here names the offending row.
    private static void ValidateContexts(BindingProfileDocument document, List<string> errors) {
        var rows = (document.Contexts ?? []);

        for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
            if (
                (rows[rowIndex] is not { } row) ||
                string.IsNullOrEmpty(value: row.Family) ||
                string.IsNullOrEmpty(value: row.State)
            ) {
                continue;
            }

            var states = WorldContextFamilies.StatesOf(family: row.Family);

            if ((states is null) && !WorldStateBindingContext.TryParseFamily(
                family: row.Family,
                rowName: out _
            )) {
                errors.Add(item: $"contexts row {rowIndex} names family \"{row.Family}\", which is not an admitted context family (admitted: {string.Join(
                    separator: ", ",
                    values: WorldContextFamilies.Families
                )}, {WorldStateBindingContext.FamilyPrefix}<row>)");
            } else if ((states is not null) && !states.Contains(
                value: row.State,
                comparer: StringComparer.Ordinal
            )) {
                errors.Add(item: $"contexts row {rowIndex} (family \"{row.Family}\") names state \"{row.State}\", which that family never publishes (states: {string.Join(
                    separator: ", ",
                    values: states
                )})");
            }
        }
    }

    /// <summary>Installs the command registry the vocabulary reads through. Called once by the composition root,
    /// after the container is built.</summary>
    /// <param name="registry">The live command registry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
    public static void Install(CommandRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        Registry = registry;
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

        var registry = Registry;

        return (
            (registry is null) ||
            registry.TryGetMetadata(
            metadata: out _,
            name: command
        )
        );
    }
    /// <summary>Runs the vocabulary check over <paramref name="document"/>, appending refusal lines to
    /// <paramref name="errors"/>. The command half is a no-op while no registry is installed; the channel half runs
    /// against <paramref name="channels"/> unconditionally, and structural context-family admission
    /// (<see cref="WorldContextFamilies"/> plus <c>state:&lt;row&gt;</c>) always runs.</summary>
    /// <param name="document">The binding document to check.</param>
    /// <param name="channels">The channel table THIS document is authored against — the declaring world's own
    /// compiled table, never another world's.</param>
    /// <param name="errors">The list refusal lines are appended to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channels"/> is <see langword="null"/>.</exception>
    public static void Validate(BindingProfileDocument document, WorldChannelTable channels, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: channels);
        ValidateContexts(
            document: document,
            errors: errors
        );

        // An absent registry withholds the COMMAND lookup and nothing else. Returning early here instead made the
        // channel half — a pure function of the document's own table, needing no registry at all — conditional on
        // whether a container happened to exist yet, so one document was refused as world.instance.start and admitted
        // as --world. Whether a document is valid must not depend on which door it walked through.
        var registry = Registry;

        BindingVocabularyCheck.Validate(
            command: ((registry is null)
            ? null
            : name => (registry.TryGetMetadata(
                    metadata: out var metadata,
                    name: name
                )
                ? metadata
                : null)),
            document: document,
            sourceKind: SourceKind,
            errors: errors,
            channel: reference => channels.TryGetOrdinal(
                ordinal: out _,
                reference: reference
            ),
            channelBinary: reference => (channels.TryGetOrdinal(
                ordinal: out var ordinal,
                reference: reference
            ) && (channels.Shape(ordinal: ordinal) == ChannelShape.Binary)),
            sourceAddressable: source => !InputSourceVocabulary.IsExplicitlyUnaddressable(sourceId: source)
        );
    }
}
