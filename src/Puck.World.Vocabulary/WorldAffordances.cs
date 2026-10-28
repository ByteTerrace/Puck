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
/// lookup that is present once the composition root installs it and absent elsewhere. Where none is installed the
/// command half judges by the generated <see cref="WorldConsoleVerbCatalog"/>, every verb any boot shape registers
/// (<c>puck compile --validate</c>), except in a host that sets <see cref="DeferUnknownVerbsToRegistry"/>, which skips
/// the half — never passes it — until its registry exists; the composition root's post-build sweep
/// (<c>WorldSeatBindings.ValidateAffordancesLoudly</c>) re-covers the documents that entered before install. With a
/// registry the same catalog answers for a verb only another shape registers, so a binding to a rendered-only verb
/// (<c>world.debug-view</c>) validates in a headless boot as it does offline, stays inert there and is dropped from the
/// seat's composition with a narrated line. It is also genuinely process-scoped: there is exactly one console, so every
/// world instance in this process dispatches through the same verb set.</para>
/// <para>The channel vocabulary is deliberately not ambient, and the asymmetry is the point. A channel table is a
/// pure function of the document that declares it, so it is a required parameter of <see cref="Validate"/>: a
/// document's binding overlays are always checked against that document's own channels. Holding it in a static
/// installed once from the boot world let a second world's document validate against the boot world's table, which
/// both refused a self-consistent document and — the direction that actually loses state — admitted a document
/// binding a channel it never declares, because some other world happened to declare it.</para>
/// <para>Native binding source kinds resolve through <see cref="InputSourceVocabulary"/>'s full value range. This
/// door feeds <see cref="InputRouter"/> directly, whose <see cref="CommandValue"/> carries Axis3D and Orientation;
/// it must not inherit the narrower two-lane payload limit of a scripted addon input act. A source this catalog
/// cannot resolve is REFUSED by name — it compiles to a control that will never signal, so the row is permanently
/// dead — and a source explicitly marked unaddressable is refused for that reason instead, never both.</para></remarks>
public static class WorldAffordances {
    private static volatile CommandRegistry? Registry;

    /// <summary>Gets or sets whether a catalog check before any registry is installed leaves a line calling a verb the
    /// catalog lacks to the registry: true in a host that re-validates its documents once its registry exists (the
    /// World), false where the catalog is the last word (<c>puck compile --validate</c>, the silo).</summary>
    public static bool DeferUnknownVerbsToRegistry { get; set; }
    /// <summary>Whether the command vocabulary has been installed — validators skip the command half (never the
    /// structural half, never the channel half) while this is <see langword="false"/>.</summary>
    public static bool Installed => (Registry is not null);

