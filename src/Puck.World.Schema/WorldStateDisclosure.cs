using System.Text.Json.Serialization;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>A disclosed literal cell, or, under <see cref="HiddenCells.Placeholder"/>, an anonymous card back
/// (<see cref="Hidden"/> true, empty key, zero value, no text, no observation).</summary>
public sealed record WorldObservedCell(string Key, long Value, string? Text = null, StateObservation? Observation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Hidden = false);

/// <summary>A presentation observation, without draw seeds, cursors, masks, grants, or executable traits.
/// <see cref="HiddenCount"/> counts the cells the row's <see cref="StateVisibility.Hidden"/> policy withheld
/// from this observer (placeholders included), zero under <see cref="HiddenCells.Omit"/>.</summary>
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

            if (row.Visibility is { } policy && !policy.Allows(observer.Name, definition.State)) {
                continue;
            }

            var cells = new List<WorldObservedCell>();
            var hidden = 0;
            var hiddenPolicy = row.Visibility?.Hidden ?? HiddenCells.Omit;
            foreach (var cell in row.Cells ?? []) {
                if (observer.CanRead(row, cell)) {
                    cells.Add(new(cell.Key.Value, cell.Value, cell.Text, cell.Observation));
                    continue;
                }
                if (hiddenPolicy == HiddenCells.Omit) {
                    continue;
                }
                hidden++;
                if (hiddenPolicy == HiddenCells.Placeholder) {
                    cells.Add(new(string.Empty, 0L, Hidden: true));
                }
            }
            result.Add(new(row.Name.Value, row.Kind, cells, hidden));
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Whether the recipient may read a value, including its containing zone's policy.</summary>
    public static bool CanRead(WorldDefinition definition, WorldStateRow row, StateCell cell, WorldPrincipal? recipient) =>
        new Observer(definition, recipient).CanRead(row, cell);

    /// <summary>Refuses flattening a presentation binding that could disclose a restricted value.</summary>
    public static void ValidateBindings(WorldDefinition definition, object graph, WorldPrincipal? recipient) {
        var observer = new Observer(definition, recipient);
        foreach (var row in definition.State) {
            if (!(row.Cells ?? []).Any(c => !observer.CanRead(row, c)) && (row.Visibility is null || row.Visibility.Allows(observer.Name, definition.State))) {
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
        private readonly WorldDefinition m_definition;

        public Observer(WorldDefinition definition, WorldPrincipal? recipient) {
            m_definition = definition;
            Name = recipient?.Describe();
            m_zonesByDomain = new(StringComparer.Ordinal);
            foreach (var row in definition.State) {
                if (row.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } zoneDomain) {
                    continue;
                }
                var domain = zoneDomain.Row.Value;
                if (!m_zonesByDomain.TryGetValue(domain, out var zones)) {
                    zones = [];
                    m_zonesByDomain[domain] = zones;
                }
                zones.Add(row);
            }
        }

        public string? Name { get; }

        public bool CanRead(WorldStateRow row, StateCell cell) {
            if ((row.Visibility is { } policy && !policy.Allows(Name, m_definition.State)) || (cell.Visibility is { } cellPolicy && !cellPolicy.Allows(Name, m_definition.State))) {
                return false;
            }

            var domain = (row.EffectiveDomain is StateDomain.KeysOf keysOf) ? keysOf.Row.Value : row.Name.Value;
            if (domain is null || !m_zonesByDomain.TryGetValue(domain, out var zones)) {
                return true;
            }

            foreach (var zone in zones) {
                foreach (var member in zone.Cells ?? []) {
                    if (member.Key != cell.Key) {
                        continue;
                    }

                    if ((zone.Visibility is { } zonePolicy && !zonePolicy.Allows(Name, m_definition.State)) || (member.Visibility is { } memberPolicy && !memberPolicy.Allows(Name, m_definition.State))) {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
