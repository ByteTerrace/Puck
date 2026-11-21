namespace Puck.World;

/// <summary>
/// Resolves every FIRST-FILL <see cref="Draw"/> site in a freshly loaded document — the ONE choke point that
/// turns an authored draw declaration into the value the rest of the engine ever sees. Runs once per fresh load
/// (process boot, and each <c>world.instance.start</c>), never on a live mutation: a live redraw rides the existing
/// <c>generate</c> mutation instead, through the SAME <c>GeneratorEngine</c> core, so the two can never disagree
/// about what a site's cursor position means.
/// </summary>
/// <remarks>
/// <para><b>Two site classes, two settle rules.</b> A BOOT-ONLY site — <c>bodies.capacityRow</c>
/// and <c>host.backendRow</c> — reads a state row after its first fill and narrates the settled document field.
/// The census retains its row reference for each fresh load. The backend clears its reference because its
/// document contract admits either a literal backend or a row reference; the source row retains the drawn value.
/// A STATE site
/// (a <see cref="WorldStateRow"/>'s own <see cref="StateRow.Draw"/>) is different — the facet is NEVER cleared
/// (it stays redrawable), the fill applies ONLY while the row carries no cell yet, and the site's cursor and drawn
/// masks persist. That is what makes an authored <c>value</c> a deliberate override, and what keeps a save/reload from
/// re-rolling a value the player has already seen: a reloaded site already holds a cell, so nothing refills it, and
/// the next redraw resumes from the stored cursor.</para>
/// <para><b>Why the backend draws a NAME.</b> The host backend's natural spelling is a weighted TEXT source over the
/// backend tokens, parsed through <see cref="WorldBackendTokens.Parse"/> here. A numeric draw over the enum's
/// ordinals would read at the authoring site as a number nothing explains, and would silently re-point itself the day
/// a member is inserted.</para>
/// <para><b>What the settle-time refusals still own.</b> Admission proves a DRAWN row over every outcome its source
/// can produce, so neither refusal below can fire for a value a draw settled. They remain the door for the value a
/// row carries by another route — an authored literal cell, a checkpoint, a live write — whose single outcome no
/// source check can see.</para>
/// </remarks>
public static class WorldDrawBootResolver {
    // Shared with boot-input validation: a persisted site that will not fire needs only final admission.
    internal static bool NeedsFirstFill(WorldStateRow row) => ((row.Draw is not null) &&
        (row.IsKeyed ? ((row.DrawCursor == 0L) && (row.Cells is { Count: > 0 })) : (row.Cells is not { Count: > 0 })));

    private static void Narrate(string site, string instanceIdentity, string settled) =>
        Console.Error.WriteLine(value: $"[world.draw: settled {site} instance={instanceIdentity} -> {settled}]");
    /// <summary>Runs one whole-field pass of a lattice row's draw fill without keeping its values: the one admission
    /// of "this row's source can draw a pass at this position" that boot admission, the <c>generate</c> compose and
    /// the live paint all rest on, so the paint never meets a refusal the others did not.</summary>
    /// <param name="definition">The document the row's source and field region resolve against.</param>
    /// <param name="instanceIdentity">The running instance identity.</param>
    /// <param name="row">The lattice row carrying a draw fill.</param>
    /// <param name="cursor">The raw-word position the pass starts at.</param>
    /// <param name="masks">The drawn masks the pass starts from.</param>
    /// <param name="advances">The raw-word cursor delta the pass consumed, on success.</param>
    /// <param name="masksAfter">The drawn masks to persist after the pass, on success.</param>
    /// <param name="reason">Why the pass was refused, naming the row, on failure.</param>
    /// <returns><see langword="true"/> when every cell of the pass drew.</returns>
    public static bool TryAdvanceLatticePass(WorldDefinition definition, string instanceIdentity, WorldStateRow row, long cursor, IReadOnlyList<ClosedBitset256>? masks, out long advances, out IReadOnlyList<ClosedBitset256>? masksAfter, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: row);

