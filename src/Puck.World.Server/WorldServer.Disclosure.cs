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

    // The operator's replica view, which reads every row whole: the whole reader a session's scene is compared with.
    private WorldRecipientView? m_wholeView;
    // The scene identity a whole reader of this world is handed now (WorldRecipientView.SceneIdentity), computed once
    // per audience while nothing it reads moves.
    internal Puck.Assets.ContentPin WholeSceneIdentity() => (m_wholeView ??= DeliveryView(Principal.Console, WorldDisclosureTier.Replica)).SceneIdentity!.Value;

    internal WorldRecipientView DeliveryView(Principal? recipient, WorldDisclosureTier tier, Func<bool>? observes = null) =>
        new(() => Definition, () => Arena, recipient, tier, observes ?? (() => true), m_disclosureImages,
            AuthorityIdentity, () => DeliveryTime, () => recipient is { } actor && Grants.IsOperator(actor));

    /// <summary>Mints the live capability this ingress-stamped console invocation reads through.</summary>
    /// <param name="context">The invocation's context, carrying its stamped principal.</param>
    /// <returns>The caller's view at its admission ceiling.</returns>
    /// <exception cref="InvalidOperationException">The caller is a session whose state-observation query is refused:
    /// it is admitted at <see cref="WorldDisclosureTier.Frames"/>, its observation ended or its epoch is retired, or
    /// it holds no observe all.</exception>
    public WorldRecipientView ReadView(CommandContext context) => ReadView(context.Principal);

    /// <summary>Mints the live capability for a mounted addon's host-side identity.</summary>
    public WorldRecipientView ReadAddonView(string name) => PolicyView(Principal.Addon(name));

    // The read door. A session reads only while its state-observation query would be answered: its observation is
    // attached and live, its tier carries a document and it holds observe all. Frames, an ended or retired epoch and
    // a revoked grant refuse here, the same decision the query door makes, so no read answers a session that the
    // query door refuses, and an admitted session reads at its own delivery ceiling.
    internal WorldRecipientView ReadView(Principal? recipient) {
        if (recipient is { Kind: PrincipalKind.Session } session) {
            if (!AllowsSessionQuery(session, StateObservationsQuery) || !m_sessionSinks.TryGetValue(session, out var attached)) {
                throw new InvalidOperationException(WorldSessionObservation.QueryRefusal);
            }
            return attached.Observation.View;
        }
        return PolicyView(recipient);
    }
    private static readonly WorldQuery StateObservationsQuery = new WorldQuery.StateObservations();

    // The write door: the capability every recipient write, guard and refusal is decided through (complete admission,
    // live state commands, typed cell writes). A write is judged by the writer's declared policy alone, so the door
    // mints the policy's own tier for every principal: never a session's delivery ceiling or its live observation,
    // which restrict what its stream carries, not what it may write. Whether a session may act at all is the
    // submission door's (a stale epoch is refused before admission), and what it may write is its grants'.
    internal WorldRecipientView WriteView(Principal? recipient) => PolicyView(recipient);

    internal StateReadAccess ReadForWrite(WorldRecipientView view) {
        var arena = Arena;
        return view.IsAuthority ? arena.ReadAccess : arena.RestrictReads(ordinal => view.Admits(arena.Catalog.Descriptors[ordinal].Name));
    }

    // The capability at the policy's own tier: Replica, always observing. Seats, the console, the world and mounted
    // addons keep theirs; any other principal is minted one per use.
    private WorldRecipientView PolicyView(Principal? recipient) {
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
        if (Grants.IsOperator(recipient)) { return DocumentVersion; }
        // A submission with no unambiguous observation stream has received no version. Never substitute an
        // authority install count, or another stream's potentially higher-fidelity delivery count.
        var sequence = m_deliveryCursors.TryGetValue(recipient, out var streams) && streams.Count == 1 ? streams[0].Sequence : 0;
        return DocumentVersion with { Sequence = sequence };
    }
}

/// <summary>Adapts the view's row replacements to the existing snapshot carrier. Both sinks use this one path.</summary>
/// <remarks>The cells <see cref="Advance"/> returns are borrowed for the delivery in hand: this carrier's scratch, which
/// its next call overwrites, or, when every cell is disclosed and no row is replaced, the incoming carrier itself, as the
/// authority's own snapshot cells are borrowed (<see cref="WorldSnapshot.FieldCells"/>). A sink hands them on within the
/// delivery, and whatever applies, encodes or samples them copies what it keeps.</remarks>
internal sealed class WorldDeliveryRows {
    private FieldCellDelta[] m_cells = [];
    private int m_count;
    private readonly Puck.Abstractions.Collections.MarkedIndexSet m_changed = new();
    private int[] m_changedRows = [];

