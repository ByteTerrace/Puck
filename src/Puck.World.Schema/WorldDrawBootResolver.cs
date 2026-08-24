namespace Puck.World;

/// <summary>
/// Resolves every FIRST-FILL <see cref="WorldDraw"/> site in a freshly loaded document — the ONE choke point that
/// turns an authored draw declaration into the value the rest of the engine ever sees. Runs once per fresh load
/// (process boot, and each <c>world.instance.start</c>), never on a live mutation: a live redraw rides the existing
/// <c>generate</c> mutation instead, through the SAME <c>WorldGeneratorEngine</c> core, so the two can never disagree
/// about what a site's cursor position means.
/// </summary>
/// <remarks>
/// <para><b>Two site classes, two settle rules.</b> A BOOT-ONLY site — <c>bodies.capacityRow</c>
/// and <c>host.backendRow</c> — is a document FIELD read exactly once at composition: this
/// resolver draws it, writes the settled value into the ordinary literal field, CLEARS the facet, and NARRATES the
/// settlement on stderr. The narration is not decoration: settling erases the only evidence the value was random, so
/// without it nothing anywhere could say the census or the backend was drawn, or which site decided it. A STATE site
/// (a <see cref="WorldStateRow"/>'s own <see cref="WorldStateRow.Draw"/>) is different — the facet is NEVER cleared
/// (it stays redrawable), the fill applies ONLY while the row carries no cell yet, and the site's cursor and decks
/// persist. That is what makes an authored <c>value</c> a deliberate override, and what keeps a save/reload from
/// re-rolling a value the player has already seen: a reloaded site already holds a cell, so nothing refills it, and
/// the next redraw resumes from the stored cursor.</para>
/// <para><b>Why the backend draws a NAME.</b> The host backend's natural spelling is a weighted TEXT source over the
/// backend tokens, parsed through <see cref="WorldHostTokens.ParseBackend"/> here. A numeric draw over the enum's
/// ordinals would read at the authoring site as a number nothing explains, and would silently re-point itself the day
/// a member is inserted. Validation already refuses a token naming no backend, so the refusal below is a loud guard
/// against that check ever going soft, not the primary door.</para>
/// </remarks>
internal static class WorldDrawBootResolver {
    private static void Narrate(string site, string instanceIdentity, string settled) =>
        Console.Error.WriteLine(value: $"[world.draw: settled {site} instance={instanceIdentity} -> {settled}]");
    private static bool TryDrawSite(WorldDefinition definition, ulong worldSeed, string instanceIdentity, string site, WorldDraw draw, CellKind targetKind, out WorldGeneratorEngine.FireResult fired, out string reason, long cursor = 0L, IReadOnlyList<long>? decks = null) {
        fired = default;

        if (!WorldGeneratorEngine.TryResolveSource(
            generators: definition.Generators,
            draw: draw,
            generator: out var generator,
            reason: out var resolveReason
        )) {
            reason = $"{site} {resolveReason}";

            return false;
        }

        if (!WorldGeneratorEngine.TryFire(
            generator: generator,
            targetKind: targetKind,
            seedState: WorldGeneratorEngine.ComputeSeedState(
                instanceIdentity: instanceIdentity,
                site: site,
                worldSeed: worldSeed
            ),
            stream: WorldGeneratorEngine.ComputeStreamId(site: site),
            cursor: cursor,
            decks: decks,
            result: out fired,
            reason: out var fireReason
        )) {
            reason = $"{site} {fireReason}";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    /// <summary>Resolves every first-fill draw site in <paramref name="definition"/>.</summary>
    /// <param name="definition">The freshly parsed, already-validated document.</param>
    /// <param name="instanceIdentity">The running instance's own identity — the seed ladder's INSTANCE rung.</param>
    /// <param name="resolved">The document with every first-fill site resolved, on success.</param>
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



        var state = new List<WorldStateRow>(capacity: definition.State.Count);

        foreach (var row in definition.State) {
            // FIRST FILL ONLY: a row already carrying a cell — authored with a literal, or loaded from a save that
            // already drew — is left exactly as it is, cursor included.
            if (
                (row.Draw is not { } draw) ||
                (row.Cells is { Count: > 0 })
            ) {
                state.Add(item: row);

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
                decks: row.DrawDecks
            )) {
                return false;
            }

            var cell = ((fired.Text is { } text)
                ? new WorldStateCell(
                    Key: WorldStateRow.SlotKey,
                    Text: text
                )
                : new WorldStateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: fired.Numeric!.Value
                )
            );

            state.Add(item: (row with { Cells = [cell], DrawCursor = (row.DrawCursor + fired.Samples), DrawDecks = (fired.Decks ?? row.DrawDecks) }));
            changed = true;
        }

        // SITE READS run AFTER row first-fills, so a Boot-drawn row is readable the same boot it draws. The value
        // narrated here is the row's — the row itself stays the persisted evidence, so nothing is cleared.
        if (population.CapacityRow is { } capacityRow) {
            var rows = state;
            var declared = rows.Find(match: r => string.Equals(a: r.Name.Value, b: capacityRow, comparisonType: StringComparison.Ordinal));

            if (declared?.Cells is not [{ } censusCell, ..]) {
                reason = $"bodies.capacityRow '{capacityRow}' names no filled scalar row this boot could read";

                return false;
            }

            var census = censusCell.Value;

            if ((census < 0) || (census > int.MaxValue)) {
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
            var rows = state;
            var declared = rows.Find(match: r => string.Equals(a: r.Name.Value, b: backendRow, comparisonType: StringComparison.Ordinal));
            var token = (((declared?.Cells is [{ } tokenCell, ..]) ? tokenCell.Text : null) ?? string.Empty);

            if (WorldHostTokens.ParseBackend(token: token) is not { } backend) {
                reason = $"host.backendRow '{backendRow}' read token '{token}', which names no backend ('{WorldHostTokens.BackendAuto}', '{WorldHostTokens.BackendDirectX}', or '{WorldHostTokens.BackendVulkan}')";

                return false;
            }

            Narrate(
                site: WorldDrawSites.HostBackend,
                instanceIdentity: instanceIdentity,
                settled: WorldHostTokens.BackendToken(backend: backend)
            );

            host = (host with { Backend = backend });
            changed = true;
        }

        if (changed) {
            resolved = (definition.WithWorldState(rows: state) with { PopulationRaw = population, HostRaw = host });
        }

        return true;
    }
}