        advances = 0L;
        masksAfter = null;

        if (WorldLatticeFill.FindDraw(trait: row.Field) is not { } fill) {
            reason = $"state row '{row.Name}' is a lattice row with no draw paint";

            return false;
        }

        if (!GeneratorEngine.TryResolveSource(
            generators: definition.Generators,
            draw: new Draw(
                Source: fill.Source,
                Generator: fill.Generator
            ),
            generator: out var generator,
            reason: out var resolveReason
        )) {
            reason = $"state row '{row.Name}' draw fill {resolveReason}";

            return false;
        }

        var region = definition.FieldRegions.FirstOrDefault(predicate: candidate => candidate.Fields.Any(predicate: field => (field.Name == row.Name.Value)));

        if (region is null) {
            reason = $"state row '{row.Name}' draw fill names no field region that carries the row";

            return false;
        }

        var shape = region.Lattice;
        var site = WorldDrawSites.StateRow(rowName: row.Name);

        if (!GeneratorEngine.TryAdvanceBatch(
            generator: generator,
            targetKind: CellKind.Fixed,
            seed: GeneratorEngine.ComputeDrawSeed(
                documentSeed: (definition.Generation?.WorldSeed ?? 0UL),
                instanceIdentity: instanceIdentity,
                site: site
            ),
            cursor: cursor,
            masks: masks,
            sampleCount: ((shape.Width * shape.Layers) * shape.Depth),
            masksAfter: out var fired,
            advances: out advances,
            reason: out var fireReason
        )) {
            reason = $"state row '{row.Name}' draw fill {fireReason}";

            return false;
        }

        masksAfter = GeneratorEngine.MasksAfter(
            generator: generator,
            fired: fired,
            previous: masks
        );
        reason = string.Empty;

