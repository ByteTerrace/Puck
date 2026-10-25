using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Maths;
using Puck.Scripting;
using Puck.World.Protocol;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    private bool QueryAuthority(MountedAddon addon, in AddonQuerySubmission query, WorldCapability capability,
        out GrantSubject subject, out AddonVerdict verdict) {
        var grants = m_server.Grants;
        var handle = new WorldHandle(query.HandleIndex, query.HandleGeneration, addon.Principal, capability);

        if (!grants.HandleTable(addon.Principal, capability).TryResolve(handle: handle, subject: out subject)) {
            verdict = AddonVerdict.StaleHandle;
            return false;
        }
        if (!IsRequested(addon: addon, capability: capability, subject: subject)) {
            verdict = AddonVerdict.AttenuatedToEmpty;
            return false;
        }
        var allowed = grants.Allows(addon.Principal, capability, subject);

        verdict = WorldAddonWire.FromRule(rule: allowed.Rule);
        if (!allowed.IsAllowed) { return false; }
        // Control's pre-existing grant contract is unmetered. The ABI has a fixed cell cap and every
        // control operation pays host fuel before work. Observe/Drive additionally spend their row budget.
        if (capability != WorldCapability.Control) {
            var key = (capability, subject);
            var bodyDrive = ((capability == WorldCapability.Drive) && (subject.Kind == GrantSubjectKind.Body));
            var bodyRead = ((capability == WorldCapability.Observe) && (subject.Kind == GrantSubjectKind.Body));
            var stateRead = ((capability == WorldCapability.Observe) && (subject.Kind == GrantSubjectKind.State));
            var count = (bodyDrive ? addon.DriveDispatchCounts[subject.Value] : (bodyRead ? addon.DispatchCounts[subject.Value] : (stateRead ? addon.StateReadCounts.GetValueOrDefault(key: subject) : addon.QueryCounts.GetValueOrDefault(key: key))));

            if (!grants.TryGetBudget(addon.Principal, capability, subject, out var budget) || (count >= budget)) {
                verdict = AddonVerdict.QuotaExhausted;
                return false;
            }
            if (bodyDrive) { addon.DriveDispatchCounts[subject.Value] = (count + 1); } else if (bodyRead) { addon.DispatchCounts[subject.Value] = (count + 1); } else if (stateRead) { addon.StateReadCounts[subject] = (count + 1); } else { addon.QueryCounts[key] = (count + 1); }
        }
        return true;
    }
    private void ResolveDebuggerQuery(MountedAddon addon, in AddonQuerySubmission query, ulong tick) {
        if ((addon.Instance.State != AddonState.Enabled) || !ChargeHostFuel(addon: addon, cost: 128)) { return; }
        var capability = query.Verb switch {
            AddonAbi.RequestVerbs.MachineSnapshot or AddonAbi.RequestVerbs.MachineOperation => WorldCapability.Control,
            AddonAbi.RequestVerbs.Engage => WorldCapability.Drive,
            _ => WorldCapability.Observe,
        };

        if (!QueryAuthority(addon: addon, capability: capability, query: in query, subject: out var subject, verdict: out var verdict)) {
            QueueAnswer(addon, query.Ordinal, verdict);
            return;
        }
        var a = 0L;
        var b = 0L;

        verdict = AddonVerdict.Rejected;
        try {
            switch (query.Verb) {
                case AddonAbi.RequestVerbs.ReadStateText:
                    if ((subject.Kind != GrantSubjectKind.State) || (query.C < 0) || (query.C > 4096) ||
                        !TryReadUtf8(addon, query.A, query.B, 128, out var keyName, out _)) { break; }
                    if (!TryReadDisclosed(addon: addon, subject: subject, name: CellName.Parse(candidate: keyName), value: out var cell)) { break; }
                    if (cell.Kind == CellKind.Text) {
                        if ((cell.AsText.Length > 4096) || !ChargeHostFuel(addon: addon, cost: (((ulong)cell.AsText.Length) * 3))) { break; }
                        var text = addon.ReadBuffer(length: Encoding.UTF8.GetByteCount(s: cell.AsText));

                        Encoding.UTF8.GetBytes(cell.AsText, text);
                        Span<byte> chunk = stackalloc byte[16];

                        chunk.Clear();
                        if (query.C < text.Length) {
                            text.Slice(((int)query.C), Math.Min(val1: 16, val2: (text.Length - ((int)query.C)))).CopyTo(destination: chunk);
                        }
                        a = BinaryPrimitives.ReadInt64LittleEndian(source: chunk);
                        b = BinaryPrimitives.ReadInt64LittleEndian(source: chunk[8..]);
                        verdict = AddonVerdict.Applied;
                    }
                    break;
                case AddonAbi.RequestVerbs.Engage:
                    if ((subject.Kind != GrantSubjectKind.Body) || (query.B != 0) || (query.C != 0) ||
                        (query.A is < -1 or > int.MaxValue)) { break; }
                    // IsRequested only narrows to what the manifest asked for and confers nothing. Authority is the Drive
                    // grant over the body (QueryAuthority, rechecked by EngageDrivenBody), CheckEngagePolicy (engageable,
                    // in reach, a machine to control) and the engage check Compose and ResolveDissolve make over each target.
                    var target = GrantSubject.Screen(index: ((int)query.A));
                    if ((query.A >= 0) && (!IsRequested(addon: addon, capability: WorldCapability.Control, subject: target) ||
                        !m_server.Machines.HasMachine(index: ((int)query.A)) ||
                        !m_server.CheckEngagePolicy(subject.Value, target, out _))) { break; }
                    // A dissolve may touch several applications; the manifest must request each. Whether the addon may
                    // dissolve it is the engage check ResolveDissolve makes, not this request check.
                    if ((query.A == -1) && !CanDissolve(addon: addon, bodyIndex: subject.Value)) { break; }
                    if (m_server.Engagement.EngageDrivenBody(subject.Value, ((int)query.A), addon.Principal)) {
                        verdict = AddonVerdict.Applied;
                    }
                    break;
                case AddonAbi.RequestVerbs.MachineRead:
                case AddonAbi.RequestVerbs.MachineHash:
                    if ((subject.Kind != GrantSubjectKind.Screen) || (query.C != 0) || (query.A is < 0 or > int.MaxValue) ||
                        (query.B < 1) || (query.B > ((query.Verb == AddonAbi.RequestVerbs.MachineRead) ? 8 : 262144)) ||
                        ((query.A + query.B) > (((long)int.MaxValue) + 1)) || !ChargeHostFuel(addon: addon, cost: ((ulong)query.B)) ||
                        (m_server.Machines.MachineAt(index: subject.Value) is not IMachineMemoryPeek memory)) { break; }
                    var bytes = addon.ReadBuffer(length: ((int)query.B));
                    memory.PeekBytes(address: ((int)query.A), destination: bytes);
                    if (query.Verb == AddonAbi.RequestVerbs.MachineRead) {
                        var value = 0UL;

                        for (var i = 0; (i < bytes.Length); i++) { value |= (((ulong)bytes[i]) << (i * 8)); }
                        a = unchecked((long)value);
                    } else {
                        a = unchecked((long)Fnv1aHash.Compute(values: bytes));
                    }
                    b = ((m_server.Machines.MachineAt(index: subject.Value) is IMachineFrameProgress progress)
                        ? progress.NativeFrameIndex : -1);
                    verdict = AddonVerdict.Applied;
                    break;
                case AddonAbi.RequestVerbs.MachineStatus:
                    if ((subject.Kind == GrantSubjectKind.Screen) && (query.A == 0) && (query.B == 0) && (query.C == 0) &&
                        (m_server.Machines.MachineAt(index: subject.Value) is { } statusRuntime)) {
                        a = ((long)statusRuntime.Status);
                        b = (m_server.Machines.State(index: subject.Value)?.FramesStepped ?? 0);
                        verdict = AddonVerdict.Applied;
                    }
                    break;
                case AddonAbi.RequestVerbs.MachineSnapshot:
                    if ((subject.Kind != GrantSubjectKind.Screen) || (query.A is < 0 or > 2) || (query.B != 0) || (query.C != 0) ||
                        !ChargeHostFuel(addon: addon, cost: 300000)) { break; }
                    var runtime = m_server.Machines.MachineAt(index: subject.Value);
                    if (query.A == 2) {
                        addon.MachineSnapshot = null; addon.SnapshotRuntime = null; verdict = AddonVerdict.Applied;
                    } else if (runtime is IMachineCheckpointRuntime checkpoint) {
                        if (query.A == 0) {
                            var snapshot = checkpoint.CaptureCheckpoint();

                            if (snapshot.Length > ((4 * 1024) * 1024)) { break; }
                            addon.MachineSnapshot = snapshot; addon.SnapshotRuntime = runtime;
                            a = snapshot.Length; verdict = AddonVerdict.Applied;
                        } else if (ReferenceEquals(objA: runtime, objB: addon.SnapshotRuntime) && (addon.MachineSnapshot is { } saved)) {
                            checkpoint.RestoreCheckpoint(checkpoint: saved); a = saved.Length; verdict = AddonVerdict.Applied;
                        }
                    }
                    break;
                case AddonAbi.RequestVerbs.MachineOperation:
                    if ((subject.Kind != GrantSubjectKind.Screen) || (query.C != 0) ||
                        !ChargeHostFuel(addon: addon, cost: 300000) || !TryReadUtf8(addon, query.A, query.B, 2048, out var json, out _)) { break; }
                    // The Control handle over the screen passed QueryAuthority's grant check, and the manifest check below
                    // narrows to the instance the guest requested. The authority over the machine itself is the Control
                    // grant ApplyMachineOperation checks for the addon's principal.
                    var screen = m_server.Configuration.Screens.FirstOrDefault(predicate: s => (s.Index == subject.Value));
                    if ((screen?.Source is not WorldScreenSource.Machine machine) ||
                        !IsRequested(addon: addon, capability: WorldCapability.Control, subject: GrantSubject.Machine(name: machine.Instance)) ||
                        (m_server.Machines.InstanceState(name: machine.Instance) is not { } state)) { break; }
                    using (var document = JsonDocument.Parse(json)) {
                        var root = document.RootElement;

                        if (!root.TryGetProperty(propertyName: "operation", value: out var op) || !root.TryGetProperty(propertyName: "payload", value: out var payload) ||
                            (op.ValueKind != JsonValueKind.String)) { break; }
                        var operationId = op.GetString()!;

                        if (root.TryGetProperty(propertyName: "contentName", value: out var contentName)) {
                            var declaration = m_server.Configuration.Machines.FirstOrDefault(predicate: item => (item.Name == machine.Instance));

                            if ((operationId != "content.insert") || (contentName.ValueKind != JsonValueKind.String) ||
                                (payload.ValueKind != JsonValueKind.Object) || (declaration is null) ||
                                !WorldMachineContentFolder.TryList(declaration, m_server.Configuration.DocumentDirectory,
                                    m_server.Machines.ValidationCatalog, out _, out var entries, out var reason)) { break; }
                            if (!WorldMachineContentFolder.TryFind(entries, contentName.GetString()!, out var entry, out reason)) {
                                Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} t={tick} content refused: {reason}]");
                                break;
                            }
                            var tree = System.Text.Json.Nodes.JsonObject.Create(payload)!;

                            tree["content"] = new System.Text.Json.Nodes.JsonObject { ["path"] = entry.ContentPath };
                            payload = MachineOperationValidation.CloneJson(value: tree);
                        }
                        var result = m_server.ApplyMachineOperation(new WorldMachineOperation(machine.Instance,
                            state.Generation, operationId, payload), addon.Principal, 0, 0, addonDerived: true);

                        if (result.Status != MachineOperationStatus.Applied) {
                            Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} t={tick} operation refused: {result.Reason}]");
                        }
                        a = ((long)result.Status);
                        verdict = ((result.Status == MachineOperationStatus.Applied) ? AddonVerdict.Applied : AddonVerdict.Rejected);
                    }
                    break;
            }
        } catch (Exception error) when ((error is InvalidOperationException or ArgumentException or JsonException or NotSupportedException)) {
            // Unsupported snapshots, invalid identity and provider refusals are local request results.
            Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} t={tick} request {query.Verb} refused: {error.Message}]");
        }
        QueuePart(addon, query.Ordinal, verdict, 0, a, b);
    }
    // Whether the manifest requests Control over every application a dissolve would end. This is a request check; the
    // engage check over each target is made by WorldEngagement.ResolveDissolve when the dissolve applies.
    private bool CanDissolve(MountedAddon addon, int bodyIndex) {
        var principal = ((bodyIndex < m_server.Population.LocalSeatCount)
            ? Puck.Commands.Principal.Seat(slot: bodyIndex) : m_server.Population.PeerPrincipal(index: bodyIndex));

        foreach (var application in m_server.Grants.Applications(principal: principal)) {
            if (application.Target == GrantSubject.Body(index: bodyIndex)) { continue; }
            if (!IsRequested(addon: addon, capability: WorldCapability.Control, subject: application.Target)) { return false; }
        }
        return true;
    }
}
