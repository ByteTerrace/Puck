using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteCapability(WireWriter writer, WorldCapability capability) {
        if (!WorldWireCodec.TryWriteCapability(
            capability: capability,
            writer: writer
        )) {
            throw new InvalidOperationException(message: $"{nameof(WorldCapability)}.{capability} has no wire value");
        }
    }
    // The (grantee, capability, subject) triple every per-grant row is keyed by — budgets, ceilings, masks, reach,
    // seeded sections — written and read through one pair so no row can spell the key in a different order.
    private static void WriteGrantKey(WireWriter writer, Grantee grantee, WorldCapability capability, GrantSubject subject) {
        WriteGrantee(
            grantee: grantee,
            writer: writer
        );
        WriteCapability(
            capability: capability,
            writer: writer
        );
        WriteSubject(
            subject: subject,
            writer: writer
        );
    }
    private static (Grantee Grantee, WorldCapability Capability, GrantSubject Subject) ReadGrantKey(ref WireReader reader) {
        var grantee = WorldWireCodec.ReadGrantee(reader: ref reader);
        var capability = WorldWireCodec.ReadCapability(reader: ref reader);
        var subject = WorldWireCodec.ReadSubject(reader: ref reader);

        return (grantee, capability, subject);
    }
    private static void WriteGrantsGrantee(WireWriter writer, WorldGrantsGranteeCheckpoint row) {
        WriteGrantee(
            grantee: row.Grantee,
            writer: writer
        );
        writer.WriteArray(
            items: row.Drive,
            writeItem: WriteSubject
        );
        writer.WriteArray(
            items: row.Observe,
            writeItem: WriteSubject
        );
        writer.WriteArray(
            items: row.Control,
            writeItem: WriteSubject
        );
        writer.WriteArray(
            items: row.Mutate,
            writeItem: WriteSubject
        );
        writer.WriteArray(
            items: row.Edit,
            writeItem: WriteSubject
        );
        writer.WriteArray(
            items: row.Applications,
            writeItem: WriteControlApplication
        );
    }
    private static void WriteControlApplication(WireWriter writer, ControlApplication application) {
        WriteSubject(
            writer: writer,
            subject: application.Target
        );
        writer.WriteString(value: (application.Kit ?? string.Empty));
        writer.WriteUInt64(value: application.Reach.Bits);
    }
    private static ControlApplication ReadControlApplication(ref WireReader reader) {
        var target = WorldWireCodec.ReadSubject(reader: ref reader);
        var kit = reader.ReadString(
            field: "control application kit",
            maxBytes: MaxStringBytes
        );
        var reach = reader.ReadUInt64();

        return new ControlApplication(
            Kit: ((kit.Length == 0)
            ? null
            : kit),
            Reach: new ChannelReachMask(Bits: reach),
            Target: target
        );
    }
    private static WorldGrantsGranteeCheckpoint ReadGrantsGrantee(ref WireReader reader) {
        var grantee = WorldWireCodec.ReadGrantee(reader: ref reader);
        var drive = reader.ReadArray(
            field: "grants drive subjects",
            readItem: static (ref WireReader r) => WorldWireCodec.ReadSubject(reader: ref r),
            maximum: MaxCollectionCount
        );
        var observe = reader.ReadArray(
            field: "grants observe subjects",
            readItem: static (ref WireReader r) => WorldWireCodec.ReadSubject(reader: ref r),
            maximum: MaxCollectionCount
        );
        var control = reader.ReadArray(
            field: "grants control subjects",
            readItem: static (ref WireReader r) => WorldWireCodec.ReadSubject(reader: ref r),
            maximum: MaxCollectionCount
        );
        var mutate = reader.ReadArray(
            field: "grants mutate subjects",
            readItem: static (ref WireReader r) => WorldWireCodec.ReadSubject(reader: ref r),
            maximum: MaxCollectionCount
        );
        var edit = reader.ReadArray(
            field: "grants edit subjects",
            readItem: static (ref WireReader r) => WorldWireCodec.ReadSubject(reader: ref r),
            maximum: MaxCollectionCount
        );
        var applications = reader.ReadArray(
            field: "grants control applications",
            readItem: static (ref WireReader r) => ReadControlApplication(reader: ref r),
            maximum: MaxCollectionCount
        );

        return new WorldGrantsGranteeCheckpoint(
            Applications: applications,
            Control: control,
            Drive: drive,
            Edit: edit,
            Grantee: grantee,
            Mutate: mutate,
            Observe: observe
        );
    }
    private static byte[] EncodeGrants(WorldGrantsCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteArray(
            items: section.Grantees,
            writeItem: WriteGrantsGrantee
        );
        writer.WriteArray(
            items: section.Exclusive,
            writeItem: static (w, row) => {
                WriteCapability(
                    capability: row.Capability,
                    writer: w
                );
                WriteSubject(
                    subject: row.Subject,
                    writer: w
                );
                WriteGrantee(
                    grantee: row.Holder,
                    writer: w
                );
            }
        );
        writer.WriteArray(
            items: section.Budgets,
            writeItem: static (w, row) => {
                WriteGrantKey(
                    capability: row.Capability,
                    grantee: row.Grantee,
                    subject: row.Subject,
                    writer: w
                );
                w.WriteInt32(value: row.Budget);
            }
        );
        writer.WriteArray(
            items: section.EventBudgets,
            writeItem: static (w, row) => {
                WriteGrantKey(
                    capability: row.Capability,
                    grantee: row.Grantee,
                    subject: row.Subject,
                    writer: w
                );
                w.WriteInt32(value: row.Budget);
            }
        );
        writer.WriteArray(
            items: section.HoldCeilings,
            writeItem: static (w, row) => {
                WriteGrantKey(
                    capability: row.Capability,
                    grantee: row.Grantee,
                    subject: row.Subject,
                    writer: w
                );
                w.WriteInt64(value: row.Ceiling);
            }
        );
        writer.WriteArray(
            items: section.ChannelReach,
            writeItem: static (w, row) => {
                WriteGrantKey(
                    capability: row.Capability,
                    grantee: row.Grantee,
                    subject: row.Subject,
                    writer: w
                );
                w.WriteUInt64(value: row.Bits);
            }
        );
        writer.WriteArray(
            items: section.PoolCeilings,
            writeItem: static (w, row) => {
                WriteGrantKey(
                    capability: row.Capability,
                    grantee: row.Grantee,
                    subject: row.Subject,
                    writer: w
                );
                w.WriteArray(
                    items: row.Ceilings,
                    writeItem: static (w2, cell) => {
                        w2.WriteInt32(value: cell.Ordinal);
                        w2.WriteInt64(value: cell.Ceiling);
                    }
                );
            }
        );
        writer.WriteArray(
            items: section.KindMasks,
            writeItem: static (w, row) => {
                WriteGrantKey(
                    capability: row.Capability,
                    grantee: row.Grantee,
                    subject: row.Subject,
                    writer: w
                );
                w.WriteUInt128(
                    value: row.Bits
                );
            }
        );
        writer.WriteArray(
            items: section.WriteMasks,
            writeItem: static (w, row) => {
                WriteGrantKey(
                    capability: row.Capability,
                    grantee: row.Grantee,
                    subject: row.Subject,
                    writer: w
                );
                w.WriteUInt64(value: row.Bits);
            }
        );
        writer.WriteArray(
            items: section.SeededSections,
            writeItem: static (w, row) => {
                WriteGrantKey(
                    capability: row.Capability,
                    grantee: row.Grantee,
                    subject: row.Subject,
                    writer: w
                );
            }
        );
        writer.WriteArray(
            items: section.DriveGates,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.BodyIndex);
                w.WriteString(value: row.Reason);
            }
        );
        writer.WriteInt32(value: section.Revision);

        return writer.ToArray();
    }
    private static bool TryDecodeGrants(byte[] bytes, out string reason, out WorldGrantsCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var grantees = reader.ReadArray(
            field: "grants grantees",
            readItem: static (ref WireReader r) => ReadGrantsGrantee(reader: ref r),
            maximum: MaxCollectionCount
        );
        var exclusive = reader.ReadArray(
            field: "grants exclusive",
            readItem: static (ref WireReader r) => {
                var capability = WorldWireCodec.ReadCapability(reader: ref r);
                var subject = WorldWireCodec.ReadSubject(reader: ref r);
                var holder = WorldWireCodec.ReadGrantee(reader: ref r);

                return (capability, subject, holder);
            },
            maximum: MaxCollectionCount
        );
        var budgets = reader.ReadArray(
            field: "grants budgets",
            readItem: static (ref WireReader r) => {
                var (grantee, capability, subject) = ReadGrantKey(reader: ref r);
                var budget = ((ushort)r.ReadInt32());

                return (grantee, capability, subject, budget);
            },
            maximum: MaxCollectionCount
        );
        var eventBudgets = reader.ReadArray(
            field: "grants event budgets",
            readItem: static (ref WireReader r) => {
                var (grantee, capability, subject) = ReadGrantKey(reader: ref r);
                var budget = ((ushort)r.ReadInt32());

                return (grantee, capability, subject, budget);
            },
            maximum: MaxCollectionCount
        );
        var holdCeilings = reader.ReadArray(
            field: "grants hold ceilings",
            readItem: static (ref WireReader r) => {
                var (grantee, capability, subject) = ReadGrantKey(reader: ref r);
                var ceiling = r.ReadInt64();

                return (grantee, capability, subject, ceiling);
            },
            maximum: MaxCollectionCount
        );
        var channelReach = reader.ReadArray(
            field: "grants channel reach",
            readItem: static (ref WireReader r) => {
                var (grantee, capability, subject) = ReadGrantKey(reader: ref r);
                var bits = r.ReadUInt64();

                return (grantee, capability, subject, bits);
            },
            maximum: MaxCollectionCount
        );
        var poolCeilings = reader.ReadArray(
            field: "grants pool ceilings",
            readItem: static (ref WireReader r) => {
                var (grantee, capability, subject) = ReadGrantKey(reader: ref r);
                var ceilings = r.ReadArray(
                    field: "grants pool ceiling cells",
                    readItem: static (ref WireReader r2) => {
                        var ordinal = r2.ReadInt32();
                        var ceiling = r2.ReadInt64();

                        return (ordinal, ceiling);
                    },
                    maximum: MaxCollectionCount
                );

                return (grantee, capability, subject, ((IReadOnlyList<(int Ordinal, long Ceiling)>)ceilings));
            },
            maximum: MaxCollectionCount
        );
        var kindMasks = reader.ReadArray(
            field: "grants kind masks",
            readItem: static (ref WireReader r) => {
                var (grantee, capability, subject) = ReadGrantKey(reader: ref r);
                var bits = r.ReadUInt128();

                return (grantee, capability, subject, bits);
            },
            maximum: MaxCollectionCount
        );
        var writeMasks = reader.ReadArray(
            field: "grants write masks",
            readItem: static (ref WireReader r) => {
                var (grantee, capability, subject) = ReadGrantKey(reader: ref r);
                var bits = r.ReadUInt64();

                return (grantee, capability, subject, bits);
            },
            maximum: MaxCollectionCount
        );
        var seededSections = reader.ReadArray(
            field: "grants seeded sections",
            readItem: static (ref WireReader r) => {
                var (grantee, capability, subject) = ReadGrantKey(reader: ref r);

                return (grantee, capability, subject);
            },
            maximum: MaxCollectionCount
        );
        var driveGates = reader.ReadArray(
            field: "grants drive gates",
            readItem: static (ref WireReader r) => {
                var bodyIndex = r.ReadInt32();
                var reasonText = r.ReadString(
                    field: "grants drive gate reason",
                    maxBytes: MaxStringBytes
                );

                return (bodyIndex, reasonText);
            },
            maximum: MaxCollectionCount
        );
        var revision = reader.ReadInt32();

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"grants section: {failure}";

            return false;
        }

        section = new WorldGrantsCheckpoint(
            Budgets: budgets,
            ChannelReach: channelReach,
            DriveGates: driveGates,
            EventBudgets: eventBudgets,
            Exclusive: exclusive,
            Grantees: grantees,
            HoldCeilings: holdCeilings,
            KindMasks: kindMasks,
            PoolCeilings: poolCeilings,
            Revision: revision,
            SeededSections: seededSections,
            WriteMasks: writeMasks
        );
        reason = string.Empty;

        return true;
    }
    // ---- escrow section ----

}