        return true;
    }
    /// <summary>Stores one emission in its target cell inside the caller's transaction.</summary>
    public delegate bool StoreDraw<T>(T cell, CellValue value, out string reason);

    /// <summary>Fills a slot or the selected keyed cells and returns the continuation to persist with them.
    /// Keyed samples are assigned in ordinal key-name order in every store.</summary>
    public static bool TryGenerate<T>(WorldDefinition definition, string instanceIdentity, StateRow row, PrivateDrawAuthority? authority,
        long cursor, IReadOnlyList<ClosedBitset256>? masks, IReadOnlyList<string>? keys, T slot,
        IEnumerable<T> cells, Func<T, string> name, StoreDraw<T> store, out long next, out IReadOnlyList<ClosedBitset256>? nextMasks, out string reason) {
        next = cursor;
        nextMasks = masks;
        if (row.Draw is not { } draw) { reason = $"state row '{row.Name}' declares no draw"; return false; }
        if (!GeneratorEngine.TryResolveExecutableSource(definition.Generators, draw, authority, out var sample, out var generator, out reason)) { return false; }
        var seed = GeneratorEngine.ComputeDrawSeed(definition.Generation?.WorldSeed ?? 0UL, instanceIdentity, WorldDrawSites.StateRow(row.Name));

        if (!row.IsKeyed) {
            if (keys is not null) { reason = "keys select cells of a keyed draw site alone"; return false; }
            if (!GeneratorEngine.TryFire(generator, row.Kind, seed, cursor, masks, out var fired, out reason, sample, draw.Skip)) { return false; }
            var value = fired.Text is { } text ? CellValue.Text(text) : CellValue.FromNumber(row.Kind, fired.Numeric!.Value);
            next = checked(cursor + fired.Advances);
            nextMasks = GeneratorEngine.MasksAfter(generator: generator, fired: fired.Masks, previous: masks);
            return store(slot, value, out reason);
        }

        var requested = keys?.ToHashSet(StringComparer.Ordinal);
        var selected = cells.Select(cell => (Cell: cell, Name: name(cell)))
            .Where(entry => requested is null || requested.Contains(entry.Name))
            .OrderBy(static entry => entry.Name, StringComparer.Ordinal).ToArray();
        if (keys is not null && selected.Length != keys.Count) { reason = "generate names an absent or repeated cell"; return false; }
        if (selected.Length == 0) { reason = string.Empty; return true; }
        if (!GeneratorEngine.DrawStream.TryOpen(generator, row.Kind, seed, cursor, masks, out var stream, out reason, sample, draw.Skip)) { return false; }
        foreach (var entry in selected) {
            if (!stream.TryNext(out var raw, out reason) || !store(entry.Cell, CellValue.FromNumber(row.Kind, raw), out reason)) { return false; }
        }
        next = stream.Cursor;
        nextMasks = GeneratorEngine.MasksAfter(generator: generator, fired: stream.MasksAfter, previous: masks);
        return true;
    }

    /// <summary>Fills a document draw site and retains the written cells and their continuation together.</summary>
    public static bool TryFillSite(WorldDefinition definition, string instanceIdentity, WorldStateRow row, IReadOnlyList<string>? keys,
        PrivateDrawAuthority? authority, out WorldStateRow filled, out string reason) {
        filled = row;
        var previous = row.Cells;
        StateCell[]? written = null;
        if (!TryGenerate(definition, instanceIdentity, row, authority, row.DrawCursor, row.DrawnMasks, keys, 0,
            Enumerable.Range(0, previous?.Count ?? 0), index => previous![index].Key.Value, Write, out var next, out var masks, out reason)) {
            reason = $"state row '{row.Name}' {reason}";
            return false;
        }
        if (written is not null) { filled = row with { Cells = written, DrawCursor = next, DrawnMasks = masks }; }
        return true;

        bool Write(int index, CellValue value, out string refusal) {
            if (row.IsKeyed) {
                written ??= previous!.ToArray();
                written[index] = written[index] with { Value = value };
            } else {
                written = [new StateCell(Key: StateRow.SlotKey, Value: value)];
            }
            refusal = string.Empty;
            return true;
        }
    }
    /// <summary>Resolves every first-fill draw site in <paramref name="definition"/>.</summary>
    /// <param name="definition">The freshly parsed document after validation of its boot draw inputs.</param>
    /// <param name="instanceIdentity">The running instance's own identity — the seed ladder's INSTANCE rung.</param>
    /// <param name="resolved">The document with every first-fill site resolved, on success. The input instance
    /// is retained when neither a draw site nor a boot-time row read changes the document.</param>
    /// <param name="reason">Why a site refused, on failure.</param>
    /// <param name="previous">The live state when installing a writer’s rebuild.</param>
    /// <param name="restore">The installation ordinals of a restored journal document.</param>
    /// <param name="keys">The authority credential, or the isolated preview sampler for an authoring read.</param>
    /// <returns><see langword="true"/> when every first-fill site resolved.</returns>
    public static bool TryResolve(WorldDefinition definition, string instanceIdentity, out WorldDefinition resolved, out string reason, WorldDrawSampler? keys = null, WorldDefinition? previous = null, WorldDrawPositions? restore = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: instanceIdentity);

        // A document with no private stream installs only its allocator, which no sampler decides, so the missing
        // sampler is made only for a row that draws: a document whose rows are all filled resolves allocating nothing.
        var sampler = keys;

        try {
            definition = ((restore is not null) ? Authority().Install(definition, restore: restore)
                : ((sampler is not null) || WorldDrawSampler.InstallsAny(definition: definition)) ? Authority().Install(definition, previous)
                : WorldDrawSampler.WithLast(definition, (previous?.StateRaw?.DrawInstallCount ?? 0L)));
        } catch (InvalidDataException error) {
            resolved = definition;
            reason = error.Message;
            return false;
        }
        resolved = definition;
        reason = string.Empty;

        var population = definition.Population;
        var host = definition.Host;
        var changed = false;

        var authored = definition.AuthoredState;
        List<WorldStateRow>? state = null;

        for (var index = 0; (index < authored.Count); index++) {
            var row = authored[index];
            // FIRST FILL ONLY: a row already carrying a cell — authored with a literal, or loaded from a save that
            // already drew — is left exactly as it is, cursor included.
            if (!NeedsFirstFill(row: row)) {
                continue;
            }
            var authority = Authority();
            if (!TryFillSite(definition, instanceIdentity, row, null, authority, out var filled, out reason)) { return false; }
            state ??= new List<WorldStateRow>(collection: authored);
            state[index] = authority.IsPreview && row.Draw!.PrivateStream ? filled with { BeforePrivatePreview = row } : filled;
            changed = true;
        }

        // A lattice row's draw fill paints a whole pass at its persisted cursor and masks when the world starts, so a
        // pass its source refuses (an exhausted bounded draw, a drawn-out mask) refuses the load here, by name.
        for (var index = 0; (index < authored.Count); index++) {
            var row = authored[index];

            if (
                (WorldLatticeFill.FindDraw(trait: row.Field) is not null) &&
                !TryAdvanceLatticePass(
                    definition: definition,
                    instanceIdentity: instanceIdentity,
                    row: row,
                    cursor: row.DrawCursor,
                    masks: row.DrawnMasks,
                    advances: out _,
                    masksAfter: out _,
                    reason: out reason
                )
            ) {
                return false;
            }
        }

        // SITE READS run AFTER row first-fills, so a Boot-drawn row is readable the same boot it draws. The value
        // narrated here is the row's; the row itself stays the persisted evidence.
        if (population.CapacityRow is { } capacityRow) {
            var declared = StateRows.FindStateRow(name: capacityRow, rows: (state ?? authored));

            if (
                (declared?.Cells is not [{ } censusCell, ..]) ||
                (censusCell.Value.Kind is not (CellKind.Bool or CellKind.Fixed or CellKind.Int))
            ) {
                reason = $"bodies.capacityRow '{capacityRow}' names no filled numeric scalar row this boot could read";

                return false;
            }

            var census = censusCell.Value.Raw;

            if (
                (census < 0) ||
                (census > int.MaxValue)
            ) {
                reason = $"bodies.capacityRow '{capacityRow}' read {census}, which does not fit a non-negative int32 census";

                return false;
            }

            Narrate(
                site: WorldDrawSites.PopulationCapacity,
                instanceIdentity: instanceIdentity,
                settled: census.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
            );

            population = (population with { CapacityRaw = ((int)census) });
            changed = true;
        }

        if (host.BackendRow is { } backendRow) {
            var declared = StateRows.FindStateRow(name: backendRow, rows: (state ?? authored));
            var token = (((declared?.Cells is [{ Value.Kind: CellKind.Text } tokenCell, ..])
                ? tokenCell.Value.AsText
                : null) ?? string.Empty);

            if (WorldBackendTokens.Parse(token: token) is not { } backend) {
                reason = $"host.backendRow '{backendRow}' read token '{token}', which names no backend ('{WorldBackendTokens.Auto}', '{WorldBackendTokens.DirectX}', or '{WorldBackendTokens.Vulkan}')";

                return false;
            }

            Narrate(
                site: WorldDrawSites.HostBackend,
                instanceIdentity: instanceIdentity,
                settled: WorldBackendTokens.Token(backend: backend)
            );

            host = (host with { Backend = backend, BackendRow = null });
            changed = true;
        }

        if (changed) {
            // An absent section is not an authored default row. A state-only draw must not materialize the host's
            // absent sentinel (whose dimensions are deliberately zero) and turn a valid document into an invalid one.
            var drawn = ((state is null) ? definition : definition.WithWorldState(rows: state));

            resolved = drawn with {
                PopulationRaw = ((population == definition.Population) ? definition.PopulationRaw : population),
                HostRaw = (ReferenceEquals(objA: host, objB: definition.Host) ? definition.HostRaw : host),
            };
        }

        return true;

        WorldDrawSampler Authority() => (sampler ??= WorldDrawSampler.Missing);
    }
}
