using Puck.Audio.Simulation;
using Puck.Networking;
using Puck.Physics;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteIntentSubmission(WireWriter writer, IntentSubmission submission) {
        writer.WriteUInt64(value: submission.Tick);
        writer.WriteInt32(value: submission.EntityIndex);
        WorldWireCodec.WriteIntent(
            intent: submission.Intent,
            writer: writer
        );
        WritePrincipal(
            writer: writer,
            principal: submission.Principal
        );
        WorldWireCodec.WriteIntent(
            intent: submission.HeldChannels,
            writer: writer
        );
        writer.WriteInt32(value: submission.MeasuredHoldTicks);
    }
    private static IntentSubmission ReadIntentSubmission(ref WireReader reader) {
        var tick = reader.ReadUInt64();
        var entityIndex = reader.ReadInt32();
        var intent = WorldWireCodec.ReadIntent(reader: ref reader);
        var principal = ReadPrincipal(reader: ref reader);
        var heldChannels = WorldWireCodec.ReadIntent(reader: ref reader);
        var measuredHoldTicks = reader.ReadInt32();

        return new IntentSubmission(
            EntityIndex: entityIndex,
            HeldChannels: heldChannels,
            Intent: intent,
            MeasuredHoldTicks: measuredHoldTicks,
            Principal: principal,
            Tick: tick
        );
    }
    private static void WriteDocumentSubmission(WireWriter writer, WorldDocumentSubmission submission) {
        writer.WriteString(value: submission.SourceDocumentId);
        writer.WriteString(value: submission.OwnerDocumentId);
        writer.WriteUInt64(value: submission.Tick);
        writer.WriteString(value: submission.Slot);
        writer.WriteByte(value: ((byte)submission.Kind));
        writer.WriteByte(value: ((byte)submission.StorageKind));
        writer.WriteInt64(value: submission.Value);
        writer.WriteNullableString(value: submission.Text);
    }
    private static WorldDocumentSubmission ReadDocumentSubmission(ref WireReader reader) {
        var sourceDocumentId = reader.ReadString(
            field: "document submission source",
            maxBytes: MaxStringBytes
        );
        var ownerDocumentId = reader.ReadString(
            field: "document submission owner",
            maxBytes: MaxStringBytes
        );
        var tick = reader.ReadUInt64();
        var slot = reader.ReadString(
            field: "document submission slot",
            maxBytes: MaxStringBytes
        );
        var kind = ((WorldDocumentWriteKind)reader.ReadByte());

        if (
            !reader.Failed &&
            !Enum.IsDefined(value: kind)
        ) {
            reader.Fail(
                detail: $"{nameof(WorldDocumentWriteKind)} wire value {((byte)kind)} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        var storageKind = ((ActionStateKind)reader.ReadByte());

        if (
            !reader.Failed &&
            !Enum.IsDefined(value: storageKind)
        ) {
            reader.Fail(
                detail: $"{nameof(ActionStateKind)} wire value {((byte)storageKind)} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        var value = reader.ReadInt64();
        var text = reader.ReadNullableString(
            field: "document submission text",
            maxBytes: MaxStringBytes
        );

        return new WorldDocumentSubmission(
            Kind: kind,
            OwnerDocumentId: ownerDocumentId,
            Slot: slot,
            SourceDocumentId: sourceDocumentId,
            StorageKind: storageKind,
            Text: text,
            Tick: tick,
            Value: value
        );
    }
    private static void WriteDocumentReceipt(WireWriter writer, WorldDocumentSubmissionReceipt receipt) {
        WriteDocumentSubmission(
            writer: writer,
            submission: receipt.Submission
        );
        writer.WriteBoolean(value: receipt.Accepted);
        writer.WriteString(value: receipt.Reason);
    }
    private static WorldDocumentSubmissionReceipt ReadDocumentReceipt(ref WireReader reader) {
        var submission = ReadDocumentSubmission(reader: ref reader);
        var accepted = reader.ReadBoolean();
        var reasonText = reader.ReadString(
            field: "document receipt reason",
            maxBytes: MaxStringBytes
        );

        return new WorldDocumentSubmissionReceipt(
            Accepted: accepted,
            Reason: reasonText,
            Submission: submission
        );
    }
    private static void WriteMusicTransition(WireWriter writer, MusicTransition transition) {
        writer.WriteString(value: transition.ToSegmentId);
        writer.WriteByte(value: ((byte)transition.When));
        writer.WriteByte(value: ((byte)transition.At));
    }
    private static MusicTransition ReadMusicTransition(ref WireReader reader) {
        var toSegmentId = reader.ReadString(
            field: "music transition segment",
            maxBytes: MaxStringBytes
        );
        var when = ((MusicSenseFamily)reader.ReadByte());

        if (
            !reader.Failed &&
            !Enum.IsDefined(value: when)
        ) {
            reader.Fail(
                detail: $"{nameof(MusicSenseFamily)} wire value {((byte)when)} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        var at = ((MusicTransitionBoundary)reader.ReadByte());

        if (
            !reader.Failed &&
            !Enum.IsDefined(value: at)
        ) {
            reader.Fail(
                detail: $"{nameof(MusicTransitionBoundary)} wire value {((byte)at)} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        return new MusicTransition(
            At: at,
            ToSegmentId: toSegmentId,
            When: when
        );
    }
    private static void WritePendingOp(WireWriter writer, WorldPendingOpCheckpoint op) {
        switch (op) {
            case WorldPendingOpCheckpoint.Mutate value:
                writer.WriteByte(value: 0);
                writer.WriteBlock(value: EncodeLeafBlock<WorldMutation>(
                    value: value.Mutation,
                    what: "pending mutation",
                    tryEncode: WorldSubmissionCodec.TryEncodeMutation
                ));
                writer.WriteInt32(value: value.ConnectionId);
                writer.WriteInt64(value: value.CorrelationId);
                writer.WriteInt64(value: value.SourceAddonInstanceId);
                WriteUInt16(
                    writer: writer,
                    value: value.ActOrdinal
                );
                break;
            case WorldPendingOpCheckpoint.Rebuild value:
                writer.WriteByte(value: 1);
                writer.WriteBlock(value: EncodeLeafBlock<WorldRebuildRequest>(
                    value: value.Request,
                    what: "pending rebuild",
                    tryEncode: WorldSubmissionCodec.TryEncodeRebuild
                ));
                WritePrincipal(
                    writer: writer,
                    principal: value.Principal
                );
                writer.WriteInt32(value: value.ConnectionId);
                writer.WriteInt64(value: value.CorrelationId);
                writer.WriteNullableString(value: value.ExpectedContentHash);
                writer.WriteNullableString(value: value.PreparationFailure);
                break;
            case WorldPendingOpCheckpoint.Undo value:
                writer.WriteByte(value: 2);
                writer.WriteInt32(value: value.Count);
                WritePrincipal(
                    writer: writer,
                    principal: value.Principal
                );
                writer.WriteInt32(value: value.ConnectionId);
                writer.WriteInt64(value: value.CorrelationId);
                break;
            default:
                throw new InvalidOperationException(message: $"pending op '{op.GetType().Name}' has no wire discriminant");
        }
    }
    private static WorldPendingOpCheckpoint ReadPendingOp(ref WireReader reader) {
        var kind = reader.ReadByte();

        switch (kind) {
            case 0: {
                    var mutation = ReadLeafBlock<WorldMutation>(
                        field: "pending mutation",
                        reader: ref reader,
                        tryDecode: WorldSubmissionCodec.TryDecodeMutation
                    );
                    var connectionId = reader.ReadInt32();
                    var correlationId = reader.ReadInt64();
                    var sourceAddonInstanceId = reader.ReadInt64();
                    var actOrdinal = ReadUInt16(reader: ref reader);

                    return new WorldPendingOpCheckpoint.Mutate(
                        ActOrdinal: actOrdinal,
                        ConnectionId: connectionId,
                        CorrelationId: correlationId,
                        Mutation: mutation!,
                        SourceAddonInstanceId: sourceAddonInstanceId
                    );
                }
            case 1: {
                    var request = ReadLeafBlock<WorldRebuildRequest>(
                        field: "pending rebuild",
                        reader: ref reader,
                        tryDecode: WorldSubmissionCodec.TryDecodeRebuild
                    );
                    var principal = ReadPrincipal(reader: ref reader);
                    var connectionId = reader.ReadInt32();
                    var correlationId = reader.ReadInt64();
                    var expectedContentHash = reader.ReadNullableString(
                        field: "pending rebuild expected hash",
                        maxBytes: MaxStringBytes
                    );
                    var preparationFailure = reader.ReadNullableString(
                        field: "pending rebuild preparation failure",
                        maxBytes: MaxStringBytes
                    );

                    return new WorldPendingOpCheckpoint.Rebuild(
                        ConnectionId: connectionId,
                        CorrelationId: correlationId,
                        ExpectedContentHash: expectedContentHash,
                        PreparationFailure: preparationFailure,
                        Principal: principal,
                        Request: request!
                    );
                }
            case 2: {
                    var count = reader.ReadInt32();
                    var principal = ReadPrincipal(reader: ref reader);
                    var connectionId = reader.ReadInt32();
                    var correlationId = reader.ReadInt64();

                    return new WorldPendingOpCheckpoint.Undo(
                        ConnectionId: connectionId,
                        CorrelationId: correlationId,
                        Count: count,
                        Principal: principal
                    );
                }
            // Discriminant 3 (the retired pending addon-lifecycle op) is unassigned and never reused.
            default:
                if (!reader.Failed) {
                    reader.Fail(
                        detail: $"pending op discriminant {kind} is not declared",
                        refusal: WireRefusal.PayloadMalformed
                    );
                }

                return new WorldPendingOpCheckpoint.Undo(
                    ConnectionId: 0,
                    CorrelationId: 0,
                    Count: 0,
                    Principal: default
                );
        }
    }
    // ---- server section ----

    private static byte[] EncodeServer(WorldServer.WorldServerCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteBlock(value: section.DefinitionJson);
        writer.WriteBlock(value: section.BaseDefinitionJson);
        writer.WriteString(value: section.BaseOrigin);
        WriteArray(
            writer: writer,
            items: section.Journal,
            writeItem: static (w, entry) => {
                w.WriteUInt64(value: entry.Tick);
                w.WriteBlock(value: EncodeLeafBlock<WorldMutation>(
                    tryEncode: WorldSubmissionCodec.TryEncodeMutation,
                    value: entry.Mutation,
                    what: "journal mutation"
                ));
            }
        );
        writer.WriteUInt64(value: section.LastCompletedTick);
        writer.WriteUInt64(value: section.LastCompletedEngineTicks);
        writer.WriteUInt64(value: section.LastStepTicks);
        WriteArray(
            writer: writer,
            items: section.Intents,
            writeItem: WriteIntentSubmission
        );
        WriteArray(
            writer: writer,
            items: section.Pending,
            writeItem: WritePendingOp
        );
        WriteArray(
            writer: writer,
            items: section.RuleGateHeld,
            writeItem: static (w, row) => {
                w.WriteString(value: row.Rule);
                w.WriteBoolean(value: row.Held);
            }
        );
        WriteArray(
            writer: writer,
            items: section.InteractionGateHeld,
            writeItem: static (w, row) => {
                w.WriteString(value: row.Interaction);
                w.WriteBoolean(value: row.Held);
            }
        );
        WriteOptional(
            writer: writer,
            value: section.LastDocumentReceipt,
            writeValue: WriteDocumentReceipt
        );
        writer.WriteInt32(value: section.SolidRevision);
        WriteOptional(
            writer: writer,
            value: section.MusicClockElapsedTicks,
            writeValue: static (w, v) => w.WriteUInt64(value: v)
        );
        writer.WriteNullableString(value: section.MusicDirectorCurrentSegmentId);
        WriteOptional(
            writer: writer,
            value: section.MusicDirectorArmed,
            writeValue: WriteMusicTransition
        );
        writer.WriteUInt64(value: section.MusicDirectorTransitionCount);
        WriteOptional(
            writer: writer,
            value: section.MusicDirectorLastTransitionTick,
            writeValue: static (w, v) => w.WriteUInt64(value: v)
        );
        writer.WriteNullableString(value: section.MusicDirectorLastTransitionFromSegmentId);
        writer.WriteNullableString(value: section.MusicDirectorLastTransitionToSegmentId);
        writer.WriteNullableString(value: section.MusicDirectorLastEmbellishmentPatchId);
        WriteOptional(
            writer: writer,
            value: section.MusicDirectorLastEmbellishmentTick,
            writeValue: static (w, v) => w.WriteUInt64(value: v)
        );
        WriteArray(
            writer: writer,
            items: section.JudgeGrades,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.EntityIndex);
                w.WriteString(value: row.JudgeRef);
                w.WriteNullableString(value: row.Grade);
                w.WriteUInt64(value: row.Tick);
            }
        );

        return writer.ToArray();
    }
    private static bool TryDecodeServer(byte[] bytes, string definitionHash, out string reason, out WorldServer.WorldServerCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var definitionJson = reader.ReadBlock(
            field: "server definition",
            maxBytes: MaxSectionBytes
        );

        if (
            !reader.Failed &&
            !string.Equals(
            a: WorldDefinitionFileSource.ComputeContentHash(content: definitionJson),
            b: definitionHash,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            reader.Fail(
                detail: "server definition does not match the checkpoint's own content-address pin",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        var baseDefinitionJson = reader.ReadBlock(
            field: "server base definition",
            maxBytes: MaxSectionBytes
        );
        var baseOrigin = reader.ReadString(
            field: "server base origin",
            maxBytes: MaxStringBytes
        );
        var journal = ReadArray(
            reader: ref reader,
            field: "server journal",
            readItem: static (ref WireReader r) => {
                var tick = r.ReadUInt64();
                var mutation = ReadLeafBlock<WorldMutation>(
                    field: "journal mutation",
                    reader: ref r,
                    tryDecode: WorldSubmissionCodec.TryDecodeMutation
                );

                return (tick, mutation!);
            }
        );
        var lastCompletedTick = reader.ReadUInt64();
        var lastCompletedEngineTicks = reader.ReadUInt64();
        var lastStepTicks = reader.ReadUInt64();
        var intents = ReadArray(
            reader: ref reader,
            field: "server intents",
            readItem: static (ref WireReader r) => ReadIntentSubmission(reader: ref r)
        );
        var pending = ReadArray(
            reader: ref reader,
            field: "server pending",
            readItem: static (ref WireReader r) => ReadPendingOp(reader: ref r)
        );
        var ruleGateHeld = ReadArray(
            reader: ref reader,
            field: "server rule gate held",
            readItem: static (ref WireReader r) => {
                var rule = r.ReadString(
                    field: "rule gate name",
                    maxBytes: MaxStringBytes
                );
                var held = r.ReadBoolean();

                return (rule, held);
            }
        );
        var interactionGateHeld = ReadArray(
            reader: ref reader,
            field: "server interaction gate held",
            readItem: static (ref WireReader r) => {
                var interaction = r.ReadString(
                    field: "interaction gate name",
                    maxBytes: MaxStringBytes
                );
                var held = r.ReadBoolean();

                return (interaction, held);
            }
        );
        var lastDocumentReceipt = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadDocumentReceipt(reader: ref r)
        );
        var solidRevision = reader.ReadInt32();
        var musicClockElapsedTicks = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => r.ReadUInt64()
        );
        var musicDirectorCurrentSegmentId = reader.ReadNullableString(
            field: "music director segment",
            maxBytes: MaxStringBytes
        );
        var musicDirectorArmed = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadMusicTransition(reader: ref r)
        );
        var musicDirectorTransitionCount = reader.ReadUInt64();
        var musicDirectorLastTransitionTick = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => r.ReadUInt64()
        );
        var musicDirectorLastTransitionFromSegmentId = reader.ReadNullableString(
            field: "music director last from segment",
            maxBytes: MaxStringBytes
        );
        var musicDirectorLastTransitionToSegmentId = reader.ReadNullableString(
            field: "music director last to segment",
            maxBytes: MaxStringBytes
        );
        var musicDirectorLastEmbellishmentPatchId = reader.ReadNullableString(
            field: "music director last embellishment patch",
            maxBytes: MaxStringBytes
        );
        var musicDirectorLastEmbellishmentTick = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => r.ReadUInt64()
        );
        var judgeGrades = ReadArray(
            reader: ref reader,
            field: "server judge grades",
            readItem: static (ref WireReader r) => {
                var entityIndex = r.ReadInt32();
                var judgeRef = r.ReadString(
                    field: "judge grade judgeRef",
                    maxBytes: MaxStringBytes
                );
                var grade = r.ReadNullableString(
                    field: "judge grade",
                    maxBytes: MaxStringBytes
                );
                var tick = r.ReadUInt64();

                return (entityIndex, judgeRef, grade, tick);
            }
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"server section: {failure}";

            return false;
        }

        section = new WorldServer.WorldServerCheckpoint(
            BaseDefinitionJson: baseDefinitionJson,
            BaseOrigin: baseOrigin,
            DefinitionJson: definitionJson,
            Intents: intents,
            InteractionGateHeld: interactionGateHeld,
            Journal: journal,
            JudgeGrades: judgeGrades,
            LastCompletedEngineTicks: lastCompletedEngineTicks,
            LastCompletedTick: lastCompletedTick,
            LastDocumentReceipt: lastDocumentReceipt,
            LastStepTicks: lastStepTicks,
            MusicClockElapsedTicks: musicClockElapsedTicks,
            MusicDirectorArmed: musicDirectorArmed,
            MusicDirectorCurrentSegmentId: musicDirectorCurrentSegmentId,
            MusicDirectorLastEmbellishmentPatchId: musicDirectorLastEmbellishmentPatchId,
            MusicDirectorLastEmbellishmentTick: musicDirectorLastEmbellishmentTick,
            MusicDirectorLastTransitionFromSegmentId: musicDirectorLastTransitionFromSegmentId,
            MusicDirectorLastTransitionTick: musicDirectorLastTransitionTick,
            MusicDirectorLastTransitionToSegmentId: musicDirectorLastTransitionToSegmentId,
            MusicDirectorTransitionCount: musicDirectorTransitionCount,
            Pending: pending,
            RuleGateHeld: ruleGateHeld,
            SolidRevision: solidRevision
        );
        reason = string.Empty;

        return true;
    }
    // ---- population section ----

    private static void WriteTransferState(WireWriter writer, WorldBody.TransferState state) {
        writer.WriteFixedVector(value: state.PlanarVelocity);
        writer.WriteFixed(value: state.VerticalVelocity);
        writer.WriteFixedQuaternion(value: state.Orientation);
        writer.WriteFixed(value: state.VehiclePitch);
        writer.WriteFixedVector(value: state.OverlayVelocity);
        writer.WriteUInt64(value: state.OverlayRemainingTicks);
        WriteULongArray(
            writer: writer,
            values: state.ChannelTimerTicks
        );
        WriteFixedArray(
            writer: writer,
            values: state.ChannelTimerValues
        );
        writer.WriteString(value: state.BodyMotionProgramName);
        WriteIntentSource(
            writer: writer,
            source: state.Source
        );
        WriteBoolArray(
            writer: writer,
            values: state.PreviousChannelBit
        );
        WorldWireCodec.WriteIntent(
            intent: state.HeldChannelImage,
            writer: writer
        );
        WriteBoolArray(
            writer: writer,
            values: state.PendingDefaultChannelPress
        );
        WriteFixedArray(
            writer: writer,
            values: state.PendingDefaultChannelValue
        );
        WriteULongArray(
            writer: writer,
            values: state.MotionRecency
        );
        writer.WriteInt64(value: state.PlanarRampRemainder);
        writer.WriteInt64(value: state.VehicleLongRemainder);
        writer.WriteInt64(value: state.VehicleLatRemainder);
        writer.WriteInt64(value: state.VehicleResidualRemainder);
        writer.WriteInt64(value: state.SwimThrustRampRemainder);
        writer.WriteInt64(value: state.PlanarFollowerPositionRawX);
        writer.WriteInt64(value: state.PlanarFollowerPositionRawY);
        writer.WriteInt64(value: state.PlanarFollowerPositionRawZ);
        writer.WriteInt64(value: state.PlanarFollowerVelocityRawX);
        writer.WriteInt64(value: state.PlanarFollowerVelocityRawY);
        writer.WriteInt64(value: state.PlanarFollowerVelocityRawZ);
        writer.WriteFixedVector(value: state.PlanarFollowerPreviousTarget);
        writer.WriteInt64(value: state.VerticalFollowerPositionRaw);
        writer.WriteInt64(value: state.VerticalFollowerVelocityRaw);
        writer.WriteFixed(value: state.VerticalFollowerPreviousTarget);
        writer.WriteInt64(value: state.OverlayRemainderX);
        writer.WriteInt64(value: state.OverlayRemainderY);
        writer.WriteInt64(value: state.OverlayRemainderZ);
        WriteULongArray(
            writer: writer,
            values: state.LaneLatch
        );
        WriteULongArray(
            writer: writer,
            values: state.LaneFactHeld
        );
        WriteJaggedULongArray(
            writer: writer,
            rows: state.LaneRecency
        );
        WriteFixedArray(
            writer: writer,
            values: state.ActionStateValues
        );
        WriteULongArray(
            writer: writer,
            values: state.ActionStateTimers
        );
        WriteBoolArray(
            writer: writer,
            values: state.ActionStateDirty
        );
        WriteArray(
            writer: writer,
            items: state.ActionStateDirtyKind,
            writeItem: static (w, v) => w.WriteByte(value: ((byte)v))
        );
        WriteFixedArray(
            writer: writer,
            values: state.ActionStateDirtyOperand
        );
        WriteBoolArray(
            writer: writer,
            values: state.DurableInputPresent
        );
        WriteFixedArray(
            writer: writer,
            values: state.DurableInputValues
        );
        WriteULongArray(
            writer: writer,
            values: state.DurableInputTimers
        );
        WriteStringArray(
            writer: writer,
            values: state.DurableInputWriters
        );
        writer.WriteUInt64(value: state.DurableInputTick);
        WriteArray(
            writer: writer,
            items: state.TapeIntents,
            writeItem: WorldWireCodec.WriteIntent
        );
        WriteULongArray(
            writer: writer,
            values: state.TapeRemainingTicks
        );
        WriteOptional(
            writer: writer,
            value: state.PendingContinuum,
            writeValue: WriteContinuum
        );
    }
    private static WorldBody.TransferState ReadTransferState(ref WireReader reader) {
        var planarVelocity = reader.ReadFixedVector();
        var verticalVelocity = reader.ReadFixed();
        var orientation = reader.ReadFixedQuaternion();
        var vehiclePitch = reader.ReadFixed();
        var overlayVelocity = reader.ReadFixedVector();
        var overlayRemainingTicks = reader.ReadUInt64();
        var channelTimerTicks = ReadULongArray(
            field: "channel timer ticks",
            reader: ref reader
        );
        var channelTimerValues = ReadFixedArray(
            field: "channel timer values",
            reader: ref reader
        );
        var bodyMotionProgramName = reader.ReadString(
            field: "body motion program name",
            maxBytes: MaxStringBytes
        );
        var source = ReadIntentSource(reader: ref reader);
        var previousChannelBit = ReadBoolArray(
            field: "previous channel bit",
            reader: ref reader
        );
        var heldChannelImage = WorldWireCodec.ReadIntent(reader: ref reader);
        var pendingDefaultChannelPress = ReadBoolArray(
            field: "pending default channel press",
            reader: ref reader
        );
        var pendingDefaultChannelValue = ReadFixedArray(
            field: "pending default channel value",
            reader: ref reader
        );
        var motionRecency = ReadULongArray(
            field: "motion recency",
            reader: ref reader
        );
        var planarRampRemainder = reader.ReadInt64();
        var vehicleLongRemainder = reader.ReadInt64();
        var vehicleLatRemainder = reader.ReadInt64();
        var vehicleResidualRemainder = reader.ReadInt64();
        var swimThrustRampRemainder = reader.ReadInt64();
        var planarFollowerPositionRawX = reader.ReadInt64();
        var planarFollowerPositionRawY = reader.ReadInt64();
        var planarFollowerPositionRawZ = reader.ReadInt64();
        var planarFollowerVelocityRawX = reader.ReadInt64();
        var planarFollowerVelocityRawY = reader.ReadInt64();
        var planarFollowerVelocityRawZ = reader.ReadInt64();
        var planarFollowerPreviousTarget = reader.ReadFixedVector();
        var verticalFollowerPositionRaw = reader.ReadInt64();
        var verticalFollowerVelocityRaw = reader.ReadInt64();
        var verticalFollowerPreviousTarget = reader.ReadFixed();
        var overlayRemainderX = reader.ReadInt64();
        var overlayRemainderY = reader.ReadInt64();
        var overlayRemainderZ = reader.ReadInt64();
        var laneLatch = ReadULongArray(
            field: "lane latch",
            reader: ref reader
        );
        var laneFactHeld = ReadULongArray(
            field: "lane fact held",
            reader: ref reader
        );
        var laneRecency = ReadJaggedULongArray(
            field: "lane recency",
            reader: ref reader
        );
        var actionStateValues = ReadFixedArray(
            field: "action state values",
            reader: ref reader
        );
        var actionStateTimers = ReadULongArray(
            field: "action state timers",
            reader: ref reader
        );
        var actionStateDirty = ReadBoolArray(
            field: "action state dirty",
            reader: ref reader
        );
        var actionStateDirtyKind = ReadArray(
            reader: ref reader,
            field: "action state dirty kind",
            readItem: static (ref WireReader r) => {
                var kind = ((WorldDocumentWriteKind)r.ReadByte());

                if (
                    !r.Failed &&
                    !Enum.IsDefined(value: kind)
                ) {
                    r.Fail(
                        detail: $"{nameof(WorldDocumentWriteKind)} wire value {((byte)kind)} is not declared",
                        refusal: WireRefusal.EnumValueUnknown
                    );
                }

                return kind;
            }
        );
        var actionStateDirtyOperand = ReadFixedArray(
            field: "action state dirty operand",
            reader: ref reader
        );
        var durableInputPresent = ReadBoolArray(
            field: "durable input present",
            reader: ref reader
        );
        var durableInputValues = ReadFixedArray(
            field: "durable input values",
            reader: ref reader
        );
        var durableInputTimers = ReadULongArray(
            field: "durable input timers",
            reader: ref reader
        );
        var durableInputWriters = ReadStringArray(
            field: "durable input writers",
            reader: ref reader
        );
        var durableInputTick = reader.ReadUInt64();
        var tapeIntents = ReadArray(
            reader: ref reader,
            field: "tape intents",
            readItem: static (ref WireReader r) => WorldWireCodec.ReadIntent(reader: ref r)
        );
        var tapeRemainingTicks = ReadULongArray(
            field: "tape remaining ticks",
            reader: ref reader
        );
        var pendingContinuum = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadContinuum(reader: ref r)
        );

        return new WorldBody.TransferState(
            ActionStateDirty: actionStateDirty,
            ActionStateDirtyKind: actionStateDirtyKind,
            ActionStateDirtyOperand: actionStateDirtyOperand,
            ActionStateTimers: actionStateTimers,
            ActionStateValues: actionStateValues,
            BodyMotionProgramName: bodyMotionProgramName,
            ChannelTimerTicks: channelTimerTicks,
            ChannelTimerValues: channelTimerValues,
            DurableInputPresent: durableInputPresent,
            DurableInputTick: durableInputTick,
            DurableInputTimers: durableInputTimers,
            DurableInputValues: durableInputValues,
            DurableInputWriters: durableInputWriters,
            HeldChannelImage: heldChannelImage,
            LaneFactHeld: laneFactHeld,
            LaneLatch: laneLatch,
            LaneRecency: laneRecency,
            MotionRecency: motionRecency,
            Orientation: orientation,
            OverlayRemainderX: overlayRemainderX,
            OverlayRemainderY: overlayRemainderY,
            OverlayRemainderZ: overlayRemainderZ,
            OverlayRemainingTicks: overlayRemainingTicks,
            OverlayVelocity: overlayVelocity,
            PendingContinuum: pendingContinuum,
            PendingDefaultChannelPress: pendingDefaultChannelPress,
            PendingDefaultChannelValue: pendingDefaultChannelValue,
            PlanarFollowerPositionRawX: planarFollowerPositionRawX,
            PlanarFollowerPositionRawY: planarFollowerPositionRawY,
            PlanarFollowerPositionRawZ: planarFollowerPositionRawZ,
            PlanarFollowerVelocityRawX: planarFollowerVelocityRawX,
            PlanarFollowerVelocityRawY: planarFollowerVelocityRawY,
            PlanarFollowerVelocityRawZ: planarFollowerVelocityRawZ,
            PlanarFollowerPreviousTarget: planarFollowerPreviousTarget,
            PlanarRampRemainder: planarRampRemainder,
            PlanarVelocity: planarVelocity,
            PreviousChannelBit: previousChannelBit,
            Source: source,
            SwimThrustRampRemainder: swimThrustRampRemainder,
            TapeIntents: tapeIntents,
            TapeRemainingTicks: tapeRemainingTicks,
            VehicleLatRemainder: vehicleLatRemainder,
            VehicleLongRemainder: vehicleLongRemainder,
            VehiclePitch: vehiclePitch,
            VehicleResidualRemainder: vehicleResidualRemainder,
            VerticalFollowerPositionRaw: verticalFollowerPositionRaw,
            VerticalFollowerVelocityRaw: verticalFollowerVelocityRaw,
            VerticalFollowerPreviousTarget: verticalFollowerPreviousTarget,
            VerticalVelocity: verticalVelocity
        );
    }
    private static void WriteJaggedULongArray(WireWriter writer, IReadOnlyList<ulong[]?> rows) {
        writer.WriteInt32(value: rows.Count);

        foreach (var row in rows) {
            var present = (row is not null);

            writer.WriteBoolean(value: present);

            if (present) {
                WriteULongArray(
                    values: row!,
                    writer: writer
                );
            }
        }
    }
    private static ulong[]?[] ReadJaggedULongArray(ref WireReader reader, string field) {
        var count = reader.ReadCount(
            field: field,
            maximum: MaxCollectionCount,
            minimum: 0
        );
        var rows = new ulong[]?[count];

        for (var index = 0; ((index < count) && !reader.Failed); index++) {
            rows[index] = (reader.ReadBoolean()
                ? ReadULongArray(
                    field: field,
                    reader: ref reader
                )
                : null
            );
        }

        return rows;
    }
    private static void WriteResidue(WireWriter writer, WorldBody.IntegrationResidue residue) {
        writer.WriteFixedVector(value: residue.PreviousPosition);
        writer.WriteInt64(value: residue.PositionRemainderX);
        writer.WriteInt64(value: residue.PositionRemainderY);
        writer.WriteInt64(value: residue.PositionRemainderZ);
        writer.WriteInt64(value: residue.RotationRemainderX);
        writer.WriteInt64(value: residue.RotationRemainderY);
        writer.WriteInt64(value: residue.RotationRemainderZ);
        writer.WriteInt64(value: residue.VerticalVelocityRemainder);
        writer.WriteFixedVector(value: residue.Up);
        writer.WriteBoolean(value: residue.Grounded);
        writer.WriteBoolean(value: residue.Engaged);
        WorldWireCodec.WriteIntent(
            intent: residue.EngagedIntent,
            writer: writer
        );
        writer.WriteBoolean(value: residue.OrdinaryAdvanceAdmitted);
        WriteOptional(
            writer: writer,
            value: residue.ContinuumConsumedThroughEngineTick,
            writeValue: static (w, v) => w.WriteUInt64(value: v)
        );
        writer.WriteInt32(value: residue.AffectingSubject);
        writer.WriteFixedQuaternion(value: residue.Frame);
        writer.WriteBoolean(value: residue.UpNeedsReseat);
        writer.WriteInt64(value: residue.FieldUpTurnRemainder);
        writer.WriteInt64(value: residue.ContactUpTurnRemainder);
        writer.WriteBoolean(value: residue.PlanarFollowerSeeded);
        writer.WriteBoolean(value: residue.VerticalFollowerSeeded);
        WriteAttachmentResidue(
            writer: writer,
            residue: residue.Attachment
        );
    }
    private static WorldBody.IntegrationResidue ReadResidue(ref WireReader reader) {
        var previousPosition = reader.ReadFixedVector();
        var positionRemainderX = reader.ReadInt64();
        var positionRemainderY = reader.ReadInt64();
        var positionRemainderZ = reader.ReadInt64();
        var rotationRemainderX = reader.ReadInt64();
        var rotationRemainderY = reader.ReadInt64();
        var rotationRemainderZ = reader.ReadInt64();
        var verticalVelocityRemainder = reader.ReadInt64();
        var up = reader.ReadFixedVector();
        var grounded = reader.ReadBoolean();
        var engaged = reader.ReadBoolean();
        var engagedIntent = WorldWireCodec.ReadIntent(reader: ref reader);
        var ordinaryAdvanceAdmitted = reader.ReadBoolean();
        var continuumConsumedThroughEngineTick = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => r.ReadUInt64()
        );
        var affectingSubject = reader.ReadInt32();
        var frame = reader.ReadFixedQuaternion();
        var upNeedsReseat = reader.ReadBoolean();
        var fieldUpTurnRemainder = reader.ReadInt64();
        var contactUpTurnRemainder = reader.ReadInt64();
        var planarFollowerSeeded = reader.ReadBoolean();
        var verticalFollowerSeeded = reader.ReadBoolean();
        var attachment = ReadAttachmentResidue(reader: ref reader);

        return new WorldBody.IntegrationResidue(
            AffectingSubject: affectingSubject,
            Attachment: attachment,
            ContactUpTurnRemainder: contactUpTurnRemainder,
            ContinuumConsumedThroughEngineTick: continuumConsumedThroughEngineTick,
            Engaged: engaged,
            EngagedIntent: engagedIntent,
            FieldUpTurnRemainder: fieldUpTurnRemainder,
            Frame: frame,
            Grounded: grounded,
            OrdinaryAdvanceAdmitted: ordinaryAdvanceAdmitted,
            PlanarFollowerSeeded: planarFollowerSeeded,
            PositionRemainderX: positionRemainderX,
            PositionRemainderY: positionRemainderY,
            PositionRemainderZ: positionRemainderZ,
            PreviousPosition: previousPosition,
            RotationRemainderX: rotationRemainderX,
            RotationRemainderY: rotationRemainderY,
            RotationRemainderZ: rotationRemainderZ,
            Up: up,
            UpNeedsReseat: upNeedsReseat,
            VerticalFollowerSeeded: verticalFollowerSeeded,
            VerticalVelocityRemainder: verticalVelocityRemainder
        );
    }
    private static void WriteAttachmentResidue(WireWriter writer, WorldBody.AttachmentResidue residue) {
        writer.WriteByte(value: ((byte)residue.Mode));
        writer.WriteBoolean(value: residue.AttachPreviousBit);
        writer.WriteBoolean(value: residue.DetachPreviousBit);
        writer.WriteFixedVector(value: residue.ClimbAnchor);
        writer.WriteFixedVector(value: residue.ClimbNormal);
        writer.WriteFixedVector(value: residue.ClimbTangentRight);
        writer.WriteFixedVector(value: residue.ClimbTangentUp);
        writer.WriteFixedVector(value: residue.ClimbVelocity);
        writer.WriteInt64(value: residue.ClimbRemainderX);
        writer.WriteInt64(value: residue.ClimbRemainderY);
        writer.WriteInt64(value: residue.ClimbRemainderZ);
        writer.WriteBoolean(value: residue.ClimbGrantedByOverride);
        writer.WriteBoolean(value: residue.Tether.HasValue);

        if (residue.Tether is { } tether) {
            writer.WriteFixed(value: tether.Length);
            writer.WriteFixed(value: tether.MinLength);
            writer.WriteInt64(value: tether.Remainder);
        }

        writer.WriteInt32(value: residue.TetherAnchorBodyIndex);
        writer.WriteFixedVector(value: residue.TetherAnchorPointOrLocalOffset);
    }
    private static WorldBody.AttachmentResidue ReadAttachmentResidue(ref WireReader reader) {
        var mode = ((WorldBodyAttachmentMode)reader.ReadByte());

        if (
            !reader.Failed &&
            !Enum.IsDefined(value: mode)
        ) {
            reader.Fail(
                detail: $"{nameof(WorldBodyAttachmentMode)} wire value {((byte)mode)} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        var attachPreviousBit = reader.ReadBoolean();
        var detachPreviousBit = reader.ReadBoolean();
        var climbAnchor = reader.ReadFixedVector();
        var climbNormal = reader.ReadFixedVector();
        var climbTangentRight = reader.ReadFixedVector();
        var climbTangentUp = reader.ReadFixedVector();
        var climbVelocity = reader.ReadFixedVector();
        var climbRemainderX = reader.ReadInt64();
        var climbRemainderY = reader.ReadInt64();
        var climbRemainderZ = reader.ReadInt64();
        var climbGrantedByOverride = reader.ReadBoolean();
        var tether = (reader.ReadBoolean()
            ? new FixedTetherConstraintState(
                Length: reader.ReadFixed(),
                MinLength: reader.ReadFixed(),
                Remainder: reader.ReadInt64()
            )
            : (FixedTetherConstraintState?)null
        );
        var tetherAnchorBodyIndex = reader.ReadInt32();
        var tetherAnchorPointOrLocalOffset = reader.ReadFixedVector();

        return new WorldBody.AttachmentResidue(
            Mode: mode,
            AttachPreviousBit: attachPreviousBit,
            DetachPreviousBit: detachPreviousBit,
            ClimbAnchor: climbAnchor,
            ClimbNormal: climbNormal,
            ClimbTangentRight: climbTangentRight,
            ClimbTangentUp: climbTangentUp,
            ClimbVelocity: climbVelocity,
            ClimbRemainderX: climbRemainderX,
            ClimbRemainderY: climbRemainderY,
            ClimbRemainderZ: climbRemainderZ,
            ClimbGrantedByOverride: climbGrantedByOverride,
            Tether: tether,
            TetherAnchorBodyIndex: tetherAnchorBodyIndex,
            TetherAnchorPointOrLocalOffset: tetherAnchorPointOrLocalOffset
        );
    }
}