    internal ReadOnlyMemory<FieldCellDelta> Advance(WorldRecipientView view, WorldDeliveryCursor cursor,
        ReadOnlyMemory<FieldCellDelta> changes, bool rows) {
        m_count = 0;
        m_changed.Clear();
        var sequence = cursor.Sequence;
        var span = changes.Span;
        // The view's answers stand for the whole call, so consecutive cells of one row or field share one answer, and
        // a field or row the recipient reads whole discloses every cell without a per-cell check.
        var wholeRow = -1;
        var wholeRowReads = false;
        string? wholeField = null;
        var wholeFieldReads = false;
        // The disclosed cells keep their order; each unbroken run of them is copied at once.
        var run = 0;
        for (var index = 0; index < span.Length; index++) {
            ref readonly var delta = ref span[index];
            bool discloses;
            if (delta.Row >= 0) {
                m_changed.Add(index: delta.Row);
                if (delta.Row != wholeRow) { wholeRow = delta.Row; wholeRowReads = view.ReadsWhole(delta.Row); }
                discloses = wholeRowReads;
            } else if (delta.Cell < 0) {
                discloses = false;
            } else {
                if (!string.Equals(delta.FieldName, wholeField, StringComparison.Ordinal)) { wholeField = delta.FieldName; wholeFieldReads = view.ReadsWhole(delta.FieldName); }
                discloses = (wholeFieldReads || view.Discloses(delta.FieldName, IndexKeyCache.Get(delta.Cell)));
            }
            if (!discloses) {
                AppendRun(span[run..index]);
                run = (index + 1);
            }
        }
        // Every cell disclosed and nothing to replace: the carrier is handed on as it came.
        var passThrough = (run == 0);
        if (!passThrough) { AppendRun(span[run..]); }
        if (rows) {
            var changedCount = m_changed.Count;
            if (m_changedRows.Length < changedCount) { m_changedRows = new int[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)changedCount)]; }
            m_changed.Members.CopyTo(m_changedRows);
            var changedRows = m_changedRows.AsSpan(0, changedCount);
            changedRows.Sort();
            foreach (var replacement in view.Advance(cursor, changedRows)) {
                var image = replacement.Image;
                if (passThrough) { AppendRun(span); passThrough = false; }
                // A row becoming wholly readable replaces its prior restricted image once, even when the raw
                // carrier also included that row's writes.
                RemoveRow(replacement.Row);
                Append(new FieldCellDelta(-1, 0, image.Cells.Count, replacement.Row,
                    Observation: view.ReadsWhole(replacement.Row) ? null : image.Observation));
                for (var position = 0; position < image.Cells.Count; position++) {
                    var cell = image.Cells[position];
                    Append(new FieldCellDelta(position, 0, 0, replacement.Row,
                        new ArenaCellImage(cell.Key, cell.Value, cell.Behavior, cell.Clock)));
                }
            }
        }
        var delivered = (passThrough ? changes : m_cells.AsMemory(0, m_count));
        if (rows && !delivered.IsEmpty && cursor.Sequence == sequence) { cursor.DeliveredRows(view); }
        return delivered;
    }
    private void Append(in FieldCellDelta delta) {
        if (m_count == m_cells.Length) { Array.Resize(ref m_cells, Math.Max(16, m_cells.Length * 2)); }
        m_cells[m_count++] = delta;
    }
    private void AppendRun(ReadOnlySpan<FieldCellDelta> cells) {
        if (cells.IsEmpty) { return; }
        if ((m_count + cells.Length) > m_cells.Length) {
            Array.Resize(ref m_cells, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, m_count + cells.Length)));
        }
        cells.CopyTo(m_cells.AsSpan(m_count));
        m_count += cells.Length;
    }
    // Drops the row's cells already carried, keeping the rest in order.
    private void RemoveRow(int row) {
        var kept = 0;
        for (var index = 0; index < m_count; index++) {
            if (m_cells[index].Row != row) { m_cells[kept++] = m_cells[index]; }
        }
        Array.Clear(m_cells, kept, m_count - kept);
        m_count = kept;
    }
}
