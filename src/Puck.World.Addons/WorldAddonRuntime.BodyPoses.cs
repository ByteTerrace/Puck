using System.Buffers.Binary;
using Puck.Scripting;
using Puck.World.Protocol;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    private void ResolveBodyPoses(MountedAddon addon, in AddonQuerySubmission query, ulong tick) {
        if ((addon.Instance.State != AddonState.Enabled) || !ChargeHostFuel(addon: addon, cost: 128)) { return; }
        if ((query.HandleIndex != 0) || (query.HandleGeneration != 0) ||
            (query.B < 1) || (query.B > AddonAbi.MaxPoseBatchBodies)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.MalformedPayload);
            return;
        }
        var count = ((int)query.B);
        // Prepay copies and per-body authority/read work. Exhaustion publishes no partial result.
        if (!ChargeHostFuel(addon: addon, cost: (((ulong)count) * ((4 + AddonAbi.BodyPoseRecordBytes) + 64)))) { return; }
        var handles = addon.PoseHandles(length: (count * 4));

        if (!addon.Instance.TryCopyMemory(query.A, (count * 4), handles, out _)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.MalformedPayload);
            return;
        }
        var result = addon.ReadBuffer(length: (count * AddonAbi.BodyPoseRecordBytes));

        result.Clear();
        for (var i = 0; (i < count); i++) {
            var handle = handles.Slice(length: 4, start: (i * 4));
            var read = query with {
                HandleIndex = BinaryPrimitives.ReadUInt16LittleEndian(source: handle),
                HandleGeneration = BinaryPrimitives.ReadUInt16LittleEndian(source: handle[2..]),
            };
            var record = result.Slice(length: AddonAbi.BodyPoseRecordBytes, start: (i * AddonAbi.BodyPoseRecordBytes));

            BinaryPrimitives.WriteInt32LittleEndian(destination: record, value: -1);
            var allowed = QueryAuthority(addon: addon, capability: WorldCapability.Observe, query: in read, subject: out var subject, verdict: out var verdict);

            if (allowed && (subject.Kind != GrantSubjectKind.Body)) {
                verdict = AddonVerdict.NoSuchSubject;
                allowed = false;
            }
            if (allowed) {
                if (m_server.Body(index: subject.Value) is { } body) {
                    BinaryPrimitives.WriteInt32LittleEndian(destination: record, value: subject.Value);
                    var position = body.FixedPosition;
                    var orientation = body.FixedOrientation;

                    BinaryPrimitives.WriteInt64LittleEndian(destination: record[8..], value: position.X.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(destination: record[16..], value: position.Y.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(destination: record[24..], value: position.Z.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(destination: record[32..], value: orientation.X.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(destination: record[40..], value: orientation.Y.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(destination: record[48..], value: orientation.Z.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(destination: record[56..], value: orientation.W.Value);
                } else { verdict = AddonVerdict.NoSuchSubject; }
            }
            record[4] = ((byte)verdict);
        }
        if (!addon.Instance.TryWriteMemory(query.C, result, out _)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.MalformedPayload);
            return;
        }
        QueuePart(addon, query.Ordinal, AddonVerdict.Applied, 0, count, unchecked((long)tick));
    }
}
