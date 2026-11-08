using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private readonly WorldDisclosureImages m_disclosureImages = new();
    private readonly ArenaChangedKeys m_snapshotKeys = new();
    private readonly Dictionary<Principal, List<WorldDeliveryCursor>> m_deliveryCursors = [];
    private readonly Dictionary<Principal, WorldRecipientView> m_readViews = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldAddonRow, WorldRecipientView> m_addonReadViews = new();

    /// <summary>The operator's disclosure work and shared storage measurement.</summary>
    public string DescribeDisclosureBudget() => m_disclosureImages.Describe();
    /// <summary>Gets the <c>world.disclosure</c> counters: the shared image table's monotonic composition work.</summary>
    public Puck.Abstractions.Counting.IWorkCounterSource DisclosureWorkSource => m_disclosureImages.Work;

    internal WorldRecipientView DeliveryView(Principal? recipient, WorldDisclosureTier tier, Func<bool>? observes = null) =>
        new(() => Definition, () => Arena, recipient, tier, observes ?? (() => true), m_disclosureImages,
            AuthorityIdentity, () => DeliveryTime);

    /// <summary>Mints the live capability for this ingress-stamped console invocation.</summary>
    public WorldRecipientView ReadView(CommandContext context) => ReadView(context.Principal);

    /// <summary>Mints the live capability for a mounted addon's host-side identity.</summary>
    public WorldRecipientView ReadAddonView(string name) => ReadView(Principal.Addon(name));

    internal WorldRecipientView ReadView(Principal? recipient) {
        if (recipient is { Kind: PrincipalKind.Session } session) {
            return m_sessionSinks.TryGetValue(session, out var attached)
                ? attached.Observation.View
                : DeliveryView(session, WorldDisclosureTier.Frames, () => false);
        }
        if (recipient is { Kind: PrincipalKind.Addon } addon &&
            WorldDefinitionRows.FindAddon(Definition.Addons, addon.Name!) is { } declaration) {
            if (!m_addonReadViews.TryGetValue(declaration, out var addonView)) {
                addonView = DeliveryView(addon, WorldDisclosureTier.Replica);
                m_addonReadViews.AddOrUpdate(declaration, addonView);
            }
            return addonView;
        }
        if (recipient is not { } actor || actor.Kind is not (PrincipalKind.Seat or PrincipalKind.Console or PrincipalKind.World)) {
            return DeliveryView(recipient, WorldDisclosureTier.Replica);
        }
        if (!m_readViews.TryGetValue(actor, out var view)) {
            m_readViews[actor] = view = DeliveryView(actor, WorldDisclosureTier.Replica);
        }
        return view;
    }

    internal void AttachCursor(Principal? recipient, WorldDeliveryCursor cursor) {
        if (recipient is not { } actor) { return; }
        if (!m_deliveryCursors.TryGetValue(actor, out var streams)) { m_deliveryCursors[actor] = streams = []; }
        if (!streams.Contains(cursor)) { streams.Add(cursor); }
    }
    internal void DetachCursor(Principal? recipient, WorldDeliveryCursor cursor) {
        if (recipient is not { } actor || !m_deliveryCursors.TryGetValue(actor, out var streams)) { return; }
        streams.Remove(cursor);
        if (streams.Count == 0) { m_deliveryCursors.Remove(actor); }
    }
    internal WorldDocumentVersion RecipientVersion(Principal recipient) {
        if (recipient.IsAuthority) { return DocumentVersion; }
        // A submission with no unambiguous observation stream has received no version. Never substitute an
        // authority install count, or another stream's potentially higher-fidelity delivery count.
        var sequence = m_deliveryCursors.TryGetValue(recipient, out var streams) && streams.Count == 1 ? streams[0].Sequence : 0;
        return DocumentVersion with { Sequence = sequence };
    }
}

/// <summary>Adapts the view's row replacements to the existing snapshot carrier. Both sinks use this one path.</summary>
internal sealed class WorldDeliveryRows {
    private readonly List<FieldCellDelta> m_cells = [];

    internal ReadOnlyMemory<FieldCellDelta> Advance(WorldRecipientView view, WorldDeliveryCursor cursor,
        ReadOnlyMemory<FieldCellDelta> changes, bool rows) {
        m_cells.Clear();
        var sequence = cursor.Sequence;
        var changed = new HashSet<int>();
        foreach (var delta in changes.Span) {
            if (delta.Row >= 0) {
                changed.Add(delta.Row);
                if (view.ReadsWhole(delta.Row)) { m_cells.Add(delta); }
                continue;
            }
            if (delta.Cell >= 0 && view.Discloses(delta.FieldName, IndexKeyCache.Get(delta.Cell))) { m_cells.Add(delta); }
        }
        if (rows) {
            foreach (var replacement in view.Advance(cursor, changed.Order().ToArray())) {
                var image = replacement.Image;
                // A row becoming wholly readable replaces its prior restricted image once, even when the raw
                // carrier also included that row's writes.
                m_cells.RemoveAll(delta => delta.Row == replacement.Row);
                m_cells.Add(new FieldCellDelta(-1, 0, image.Cells.Count, replacement.Row,
                    Observation: view.ReadsWhole(replacement.Row) ? null : image.Observation));
                for (var position = 0; position < image.Cells.Count; position++) {
                    var cell = image.Cells[position];
                    m_cells.Add(new FieldCellDelta(position, 0, 0, replacement.Row,
                        new ArenaCellImage(cell.Key, cell.Value, cell.Behavior, cell.Clock)));
                }
            }
        }
        if (rows && m_cells.Count != 0 && cursor.Sequence == sequence) { cursor.DeliveredRows(view); }
        return m_cells.ToArray();
    }
}
