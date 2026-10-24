using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Maths;
using Puck.Scripting;
using Puck.World.Protocol;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    private bool ChargeHost(MountedAddon addon, ulong cost) {
        var allowed = addon.Instance.TryChargeHostFuel(cost, out var spent);
        addon.LastTickFuelConsumed = addon.LastTickFuelConsumed.AddSaturating(spent);
        addon.PeakTickFuelConsumed = Math.Max(addon.PeakTickFuelConsumed, addon.LastTickFuelConsumed);
        addon.TotalFuelConsumed = addon.TotalFuelConsumed.AddSaturating(spent);
        if (!allowed) { ReportFault(addon); }
        return allowed;
    }

    private bool QueryAuthority(MountedAddon addon, in AddonQuerySubmission query, WorldCapability capability,
        out GrantSubject subject, out AddonVerdict verdict) {
        var grants = m_server.Grants;
        var handle = new WorldHandle(query.HandleIndex, query.HandleGeneration, addon.Principal, capability);
        if (!grants.HandleTable(addon.Principal, capability).TryResolve(handle, out subject)) {
            verdict = AddonVerdict.StaleHandle;
            return false;
        }
        if (!IsRequested(addon, capability, subject)) {
            verdict = AddonVerdict.AttenuatedToEmpty;
            return false;
        }
        var allowed = grants.Allows(addon.Principal, capability, subject);
        verdict = WorldAddonWire.FromRule(allowed.Rule);
        if (!allowed.IsAllowed) { return false; }
        // Control's pre-existing grant contract is unmetered. The ABI has a fixed cell cap and every
        // control operation pays host fuel before work. Observe/Drive additionally spend their row budget.
        if (capability != WorldCapability.Control) {
            var key = (capability, subject);
            var bodyDrive = capability == WorldCapability.Drive && subject.Kind == GrantSubjectKind.Body;
            var count = bodyDrive ? addon.DriveDispatchCounts[subject.Value] : addon.QueryCounts.GetValueOrDefault(key);
            if (!grants.TryGetBudget(addon.Principal, capability, subject, out var budget) || count >= budget) {
                verdict = AddonVerdict.QuotaExhausted;
                return false;
            }
            if (bodyDrive) { addon.DriveDispatchCounts[subject.Value] = count + 1; }
            else { addon.QueryCounts[key] = count + 1; }
        }
        return true;
    }

    private static bool CopyText(MountedAddon addon, long pointer, long length, int limit, out string text) {
        text = "";
        if (length < 0 || length > limit || !addon.Instance.TryCopyMemory(pointer, length, addon.ReadBuffer, out _)) {
            return false;
        }
        try { text = new UTF8Encoding(false, true).GetString(addon.ReadBuffer, 0, (int)length); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private void ResolveDebuggerQuery(MountedAddon addon, in AddonQuerySubmission query, ulong tick) {
        if (addon.Instance.State != AddonState.Enabled || !ChargeHost(addon, 128)) { return; }
        var capability = query.Verb switch {
            AddonAbi.RequestVerbs.MachineSnapshot or AddonAbi.RequestVerbs.MachineOperation => WorldCapability.Control,
            AddonAbi.RequestVerbs.Engage => WorldCapability.Drive,
            _ => WorldCapability.Observe,
        };
        if (!QueryAuthority(addon, in query, capability, out var subject, out var verdict)) {
            QueueAnswer(addon, query.Ordinal, verdict);
            return;
        }
        var a = 0L;
        var b = 0L;
        verdict = AddonVerdict.Rejected;
        try {
            switch (query.Verb) {
                case AddonAbi.RequestVerbs.Log:
                    if (subject.Kind == GrantSubjectKind.State && query.C == 0 && CopyText(addon, query.A, query.B, 2048, out var line) &&
                        ChargeHost(addon, (ulong)query.B)) {
                        Console.Error.WriteLine($"[world.addon: {addon.Instance.Name} t={tick} guest: {line.Replace('\n', ' ').Replace('\r', ' ')}]");
                        verdict = AddonVerdict.Applied;
                    }
                    break;
                case AddonAbi.RequestVerbs.StateCell:
                    if (subject.Kind != GrantSubjectKind.State || query.C < -1 || query.C > 4096 ||
                        !CopyText(addon, query.A, query.B, 128, out var keyName)) { break; }
                    var arena = m_server.Arena;
                    if (!arena.Catalog.TryResolve(StateLane.Document, subject.Id!, out var row) ||
                        !arena.Keys.TryResolve(CellName.Parse(keyName), out var key) ||
                        !arena.TryReadLive(row.Ordinal, key, m_server.DeliveryTime, out var cell)) { break; }
                    if (query.C == -1 && cell.Kind is CellKind.Int or CellKind.Fixed or CellKind.Bool) {
                        a = cell.Raw; b = (long)cell.Kind; verdict = AddonVerdict.Applied;
                    } else if (query.C >= 0 && cell.Kind == CellKind.Text) {
                        if (cell.AsText.Length > 4096 || !ChargeHost(addon, (ulong)cell.AsText.Length * 3)) { break; }
                        var byteCount = Encoding.UTF8.GetBytes(cell.AsText, addon.ReadBuffer);
                        var text = addon.ReadBuffer.AsSpan(0, byteCount);
                        Span<byte> chunk = stackalloc byte[16];
                        chunk.Clear();
                        if (query.C < text.Length) {
                            text.Slice((int)query.C, Math.Min(16, text.Length - (int)query.C)).CopyTo(chunk);
                        }
                        a = BinaryPrimitives.ReadInt64LittleEndian(chunk);
                        b = BinaryPrimitives.ReadInt64LittleEndian(chunk[8..]);
                        verdict = AddonVerdict.Applied;
                    }
                    break;
                case AddonAbi.RequestVerbs.Engage:
                    if (subject.Kind != GrantSubjectKind.Body || query.B != 0 || query.C != 0 ||
                        query.A is < -1 or > int.MaxValue) { break; }
                    var target = GrantSubject.Screen((int)query.A);
                    if (query.A >= 0 && (!IsRequested(addon, WorldCapability.Control, target) ||
                        !m_server.Machines.HasMachine((int)query.A) ||
                        !m_server.CheckEngagePolicy(subject.Value, target, out _))) { break; }
                    // A dissolve may touch several applications; the manifest must cover each, like the grant check.
                    if (query.A == -1 && !CanDissolve(addon, subject.Value)) { break; }
                    if (m_server.Engagement.EngageDrivenBody(subject.Value, (int)query.A, addon.Principal)) {
                        verdict = AddonVerdict.Applied;
                    }
                    break;
                case AddonAbi.RequestVerbs.MachineRead:
                case AddonAbi.RequestVerbs.MachineHash:
                    if (subject.Kind != GrantSubjectKind.Screen || query.C != 0 || query.A is < 0 or > int.MaxValue ||
                        query.B < 1 || query.B > (query.Verb == AddonAbi.RequestVerbs.MachineRead ? 8 : 262144) ||
                        query.A + query.B > (long)int.MaxValue + 1 || !ChargeHost(addon, (ulong)query.B) ||
                        m_server.Machines.MachineAt(subject.Value) is not IMachineMemoryPeek memory) { break; }
                    var bytes = addon.ReadBuffer.AsSpan(0, (int)query.B);
                    memory.PeekBytes((int)query.A, bytes);
                    if (query.Verb == AddonAbi.RequestVerbs.MachineRead) {
                        ulong value = 0;
                        for (var i = 0; i < bytes.Length; i++) { value |= (ulong)bytes[i] << (i * 8); }
                        a = unchecked((long)value);
                    } else {
                        a = unchecked((long)Fnv1aHash.Compute(bytes));
                    }
                    b = m_server.Machines.MachineAt(subject.Value) is IMachineFrameProgress progress
                        ? progress.NativeFrameIndex : -1;
                    verdict = AddonVerdict.Applied;
                    break;
                case AddonAbi.RequestVerbs.MachineStatus:
                    if (subject.Kind == GrantSubjectKind.Screen && query.A == 0 && query.B == 0 && query.C == 0 &&
                        m_server.Machines.MachineAt(subject.Value) is { } statusRuntime) {
                        a = (long)statusRuntime.Status;
                        b = m_server.Machines.State(subject.Value)?.FramesStepped ?? 0;
                        verdict = AddonVerdict.Applied;
                    }
                    break;
                case AddonAbi.RequestVerbs.MachineSnapshot:
                    if (subject.Kind != GrantSubjectKind.Screen || query.A is < 0 or > 2 || query.B != 0 || query.C != 0 ||
                        !ChargeHost(addon, 300000)) { break; }
                    var runtime = m_server.Machines.MachineAt(subject.Value);
                    if (query.A == 2) {
                        addon.MachineSnapshot = null; addon.SnapshotRuntime = null; verdict = AddonVerdict.Applied;
                    } else if (runtime is IMachineCheckpointRuntime checkpoint) {
                        if (query.A == 0) {
                            var snapshot = checkpoint.CaptureCheckpoint();
                            if (snapshot.Length > 4 * 1024 * 1024) { break; }
                            addon.MachineSnapshot = snapshot; addon.SnapshotRuntime = runtime;
                            a = snapshot.Length; verdict = AddonVerdict.Applied;
                        } else if (ReferenceEquals(runtime, addon.SnapshotRuntime) && addon.MachineSnapshot is { } saved) {
                            checkpoint.RestoreCheckpoint(saved); a = saved.Length; verdict = AddonVerdict.Applied;
                        }
                    }
                    break;
                case AddonAbi.RequestVerbs.MachineOperation:
                    if (subject.Kind != GrantSubjectKind.Screen || query.C != 0 ||
                        !CopyText(addon, query.A, query.B, 2048, out var json) || !ChargeHost(addon, 300000)) { break; }
                    var screen = m_server.Definition.Screens.FirstOrDefault(s => s.Index == subject.Value);
                    if (screen?.Source is not WorldScreenSource.Machine machine ||
                        !IsRequested(addon, WorldCapability.Control, GrantSubject.Machine(machine.Instance)) ||
                        m_server.Machines.InstanceState(machine.Instance) is not { } state) { break; }
                    using (var document = JsonDocument.Parse(json)) {
                        var root = document.RootElement;
                        if (!root.TryGetProperty("operation", out var op) || !root.TryGetProperty("payload", out var payload) ||
                            op.ValueKind != JsonValueKind.String) { break; }
                        var result = m_server.ApplyMachineOperation(new WorldMachineOperation(machine.Instance,
                            state.Generation, op.GetString()!, payload), addon.Principal, 0, 0, recomputed: true);
                        a = (long)result.Status;
                        verdict = result.Status == MachineOperationStatus.Applied ? AddonVerdict.Applied : AddonVerdict.Rejected;
                    }
                    break;
            }
        } catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException or NotSupportedException) {
            // Unsupported snapshots, invalid identity and provider refusals are local request results.
            Console.Error.WriteLine($"[world.addon: {addon.Instance.Name} t={tick} request {query.Verb} refused: {error.Message}]");
        }
        QueuePart(addon, query.Ordinal, verdict, 0, a, b);
    }

    private bool CanDissolve(MountedAddon addon, int bodyIndex) {
        var principal = bodyIndex < m_server.Population.LocalSeatCount
            ? Puck.Commands.Principal.Seat(bodyIndex) : m_server.Population.PeerPrincipal(bodyIndex);
        foreach (var application in m_server.Grants.Applications(principal)) {
            if (application.Target == GrantSubject.Body(bodyIndex)) { continue; }
            if (!IsRequested(addon, WorldCapability.Control, application.Target)) { return false; }
        }
        return true;
    }
}
