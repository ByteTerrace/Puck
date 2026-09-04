using System.Text.Json.Serialization;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>What an observer learns about a row's cells it may not read.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldHiddenCells>))]
public enum WorldHiddenCells : byte {
    /// <summary>Hidden cells leave no trace: neither their count nor their positions.</summary>
    Omit,
    /// <summary>The observed row reports how many cells were hidden and nothing else about them.</summary>
    Count,
    /// <summary>Every hidden cell appears in pile order as an anonymous placeholder (a card back): no key, no
    /// value, no text, no observation stamp.</summary>
    Placeholder,
}

/// <summary>Opt-in observation policy. Null readers means public; an empty list means authority only.
/// Row and cell policies intersect. Replica-tier authorities remain fully trusted.</summary>
/// <param name="Readers">Canonical authenticated principal tokens; no seat or peer identity comes from the request payload.</param>
/// <param name="Hidden">What an observer who may read the row learns about the cells it may not.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateVisibility(IReadOnlyList<string>? Readers = null, WorldHiddenCells Hidden = WorldHiddenCells.Omit) {
    /// <summary>Whether this observation policy admits the recipient named by its canonical token, or the public
    /// observer when the token is null.</summary>
    public bool Allows(string? recipient) {
        if (Readers is null) {
            return true;
        }
        if (recipient is null) {
            return false;
        }
        for (var index = 0; index < Readers.Count; index++) {
            if (string.Equals(Readers[index], recipient, StringComparison.Ordinal)) {
                return true;
            }
        }
        return false;
    }
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

/// <summary>A disclosed literal cell, or, under <see cref="WorldHiddenCells.Placeholder"/>, an anonymous card back
/// (<see cref="Hidden"/> true, empty key, zero value, no text, no observation).</summary>
public sealed record WorldObservedCell(string Key, long Value, string? Text = null, WorldStateObservation? Observation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Hidden = false);

/// <summary>A presentation observation, without draw seeds, cursors, masks, grants, or executable traits.
/// <see cref="HiddenCount"/> counts the cells the row's <see cref="WorldStateVisibility.Hidden"/> policy withheld
/// from this observer (placeholders included), zero under <see cref="WorldHiddenCells.Omit"/>.</summary>
public sealed record WorldObservedRow(string Name, CellKind Kind, IReadOnlyList<WorldObservedCell> Cells,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int HiddenCount = 0);

/// <summary>Composes state observations for one authenticated recipient.</summary>
public static class WorldStateDisclosure {
    /// <summary>Projects only rows/cells with explicit observation policies; token attributes inherit their zone's restrictions.</summary>
    public static IReadOnlyList<WorldObservedRow>? Compose(WorldDefinition definition, WorldPrincipal? recipient) {
        var observer = new Observer(definition, recipient);
        var result = new List<WorldObservedRow>();
        foreach (var row in definition.State) {
            if (row.Visibility is null && !(row.Cells ?? []).Any(c => c.Visibility is not null)) {
                continue;
            }

            if (row.Visibility is { } policy && !policy.Allows(observer.Name)) {
                continue;
            }

            var cells = new List<WorldObservedCell>();
            var hidden = 0;
            var hiddenPolicy = row.Visibility?.Hidden ?? WorldHiddenCells.Omit;
            foreach (var cell in row.Cells ?? []) {
                if (observer.CanRead(row, cell)) {
                    cells.Add(new(cell.Key.Value, cell.Value, cell.Text, cell.Observation));
                    continue;
                }
                if (hiddenPolicy == WorldHiddenCells.Omit) {
                    continue;
                }
                hidden++;
                if (hiddenPolicy == WorldHiddenCells.Placeholder) {
                    cells.Add(new(string.Empty, 0L, Hidden: true));
                }
            }
            result.Add(new(row.Name.Value, row.Kind, cells, hidden));
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Whether the recipient may read a value, including its containing zone's policy.</summary>
    public static bool CanRead(WorldDefinition definition, WorldStateRow row, WorldStateCell cell, WorldPrincipal? recipient) =>
        new Observer(definition, recipient).CanRead(row, cell);

    /// <summary>Refuses flattening a presentation binding that could disclose a restricted value.</summary>
    public static void ValidateBindings(WorldDefinition definition, object graph, WorldPrincipal? recipient) {
        var observer = new Observer(definition, recipient);
        foreach (var row in definition.State) {
            if (!(row.Cells ?? []).Any(c => !observer.CanRead(row, c)) && (row.Visibility is null || row.Visibility.Allows(observer.Name))) {
                continue;
            }

            if (WorldStateDocumentValues.ReferencesRow(definition, graph, row.Name.Value)) {
                throw new InvalidOperationException("a presentation binding references restricted state; bind an explicit observation layer instead");
            }
        }
    }

    // One recipient's view of one document: the canonical token is formatted once, and the zones are indexed by
    // token domain once, so a cell's read check costs the members of its own domain's zones and nothing else.
    private readonly struct Observer {
        private readonly Dictionary<string, List<WorldStateRow>> m_zonesByDomain;

        public Observer(WorldDefinition definition, WorldPrincipal? recipient) {
            Name = recipient?.Describe();
            m_zonesByDomain = new(StringComparer.Ordinal);
            foreach (var row in definition.State) {
                if (row.Zone?.Tokens is not { } domain) {
                    continue;
                }
                if (!m_zonesByDomain.TryGetValue(domain, out var zones)) {
                    zones = [];
                    m_zonesByDomain[domain] = zones;
                }
                zones.Add(row);
            }
        }

        public string? Name { get; }

        public bool CanRead(WorldStateRow row, WorldStateCell cell) {
            if ((row.Visibility is { } policy && !policy.Allows(Name)) || (cell.Visibility is { } cellPolicy && !cellPolicy.Allows(Name))) {
                return false;
            }

            var domain = row.Tokens is not null ? row.Name.Value : row.KeysFrom;
            if (domain is null || !m_zonesByDomain.TryGetValue(domain, out var zones)) {
                return true;
            }

            foreach (var zone in zones) {
                foreach (var member in zone.Cells ?? []) {
                    if (member.Key != cell.Key) {
                        continue;
                    }

                    if ((zone.Visibility is { } zonePolicy && !zonePolicy.Allows(Name)) || (member.Visibility is { } memberPolicy && !memberPolicy.Allows(Name))) {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