    private static WorldSeatModeFamily? FindSeatMode(IReadOnlyList<WorldSeatModeFamily> seatModes, string family) {
        foreach (var mode in (seatModes ?? [])) {
            if (string.Equals(
                a: mode.Name,
                b: family,
                comparisonType: StringComparison.Ordinal
            )) {
                return mode;
            }
        }

        return null;
    }
    // The physical source's FULL declared kind, via the engine's one reflection-derived source catalog. Native
    // bindings ride CommandValue and therefore admit its whole range (including Axis3D and Orientation); only addon
    // input records apply AddonSourceVocabulary's narrower payload shape. Unknown and explicitly unaddressable
    // sources both answer null, and null is itself a refusal: an unaddressable source is refused by name through
    // sourceAddressable, and a source nothing at all declares is refused as an unknown control.
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
    // the document/runtime WorldStateBindingContext gate; an AUTHORED seatModes family resolves against its own
    // declared states, the same as a built-in family. Empty members and null rows are the binding compiler's
    // findings, not this one's; every refusal here names the offending row.
    private static void ValidateContexts(BindingProfileDocument document, IReadOnlyList<WorldSeatModeFamily> seatModes, List<string> errors) {
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

            if (WorldContextFamilies.IsOpenStates(family: row.Family)) {
                continue;
            }
            if (states is null) {
                var mode = FindSeatMode(
                    seatModes: seatModes,
                    family: row.Family
                );

                if (mode is not null) {
                    if (!mode.States.Any(predicate: state => string.Equals(
                        a: state.Name,
                        b: row.State,
                        comparisonType: StringComparison.Ordinal
                    ))) {
                        errors.Add(item: $"contexts row {rowIndex} (family \"{row.Family}\") names state \"{row.State}\", which that family never publishes (states: {string.Join(
                            separator: ", ",
                            values: mode.States.Select(selector: state => state.Name)
                        )})");
                    }

                    continue;
                }
            }
            if (
                (states is null) &&
                !WorldStateBindingContext.TryParseFamily(
                family: row.Family,
                rowName: out _
            )
            ) {
                errors.Add(item: $"contexts row {rowIndex} names family \"{row.Family}\", which is not an admitted context family (admitted: {string.Join(
                    separator: ", ",
                    values: WorldContextFamilies.Families
                )}, {WorldStateBindingContext.FamilyPrefix}<row>)");
            } else if (
                (states is not null) &&
                !states.Contains(
                value: row.State,
                comparer: StringComparer.Ordinal
            )
            ) {
                errors.Add(item: $"contexts row {rowIndex} (family \"{row.Family}\") names state \"{row.State}\", which that family never publishes (states: {string.Join(
                    separator: ", ",
                    values: states
                )})");
            }
        }
    }
    // A document's own authored commands answer for it; with them supplied, the live world's authored names answer
    // nothing, so a document that drops a command is judged without it. A verb the registry lacks is judged by the
    // catalog's facts (every verb any boot shape registers), so the document validates the same in every shape.
    private static CommandMetadata? Lookup(AuthoredCommandSet? commands, string name, CommandRegistry? registry) {
        if (commands is not null) {
            if (commands.TryGet(command: out var authored, name: name)) {
                return CommandRegistry.AuthoredMetadata(name: authored!.Name);
            }

            if (registry?.NamesAuthoredCommand(name: name) ?? false) {
                return null;
            }
        }

        return ((registry is null)
            ? WorldConsoleVerbCatalog.Lookup(name: name)
            : registry.MetadataForDocument(name: name));
    }

    /// <summary>Runs the registry half of <c>commands</c> validation over a document's authored set against the
    /// installed registry, appending each refusal (<see cref="ValidateCommandsAgainst"/>).</summary>
    /// <param name="commands">The document's compiled authored commands.</param>
    /// <param name="errors">The list refusal lines are appended to.</param>
    public static void ValidateCommands(AuthoredCommandSet commands, List<string> errors) => ValidateCommandsAgainst(
        commands: commands,
        errors: errors,
        registry: Registry
    );
    /// <summary>Runs the registry half of <c>commands</c> validation over a document's authored set, appending each
    /// refusal: against <paramref name="registry"/> when there is one (<see cref="CommandRegistry.CheckAuthored"/>),
    /// otherwise against the generated <see cref="WorldConsoleVerbCatalog"/>, every verb any boot shape registers, which
    /// refuses a clash, an operator or unbindable line and a line calling a verb no shape registers.</summary>
    /// <param name="commands">The document's compiled authored commands.</param>
    /// <param name="registry">The live registry, or <see langword="null"/> where none exists.</param>
    /// <param name="errors">The list refusal lines are appended to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/> is <see langword="null"/>.</exception>
    public static void ValidateCommandsAgainst(AuthoredCommandSet commands, CommandRegistry? registry, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: commands);

        if (registry is not null) {
            registry.CheckAuthored(errors: errors, set: commands);

            return;
        }

        commands.CheckAgainst(
            errors: errors,
            refuseUnknown: !DeferUnknownVerbsToRegistry,
            registered: WorldConsoleVerbCatalog.Lookup
        );
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
    /// (<see cref="WorldContextFamilies"/> plus <c>state:&lt;row&gt;</c> plus <paramref name="seatModes"/>) always
    /// runs.</summary>
    /// <param name="document">The binding document to check.</param>
    /// <param name="channels">The channel table THIS document is authored against — the declaring world's own
    /// compiled table, never another world's.</param>
    /// <param name="seatModes">The AUTHORED per-seat mode families THIS document's world declares — the same
    /// per-document rule as <paramref name="channels"/>.</param>
    /// <param name="commands">The authored commands THIS document declares, which its rows may name in place of the
    /// live world's; <see langword="null"/> resolves an authored name against the live world's set.</param>
    /// <param name="errors">The list refusal lines are appended to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channels"/> is <see langword="null"/>.</exception>
    public static void Validate(BindingProfileDocument document, WorldChannelTable channels, IReadOnlyList<WorldSeatModeFamily> seatModes, AuthoredCommandSet? commands, List<string> errors) => ValidateAgainst(
        channels: channels,
        commands: commands,
        document: document,
        errors: errors,
        registry: Registry,
        seatModes: seatModes
    );
    /// <summary>Runs the vocabulary check over <paramref name="document"/> against <paramref name="registry"/> rather
    /// than the installed one (<see cref="Validate"/>).</summary>
    /// <param name="document">The binding document to check.</param>
    /// <param name="channels">The channel table THIS document is authored against.</param>
    /// <param name="seatModes">The authored per-seat mode families THIS document's world declares.</param>
    /// <param name="commands">The authored commands THIS document declares, or <see langword="null"/> for the
    /// registry's live set.</param>
    /// <param name="errors">The list refusal lines are appended to.</param>
    /// <param name="registry">The registry the command half reads, or <see langword="null"/> to skip that half.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channels"/> is <see langword="null"/>.</exception>
    public static void ValidateAgainst(BindingProfileDocument document, WorldChannelTable channels, IReadOnlyList<WorldSeatModeFamily> seatModes, AuthoredCommandSet? commands, List<string> errors, CommandRegistry? registry) {
        ArgumentNullException.ThrowIfNull(argument: channels);
        ValidateContexts(
            document: document,
            errors: errors,
            seatModes: seatModes
        );

        // An absent registry withholds the COMMAND lookup and nothing else. Returning early here instead made the
        // channel half — a pure function of the document's own table, needing no registry at all — conditional on
        // whether a container happened to exist yet, so one document was refused as world.instance.start and admitted
        // as --world. Whether a document is valid must not depend on which door it walked through.
        errors.AddRange(collection: BindingVocabularyCheck.Validate(
            document: document,
            lookups: new BindingVocabularyLookups(
                Command: (((registry is null) && DeferUnknownVerbsToRegistry)
            ? null
            : name => Lookup(commands: commands, name: name, registry: registry)),
                SourceKind: SourceKind,
                Channel: reference => (channels.TryGetShape(reference: reference, shape: out var shape) && (shape != ChannelShape.Text)),
                ChannelBinary: reference => (channels.TryGetShape(reference: reference, shape: out var shape) && (shape == ChannelShape.Binary)),
                SourceAddressable: source => !InputSourceVocabulary.IsExplicitlyUnaddressable(sourceId: source)
            )
        ).Errors);
    }
}
