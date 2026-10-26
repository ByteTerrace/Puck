using System.Buffers.Binary;
using Puck.Scripting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    private struct BoundCell(GrantSubject subject, CellName name) {
        public GrantSubject Subject = subject;
        public CellName Name = name;

        public bool Resolved;
        public StateHandle Row;
        public CellKey Key;
    }

    private AddonVerdict BindCells(MountedAddon addon, in AddonQuerySubmission query) {
        if ((query.HandleIndex != 0) || (query.HandleGeneration != 0) || (query.C != 0) || (query.B is < 1 or > 4096)) {
            return AddonVerdict.MalformedPayload;
        }
        if ((addon.CellGeneration == ushort.MaxValue) || !ChargeHostFuel(addon: addon, cost: (((ulong)query.B) * 128))) {
            return AddonVerdict.QuotaExhausted;
        }
        // Copy all declarations before decoding. Guest memory is bounded and never retained by the host.
        var bytes = new byte[(((int)query.B) * 16)];

        if (!addon.Instance.TryCopyMemory(query.A, bytes.Length, bytes, out _)) {
            return AddonVerdict.MalformedPayload;
        }
        var cells = new BoundCell[((int)query.B)];

        for (var i = 0; (i < cells.Length); i++) {
            var entry = bytes.AsSpan(length: 16, start: (i * 16));

            if (BinaryPrimitives.ReadUInt32LittleEndian(source: entry[12..]) != 0) {
                return AddonVerdict.MalformedPayload;
            }
            if (!TryReadUtf8(addon, BinaryPrimitives.ReadUInt32LittleEndian(source: entry), BinaryPrimitives.ReadUInt16LittleEndian(source: entry[8..]), 244, out var row, out var refusal) ||
                !TryReadUtf8(addon, BinaryPrimitives.ReadUInt32LittleEndian(source: entry[4..]), BinaryPrimitives.ReadUInt16LittleEndian(source: entry[10..]), 128, out var key, out refusal)) {
                return refusal;
            }
            if (!SafeName.TryParse(candidate: row, name: out _, reason: out _) || !CellName.TryParse(candidate: key, name: out var name, reason: out _)) {
                return AddonVerdict.MalformedPayload;
            }
            cells[i] = new BoundCell(GrantSubject.State(name: row), name);
        }
        addon.Cells = cells;
        addon.CellGeneration++;
        return AddonVerdict.Applied;
    }
    // The validated batch is reused in place. Receipt-pinned replay derives these writes again before rules.
    private void ApplyCellWrites(MountedAddon addon, ulong tick) {
        var hasWrites = false;
        foreach (ref readonly var query in addon.Pump.Queries) {
            hasWrites |= query.Verb is AddonAbi.RequestVerbs.WriteStateCell or AddonAbi.RequestVerbs.WriteStateText;
        }
        var mark = hasWrites ? m_server.BeginStateTransaction() : -1;
        var accepted = true;
        var replies = 0;
        if (m_cellReplies.Length < addon.Pump.Queries.Length) { m_cellReplies = new (ushort Ordinal, AddonVerdict Verdict)[addon.Pump.Queries.Length]; }
        foreach (ref readonly var query in addon.Pump.Queries) {
            if (query.Verb == AddonAbi.RequestVerbs.BindStateCell) {
                var verdict = BindCells(addon: addon, query: in query);

                QueueAnswer(addon, query.Ordinal, verdict, 0, addon.CellGeneration);
            } else if (query.Verb is AddonAbi.RequestVerbs.WriteStateCell or AddonAbi.RequestVerbs.WriteStateText) {
                var verdict = ApplyCellWrite(addon: addon, query: in query);
                accepted &= verdict == AddonVerdict.Applied;
                m_cellReplies[replies++] = (query.Ordinal, verdict);
            }
        }
        if (hasWrites) {
            if (!accepted) { m_server.RewindStateTransaction(mark: mark); }
            else { accepted = m_server.TryCommitStateTransaction(mark: mark, principal: addon.Principal, reason: out _); }
        }
        for (var i = 0; i < replies; i++) {
            var reply = m_cellReplies[i];
            QueueAnswer(addon, reply.Ordinal, !accepted && reply.Verdict == AddonVerdict.Applied ? AddonVerdict.Rejected : reply.Verdict);
        }
    }
    private (ushort Ordinal, AddonVerdict Verdict)[] m_cellReplies = [];
    private AddonVerdict ApplyCellWrite(MountedAddon addon, in AddonQuerySubmission query) {
        if ((query.HandleGeneration != addon.CellGeneration) || (query.HandleIndex >= addon.Cells.Length)) {
            return AddonVerdict.StaleHandle;
        }
        ref var cell = ref addon.Cells[query.HandleIndex];
        var subject = cell.Subject;

        if (!IsRequested(addon: addon, capability: WorldCapability.Edit, subject: subject)) {
            return AddonVerdict.AttenuatedToEmpty;
        }
        var authority = m_server.Grants.Allows(addon.Principal, WorldCapability.Edit, subject);

        if (!authority.IsAllowed) {
            return WorldAddonWire.FromRule(rule: authority.Rule);
        }
        var text = (query.Verb == AddonAbi.RequestVerbs.WriteStateText);
        var write = (text ? WorldDocumentWriteKind.Set : (WorldDocumentWriteKind)query.B);

        if ((query.C != 0) || (!text && (query.B is not (0 or 1)))) {
            return AddonVerdict.MalformedPayload;
        }
        if (!ChargeHostFuel(addon: addon, cost: 64)) {
            return AddonVerdict.QuotaExhausted;
        }
        var arena = m_server.Arena;

        if (addon.CellsArena is not { } cachedArena) {
            addon.CellsArena = new WeakReference<StateArena>(target: arena);
        } else if (!cachedArena.TryGetTarget(target: out var previousArena) || !ReferenceEquals(objA: arena, objB: previousArena)) {
            cachedArena.SetTarget(target: arena);
            foreach (ref var bound in addon.Cells.AsSpan()) {
                bound.Resolved = false;
                bound.Row = default;
                bound.Key = default;
            }
        }

        // Resolution caches the address, never the disclosure: a cell hidden from the addon refuses as an absent one does.
        if (!IsDisclosed(addon: addon, subject: subject, name: cell.Name)) {
            return AddonVerdict.NoSuchSubject;
        }
        if (!cell.Resolved) {
            if (!arena.Catalog.TryResolve(StateLane.Document, subject.Id!, out cell.Row) ||
                !arena.Keys.TryResolve(key: out cell.Key, name: cell.Name) || !arena.TryRead(cell.Row.Ordinal, cell.Key, out _)) {
                return AddonVerdict.NoSuchSubject;
            }
            cell.Resolved = true;
        }
        var value = "";

        if (text && !TryReadUtf8(addon, query.A, query.B, (StateCapacity.MaxTextValueLength * 4), out value, out var refusal)) {
            return refusal;
        }
        return m_server.WriteStateCell(addon.Principal, cell.Row, cell.Key, query.A, write, (text ? value : null)) switch {
            WorldCellWriteVerdict.Applied => AddonVerdict.Applied,
            WorldCellWriteVerdict.StaleAddress => AddonVerdict.StaleHandle,
            WorldCellWriteVerdict.Denied => AddonVerdict.NoHold,
            WorldCellWriteVerdict.Masked => AddonVerdict.AttenuatedToEmpty,
            WorldCellWriteVerdict.BudgetExhausted => AddonVerdict.QuotaExhausted,
            _ => AddonVerdict.Rejected,
        };
    }
}
