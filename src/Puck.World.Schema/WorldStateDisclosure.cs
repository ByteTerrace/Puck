using System.Text.Json.Serialization;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Opt-in observation policy. Null readers means public; an empty list means authority only.
/// Row and cell policies intersect. Replica-tier authorities remain fully trusted.</summary>
/// <param name="Readers">Canonical authenticated principal tokens; no seat or peer identity comes from the request payload.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateVisibility(IReadOnlyList<string>? Readers = null) {
    /// <summary>Whether this observation policy admits the authenticated recipient, or the public observer when absent.</summary>
    public bool Allows(WorldPrincipal? recipient) => Readers is null || (recipient is { } actor && Readers.Contains(actor.Describe(), StringComparer.Ordinal));
}

/// <summary>A persisted knowledge layer refreshed explicitly by the authority.</summary>
/// <param name="Source">The integer/boolean board observed.</param>
/// <param name="Mask">A boolean board over the same topology; true cells are currently observed.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateKnowledge(string Source, string Mask);

/// <summary>When a stored knowledge value was last seen and whether the latest observation still sees it.</summary>
/// <param name="Tick">The last observation tick.</param>
/// <param name="Visible">Whether the latest explicit refresh sees this cell.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateObservation(long Tick, bool Visible);

/// <summary>A disclosed literal cell. Hidden cells have no entry; no identity or placeholder is sent.</summary>
public sealed record WorldObservedCell(string Key, long Value, string? Text = null, WorldStateObservation? Observation = null);

/// <summary>A presentation observation, without draw seeds, cursors, masks, grants, or executable traits.</summary>
public sealed record WorldObservedRow(string Name, CellKind Kind, IReadOnlyList<WorldObservedCell> Cells);

/// <summary>Composes state observations for one authenticated recipient.</summary>
public static class WorldStateDisclosure {
    /// <summary>Projects only rows/cells with explicit observation policies; token attributes inherit their zone's restrictions.</summary>
    public static IReadOnlyList<WorldObservedRow>? Compose(WorldDefinition definition, WorldPrincipal? recipient) {
        var result = new List<WorldObservedRow>();
        foreach (var row in definition.State) {
            if (row.Visibility is null && !(row.Cells ?? []).Any(c => c.Visibility is not null)) {
                continue;
            }

            if (row.Visibility is { } policy && !policy.Allows(recipient)) {
                continue;
            }

            var cells = new List<WorldObservedCell>();
            foreach (var cell in row.Cells ?? []) {
                if (CanRead(definition, row, cell, recipient)) {
                    cells.Add(new(cell.Key.Value, cell.Value, cell.Text, cell.Observation));
                }
            }
            result.Add(new(row.Name.Value, row.Kind, cells));
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Whether the recipient may read a value, including its containing zone's policy.</summary>
    public static bool CanRead(WorldDefinition definition, WorldStateRow row, WorldStateCell cell, WorldPrincipal? recipient) {
        if ((row.Visibility is { } policy && !policy.Allows(recipient)) || (cell.Visibility is { } cellPolicy && !cellPolicy.Allows(recipient))) {
            return false;
        }

        var domain = row.Tokens is not null ? row.Name.Value : row.KeysFrom;
        if (domain is null) {
            return true;
        }

        foreach (var zone in definition.State) {
            if (zone.Zone?.Tokens != domain) {
                continue;
            }

            foreach (var member in zone.Cells ?? []) {
                if (member.Key != cell.Key) {
                    continue;
                }

                if ((zone.Visibility is { } zonePolicy && !zonePolicy.Allows(recipient)) || (member.Visibility is { } memberPolicy && !memberPolicy.Allows(recipient))) {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Refuses flattening a presentation binding that could disclose a restricted value.</summary>
    public static void ValidateBindings(WorldDefinition definition, object graph, WorldPrincipal? recipient) {
        foreach (var row in definition.State) {
            if (!(row.Cells ?? []).Any(c => !CanRead(definition, row, c, recipient)) && (row.Visibility is null || row.Visibility.Allows(recipient))) {
                continue;
            }

            if (WorldStateDocumentValues.ReferencesRow(definition, graph, row.Name.Value)) {
                throw new InvalidOperationException("a presentation binding references restricted state; bind an explicit observation layer instead");
            }
        }
    }
}
