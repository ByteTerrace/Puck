using Puck.Assets;

namespace Puck.World;

/// <summary>A replacement computed from the current view and this stream's last delivered image.</summary>
public readonly record struct WorldRowReplacement(int Row, WorldRecipientImage Image);

/// <summary>One delivery stream's history. Definition and row delivery seed the same hashes; reads cannot move
/// them. Versions count observable deliveries in this stream, never authority installs.</summary>
public sealed class WorldDeliveryCursor {
    private readonly Dictionary<string, ContentPin> m_rows = [];
    private readonly HashSet<int> m_metadataPrimers = [];
    private ContentPin? m_definition;
    private WorldDefinition? m_lastSource;

    /// <summary>The sequence this stream has delivered.</summary>
    public long Sequence { get; private set; }
    /// <summary>The number of restricted rows retained by this stream.</summary>
    public int RetainedRows => m_rows.Count;
    /// <summary>Whether the replica's current disclosed document equals the one this stream delivered, including
    /// subsequent row snapshots. Used at a traveller's install boundary, never against the authority identity.</summary>
    public bool Holds(WorldRecipientView view) => m_definition is null || m_definition == view.Images.DefinitionImage(view).Identity;
    /// <summary>Delivers a definition only when its observable content differs, and seeds all its row images.</summary>
    public WorldDefinition? DeliverDefinition(WorldRecipientView view) {
        if (!view.Observes) { return null; }
        var (definition, identity) = view.Ceiling == Puck.World.Protocol.WorldDisclosureTier.Replica
            ? view.Images.DefinitionImage(view) : Presentation(view);
        if (ReferenceEquals(definition, m_lastSource)) { return null; }
        m_lastSource = definition;
        if (identity == m_definition) { return null; }
        m_definition = identity;
        Seed(view);
        Delivered();
        return definition;
    }
    private static (WorldDefinition Definition, ContentPin Identity) Presentation(WorldRecipientView view) {
        var definition = view.Definition;
        return (definition, ContentPin.Compute(WorldDefinitionSerialization.SerializeCompact(definition)));
    }
    /// <summary>Records the images a presentation delivery actually carried.</summary>
    public void Delivered(WorldRecipientView view) { Seed(view); Delivered(); }
    /// <summary>Records one observable delivery, preserving the sequence across a withheld span.</summary>
    public void Delivered() => Sequence = checked(Sequence + 1);
    private void Seed(WorldRecipientView view) {
        m_rows.Clear();
        m_metadataPrimers.Clear();
        for (var row = 0; row < view.Source.AuthoredState.Count; row++) {
            if (!view.ReadsWhole(row)) {
                var image = view.Image(row);
                // Replica state rows carry readable cells. A nonzero hidden count or backs is carried by the primer
                // snapshot's observation header instead; do not pretend the definition delivered that metadata.
                if (view.Ceiling == Puck.World.Protocol.WorldDisclosureTier.Replica && image.Observation is { HiddenCount: > 0 }) { m_metadataPrimers.Add(row); }
                m_rows[view.Arena.Rows[row].Name.Value] = image.Identity;
            }
        }
    }
    internal IReadOnlyList<WorldRowReplacement> Advance(WorldRecipientView view, ReadOnlySpan<int> rows) {
        var result = new List<WorldRowReplacement>();
        var audience = view.Images.Audience(view);
        foreach (var row in view.Images.Affected(view, rows).Concat(m_metadataPrimers).Distinct().Order().ToArray()) {
            var name = view.Arena.Rows[row].Name.Value;
            if (view.ReadsWhole(row) && !m_rows.ContainsKey(name)) { continue; }
            var image = view.Images.Image(view, row, audience);
            if (view.ReadsWhole(row)) { m_rows.Remove(name); result.Add(new(row, image)); continue; }
            if (!m_metadataPrimers.Remove(row) && m_rows.TryGetValue(name, out var held) && held == image.Identity) { continue; }
            m_rows[name] = image.Identity;
            result.Add(new(row, image));
        }
        if (result.Count != 0) { DeliveredRows(view); }
        return result;
    }
    /// <summary>Records a snapshot's delivered rows in the document identity too, so a later hidden-only install
    /// cannot resend public values already received through snapshots.</summary>
    public void DeliveredRows(WorldRecipientView view) {
        if (m_definition is not null) {
            var image = view.Images.DefinitionImage(view);
            m_definition = image.Identity; m_lastSource = image.Definition;
        }
        Delivered();
    }
    /// <summary>Releases content after withholding or detachment. A resumed delivery seeds it again, while the
    /// stream's sequence continues.</summary>
    public void Release() { m_rows.Clear(); m_metadataPrimers.Clear(); m_definition = null; m_lastSource = null; }
}
