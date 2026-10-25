using System.Buffers.Binary;
using Puck.Scripting;
using Puck.World.Protocol;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    private void ResolveBodyPoses(MountedAddon addon, in AddonQuerySubmission query, ulong tick) {
        if (addon.Instance.State != AddonState.Enabled || !ChargeHostFuel(addon, 128)) { return; }
        if (query.HandleIndex != 0 || query.HandleGeneration != 0 ||
            query.B < 1 || query.B > AddonAbi.MaxPoseBatchBodies) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.MalformedPayload);
            return;
        }
        var count = (int)query.B;
        // Prepay copies and per-body authority/read work. Exhaustion publishes no partial result.
        if (!ChargeHostFuel(addon, (ulong)count * (4 + AddonAbi.BodyPoseRecordBytes + 64))) { return; }
        if (!addon.Instance.TryCopyMemory(query.A, count * 4, addon.PoseHandles, out _)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.MalformedPayload);
            return;
        }
        var result = addon.ReadBuffer.AsSpan(0, count * AddonAbi.BodyPoseRecordBytes);
        result.Clear();
        for (var i = 0; i < count; i++) {
            var handle = addon.PoseHandles.AsSpan(i * 4, 4);
            var read = query with {
                HandleIndex = BinaryPrimitives.ReadUInt16LittleEndian(handle),
                HandleGeneration = BinaryPrimitives.ReadUInt16LittleEndian(handle[2..]),
            };
            var record = result.Slice(i * AddonAbi.BodyPoseRecordBytes, AddonAbi.BodyPoseRecordBytes);
            BinaryPrimitives.WriteInt32LittleEndian(record, -1);
            var allowed = QueryAuthority(addon, in read, WorldCapability.Observe, out var subject, out var verdict);
            if (allowed && subject.Kind != GrantSubjectKind.Body) {
                verdict = AddonVerdict.NoSuchSubject;
                allowed = false;
            }
            if (allowed) {
                if (m_server.Body(subject.Value) is { } body) {
                    BinaryPrimitives.WriteInt32LittleEndian(record, subject.Value);
                    var position = body.FixedPosition;
                    var orientation = body.FixedOrientation;
                    BinaryPrimitives.WriteInt64LittleEndian(record[8..], position.X.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(record[16..], position.Y.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(record[24..], position.Z.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(record[32..], orientation.X.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(record[40..], orientation.Y.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(record[48..], orientation.Z.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(record[56..], orientation.W.Value);
                } else { verdict = AddonVerdict.NoSuchSubject; }
            }
            record[4] = (byte)verdict;
        }
        if (!addon.Instance.TryWriteMemory(query.C, result, out _)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.MalformedPayload);
            return;
        }
        QueuePart(addon, query.Ordinal, AddonVerdict.Applied, 0, count, unchecked((long)tick));
    }
}
