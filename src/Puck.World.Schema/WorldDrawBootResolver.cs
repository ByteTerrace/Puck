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
/// backend tokens, parsed through <see cref="WorldHostTokens.ParseBackend"/> here. A numeric draw over the enum's
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
    private static bool TryDrawSite(WorldDefinition definition, ulong worldSeed, string instanceIdentity, string site, Draw draw, CellKind targetKind, out GeneratorEngine.FireResult fired, out string reason, long cursor = 0L, IReadOnlyList<ClosedBitset256>? masks = null) {
        fired = default;

        if (!GeneratorEngine.TryResolveSource(
            generators: definition.Generators,
            draw: draw,
            generator: out var generator,
            reason: out var resolveReason
        )) {
            reason = $"{site} {resolveReason}";

            return false;
        }

        if (!GeneratorEngine.TryFire(
            generator: generator,
            targetKind: targetKind,
            seedState: GeneratorEngine.ComputeSeedState(
                documentSeed: worldSeed,
                instanceIdentity: instanceIdentity,
                site: site
            ),
            stream: GeneratorEngine.ComputeStreamId(site: site),
            cursor: cursor,
            masks: masks,
            result: out fired,
            secret: draw.Secret,
            reason: out var fireReason,
            skip: draw.Skip
        )) {
            reason = $"{site} {fireReason}";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    /// <summary>Draws one numeric sample per selected cell of a keyed draw site, in cell order, advancing the site's
    /// cursor by the cell count — the whole-row roll of a dice tray, or a re-roll of the named <paramref name="keys"/>
    /// alone with every other cell held.</summary>
    /// <param name="definition">The document the site's source resolves against.</param>
    /// <param name="worldSeed">The document's world seed.</param>
    /// <param name="instanceIdentity">The running instance identity.</param>
    /// <param name="row">The keyed draw site.</param>
    /// <param name="keys">The cells to redraw, or <see langword="null"/> for every cell.</param>
    /// <param name="filled">The row with its drawn values, cursor, and drawn masks.</param>
    /// <param name="reason">Why the fill refused, on failure.</param>
    /// <returns><see langword="true"/> when every selected cell drew.</returns>
    public static bool TryFillKeyedSite(WorldDefinition definition, ulong worldSeed, string instanceIdentity, WorldStateRow row, IReadOnlyList<string>? keys, out WorldStateRow filled, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: row);

        filled = row;
        var site = WorldDrawSites.StateRow(rowName: row.Name);

        if (
            (row.Draw is not { } draw) ||
            !row.IsKeyed
        ) {
            reason = $"{site} is not a keyed draw site";

            return false;
        }

        if (!GeneratorEngine.TryResolveSource(
            generators: definition.Generators,
            draw: draw,
            generator: out var generator,
            reason: out var resolveReason
        )) {
            reason = $"{site} {resolveReason}";

            return false;
        }

        var cells = (row.Cells ?? []).ToArray();
        var selected = new List<int>(capacity: cells.Length);

        if (keys is null) {
            for (var index = 0; (index < cells.Length); index++) { selected.Add(item: index); }
        } else {
            foreach (var key in keys) {
                var index = Array.FindIndex(
                    array: cells,
                    match: cell => (cell.Key.Value == key)
                );

                if (
                    (index < 0) ||
                    selected.Contains(item: index)
                ) {
                    reason = $"{site} names no distinct cell '{key}' to redraw";

                    return false;
                }

                selected.Add(item: index);
            }

            selected.Sort();
        }

        if (selected.Count == 0) {
            reason = string.Empty;

            return true;
        }

        var values = new long[selected.Count];

        if (!GeneratorEngine.TryFireBatch(
            generator: generator,
            targetKind: row.Kind,
            seedState: GeneratorEngine.ComputeSeedState(
                documentSeed: worldSeed,
                instanceIdentity: instanceIdentity,
                site: site
            ),
            stream: GeneratorEngine.ComputeStreamId(site: site),
            cursor: row.DrawCursor,
            masks: row.DrawnMasks,
            values: values,
            masksAfter: out var masksAfter,
            reason: out var fireReason,
            skip: draw.Skip
        )) {
            reason = $"{site} {fireReason}";

            return false;
        }

        for (var slot = 0; (slot < selected.Count); slot++) {
            cells[selected[slot]] = cells[selected[slot]] with { Value = CellValue.FromNumber(kind: row.Kind, raw: values[slot]) };
        }

        filled = row with {
            Cells = cells,
            DrawCursor = checked((row.DrawCursor + selected.Count)),
            DrawnMasks = GeneratorEngine.MasksAfter(
            generator: generator,
            fired: masksAfter,
            previous: row.DrawnMasks
        ),
        };
        reason = string.Empty;

        return true;
    }
    /// <summary>Resolves every first-fill draw site in <paramref name="definition"/>.</summary>
    /// <param name="definition">The freshly parsed document after validation of its boot draw inputs.</param>
    /// <param name="instanceIdentity">The running instance's own identity — the seed ladder's INSTANCE rung.</param>
    /// <param name="resolved">The document with every first-fill site resolved, on success. The input instance
    /// is retained when neither a draw site nor a boot-time row read changes the document.</param>
    /// <param name="reason">Why a site refused, on failure.</param>
    /// <returns><see langword="true"/> when every first-fill site resolved.</returns>
    public static bool TryResolve(WorldDefinition definition, string instanceIdentity, out WorldDefinition resolved, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: instanceIdentity);

        resolved = definition;
        reason = string.Empty;

        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);
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
            var draw = row.Draw!;

            if (row.IsKeyed) {
                if (!TryFillKeyedSite(
                    definition: definition,
                    filled: out var filledRow,
                    instanceIdentity: instanceIdentity,
                    keys: null,
                    reason: out reason,
                    row: row,
                    worldSeed: worldSeed
                )) {
                    return false;
                }

                state ??= new List<WorldStateRow>(collection: authored);
                state[index] = filledRow;
                changed = true;

                continue;
            }

            if (!TryDrawSite(
                definition: definition,
                worldSeed: worldSeed,
                instanceIdentity: instanceIdentity,
                site: WorldDrawSites.StateRow(rowName: row.Name),
                draw: draw,
                targetKind: row.Kind,
                fired: out var fired,
                reason: out reason,
                cursor: row.DrawCursor,
                masks: row.DrawnMasks
            )) {
                return false;
            }

            var cell = ((fired.Text is { } text)
                ? new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: CellValue.Text(value: text)
                )
                : new StateCell(
                    Key: WorldStateRow.SlotKey,
                    // A numeric draw is already in the site's own encoding — raw FixedQ4816 bits on a fixed row — the
                    // contract the source's range/outcome values, the validator's domain narrowing and a lattice fill
                    // all share.
                    Value: CellValue.FromNumber(kind: row.Kind, raw: fired.Numeric!.Value)
                )
            );

            _ = GeneratorEngine.TryResolveSource(
                generators: definition.Generators,
                draw: draw,
                generator: out var generator,
                reason: out _
            );
            state ??= new List<WorldStateRow>(collection: authored);
            state[index] = row with {
                Cells = [cell],
                DrawCursor = (row.DrawCursor + fired.Samples),
                DrawnMasks = GeneratorEngine.MasksAfter(
                generator: generator,
                fired: fired.Masks,
                previous: row.DrawnMasks
            ),
            };
            changed = true;
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

            if (WorldHostTokens.ParseBackend(token: token) is not { } backend) {
                reason = $"host.backendRow '{backendRow}' read token '{token}', which names no backend ('{WorldHostTokens.BackendAuto}', '{WorldHostTokens.BackendDirectX}', or '{WorldHostTokens.BackendVulkan}')";

                return false;
            }

            Narrate(
                site: WorldDrawSites.HostBackend,
                instanceIdentity: instanceIdentity,
                settled: WorldHostTokens.BackendToken(backend: backend)
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
    }
}
