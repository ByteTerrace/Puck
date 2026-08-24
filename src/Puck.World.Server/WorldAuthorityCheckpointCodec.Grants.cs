using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteCapability(WireWriter writer, WorldCapability capability) {
        if (!WorldWireTags.TryToWire(
            value: capability,
            wire: out var wire
        )) {
            throw new InvalidOperationException(message: $"{nameof(WorldCapability)}.{capability} has no wire value");
        }
        writer.WriteByte(value: wire);
    }
    private static WorldCapability ReadCapability(ref WireReader reader) {
        var wire = reader.ReadByte();
        var valid = WorldWireTags.TryFromWire(
            value: out WorldCapability capability,
            wire: wire
        );

        if (
            !reader.Failed &&
            !valid
        ) {
            reader.Fail(
                detail: (WorldWireTags.IsRetiredCapabilityWire(wire: wire)
                    ? $"{nameof(WorldCapability)} wire value {wire} is retired"
                    : $"{nameof(WorldCapability)} wire value {wire} is not declared"
                ),
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        return capability;
    }
    private static void WriteGrantsPrincipal(WireWriter writer, WorldGrants.WorldGrantsPrincipalCheckpoint row) {
        WritePrincipal(
            writer: writer,
            principal: row.Principal
        );
        WriteArray(
            writer: writer,
            items: row.Drive,
            writeItem: WriteSubject
        );
        WriteArray(
            writer: writer,
            items: row.Observe,
            writeItem: WriteSubject
        );
        WriteArray(
            writer: writer,
            items: row.Control,
            writeItem: WriteSubject
        );
        WriteArray(
            writer: writer,
            items: row.Mutate,
            writeItem: WriteSubject
        );
        WriteArray(
            writer: writer,
            items: row.Edit,
            writeItem: WriteSubject
        );
        WriteArray(
            writer: writer,
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
        var target = ReadSubject(reader: ref reader);
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
    private static WorldGrants.WorldGrantsPrincipalCheckpoint ReadGrantsPrincipal(ref WireReader reader) {
        var principal = ReadPrincipal(reader: ref reader);
        var drive = ReadArray(
            reader: ref reader,
            field: "grants drive subjects",
            readItem: static (ref WireReader r) => ReadSubject(reader: ref r)
        );
        var observe = ReadArray(
            reader: ref reader,
            field: "grants observe subjects",
            readItem: static (ref WireReader r) => ReadSubject(reader: ref r)
        );
        var control = ReadArray(
            reader: ref reader,
            field: "grants control subjects",
            readItem: static (ref WireReader r) => ReadSubject(reader: ref r)
        );
        var mutate = ReadArray(
            reader: ref reader,
            field: "grants mutate subjects",
            readItem: static (ref WireReader r) => ReadSubject(reader: ref r)
        );
        var edit = ReadArray(
            reader: ref reader,
            field: "grants edit subjects",
            readItem: static (ref WireReader r) => ReadSubject(reader: ref r)
        );
        var applications = ReadArray(
            reader: ref reader,
            field: "grants control applications",
            readItem: static (ref WireReader r) => ReadControlApplication(reader: ref r)
        );

        return new WorldGrants.WorldGrantsPrincipalCheckpoint(
            Applications: applications,
            Control: control,
            Drive: drive,
            Edit: edit,
            Mutate: mutate,
            Observe: observe,
            Principal: principal
        );
    }
    private static byte[] EncodeGrants(WorldGrants.WorldGrantsCheckpoint section) {
        var writer = new WireWriter();

        WriteArray(
            writer: writer,
            items: section.Principals,
            writeItem: WriteGrantsPrincipal
        );
        WriteArray(
            writer: writer,
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
                WritePrincipal(
                    principal: row.Holder,
                    writer: w
                );
            }
        );
        WriteArray(
            writer: writer,
            items: section.Budgets,
            writeItem: static (w, row) => {
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
                WriteCapability(
                    capability: row.Capability,
                    writer: w
                );
                WriteSubject(
                    subject: row.Subject,
                    writer: w
                );
                w.WriteInt32(value: row.Budget);
            }
        );
        WriteArray(
            writer: writer,
            items: section.EventBudgets,
            writeItem: static (w, row) => {
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
                WriteCapability(
                    capability: row.Capability,
                    writer: w
                );
                WriteSubject(
                    subject: row.Subject,
                    writer: w
                );
                w.WriteInt32(value: row.Budget);
            }
        );
        WriteArray(
            writer: writer,
            items: section.HoldCeilings,
            writeItem: static (w, row) => {
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
                WriteCapability(
                    capability: row.Capability,
                    writer: w
                );
                WriteSubject(
                    subject: row.Subject,
                    writer: w
                );
                w.WriteInt64(value: row.Ceiling);
            }
        );
        WriteArray(
            writer: writer,
            items: section.ChannelReach,
            writeItem: static (w, row) => {
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
                WriteCapability(
                    capability: row.Capability,
                    writer: w
                );
                WriteSubject(
                    subject: row.Subject,
                    writer: w
                );
                w.WriteUInt64(value: row.Bits);
            }
        );
        WriteArray(
            writer: writer,
            items: section.PoolCeilings,
            writeItem: static (w, row) => {
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
                WriteCapability(
                    capability: row.Capability,
                    writer: w
                );
                WriteSubject(
                    subject: row.Subject,
                    writer: w
                );
                WriteArray(
                    writer: w,
                    items: row.Ceilings,
                    writeItem: static (w2, cell) => {
                        w2.WriteInt32(value: cell.Ordinal);
                        w2.WriteInt64(value: cell.Ceiling);
                    }
                );
            }
        );
        WriteArray(
            writer: writer,
            items: section.KindMasks,
            writeItem: static (w, row) => {
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
                WriteCapability(
                    capability: row.Capability,
                    writer: w
                );
                WriteSubject(
                    subject: row.Subject,
                    writer: w
                );
                WriteUInt128(
                    value: row.Bits,
                    writer: w
                );
            }
        );
        WriteArray(
            writer: writer,
            items: section.WriteMasks,
            writeItem: static (w, row) => {
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
                WriteCapability(
                    capability: row.Capability,
                    writer: w
                );
                WriteSubject(
                    subject: row.Subject,
                    writer: w
                );
                w.WriteUInt64(value: row.Bits);
            }
        );
        WriteArray(
            writer: writer,
            items: section.SeededSections,
            writeItem: static (w, row) => {
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
                WriteCapability(
                    capability: row.Capability,
                    writer: w
                );
                WriteSubject(
                    subject: row.Subject,
                    writer: w
                );
            }
        );
        WriteArray(
            writer: writer,
            items: section.GroupMembership,
            writeItem: static (w, row) => {
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
                WriteStringArray(
                    values: row.Groups,
                    writer: w
                );
            }
        );
        WriteArray(
            writer: writer,
            items: section.GroupReach,
            writeItem: static (w, row) => {
                w.WriteString(value: row.Group);
                WriteArray(
                    items: row.Reach,
                    writeItem: WriteCapability,
                    writer: w
                );
            }
        );
        WriteArray(
            writer: writer,
            items: section.OwnedGroups,
            writeItem: static (w, row) => {
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
                WriteStringArray(
                    values: row.Groups,
                    writer: w
                );
            }
        );
        WriteArray(
            writer: writer,
            items: section.DriveGates,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.BodyIndex);
                w.WriteString(value: row.Reason);
            }
        );
        writer.WriteInt32(value: section.Revision);

        return writer.ToArray();
    }
    private static bool TryDecodeGrants(byte[] bytes, out string reason, out WorldGrants.WorldGrantsCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var principals = ReadArray(
            reader: ref reader,
            field: "grants principals",
            readItem: static (ref WireReader r) => ReadGrantsPrincipal(reader: ref r)
        );
        var exclusive = ReadArray(
            reader: ref reader,
            field: "grants exclusive",
            readItem: static (ref WireReader r) => {
                var capability = ReadCapability(reader: ref r);
                var subject = ReadSubject(reader: ref r);
                var holder = ReadPrincipal(reader: ref r);

                return (capability, subject, holder);
            }
        );
        var budgets = ReadArray(
            reader: ref reader,
            field: "grants budgets",
            readItem: static (ref WireReader r) => {
                var principal = ReadPrincipal(reader: ref r);
                var capability = ReadCapability(reader: ref r);
                var subject = ReadSubject(reader: ref r);
                var budget = ((ushort)r.ReadInt32());

                return (principal, capability, subject, budget);
            }
        );
        var eventBudgets = ReadArray(
            reader: ref reader,
            field: "grants event budgets",
            readItem: static (ref WireReader r) => {
                var principal = ReadPrincipal(reader: ref r);
                var capability = ReadCapability(reader: ref r);
                var subject = ReadSubject(reader: ref r);
                var budget = ((ushort)r.ReadInt32());

                return (principal, capability, subject, budget);
            }
        );
        var holdCeilings = ReadArray(
            reader: ref reader,
            field: "grants hold ceilings",
            readItem: static (ref WireReader r) => {
                var principal = ReadPrincipal(reader: ref r);
                var capability = ReadCapability(reader: ref r);
                var subject = ReadSubject(reader: ref r);
                var ceiling = r.ReadInt64();

                return (principal, capability, subject, ceiling);
            }
        );
        var channelReach = ReadArray(
            reader: ref reader,
            field: "grants channel reach",
            readItem: static (ref WireReader r) => {
                var principal = ReadPrincipal(reader: ref r);
                var capability = ReadCapability(reader: ref r);
                var subject = ReadSubject(reader: ref r);
                var bits = r.ReadUInt64();

                return (principal, capability, subject, bits);
            }
        );
        var poolCeilings = ReadArray(
            reader: ref reader,
            field: "grants pool ceilings",
            readItem: static (ref WireReader r) => {
                var principal = ReadPrincipal(reader: ref r);
                var capability = ReadCapability(reader: ref r);
                var subject = ReadSubject(reader: ref r);
                var ceilings = ReadArray(
                    reader: ref r,
                    field: "grants pool ceiling cells",
                    readItem: static (ref WireReader r2) => {
                        var ordinal = r2.ReadInt32();
                        var ceiling = r2.ReadInt64();

                        return (ordinal, ceiling);
                    }
                );

                return (principal, capability, subject, ((IReadOnlyList<(int Ordinal, long Ceiling)>)ceilings));
            }
        );
        var kindMasks = ReadArray(
            reader: ref reader,
            field: "grants kind masks",
            readItem: static (ref WireReader r) => {
                var principal = ReadPrincipal(reader: ref r);
                var capability = ReadCapability(reader: ref r);
                var subject = ReadSubject(reader: ref r);
                var bits = ReadUInt128(reader: ref r);

                return (principal, capability, subject, bits);
            }
        );
        var writeMasks = ReadArray(
            reader: ref reader,
            field: "grants write masks",
            readItem: static (ref WireReader r) => {
                var principal = ReadPrincipal(reader: ref r);
                var capability = ReadCapability(reader: ref r);
                var subject = ReadSubject(reader: ref r);
                var bits = r.ReadUInt64();

                return (principal, capability, subject, bits);
            }
        );
        var seededSections = ReadArray(
            reader: ref reader,
            field: "grants seeded sections",
            readItem: static (ref WireReader r) => {
                var principal = ReadPrincipal(reader: ref r);
                var capability = ReadCapability(reader: ref r);
                var subject = ReadSubject(reader: ref r);

                return (principal, capability, subject);
            }
        );
        var groupMembership = ReadArray(
            reader: ref reader,
            field: "grants group membership",
            readItem: static (ref WireReader r) => {
                var principal = ReadPrincipal(reader: ref r);
                var groups = ReadStringArray(
                    field: "grants group membership groups",
                    reader: ref r
                );

                return (principal, ((IReadOnlyList<string>)groups));
            }
        );
        var groupReach = ReadArray(
            reader: ref reader,
            field: "grants group reach",
            readItem: static (ref WireReader r) => {
                var group = r.ReadString(
                    field: "grants group reach name",
                    maxBytes: MaxStringBytes
                );
                var reach = ReadArray(
                    reader: ref r,
                    field: "grants group reach capabilities",
                    readItem: static (ref WireReader r2) => ReadCapability(reader: ref r2)
                );

                return (group, ((IReadOnlyList<WorldCapability>)reach));
            }
        );
        var ownedGroups = ReadArray(
            reader: ref reader,
            field: "grants owned groups",
            readItem: static (ref WireReader r) => {
                var principal = ReadPrincipal(reader: ref r);
                var groups = ReadStringArray(
                    field: "grants owned groups groups",
                    reader: ref r
                );

                return (principal, ((IReadOnlyList<string>)groups));
            }
        );
        var driveGates = ReadArray(
            reader: ref reader,
            field: "grants drive gates",
            readItem: static (ref WireReader r) => {
                var bodyIndex = r.ReadInt32();
                var reasonText = r.ReadString(
                    field: "grants drive gate reason",
                    maxBytes: MaxStringBytes
                );

                return (bodyIndex, reasonText);
            }
        );
        var revision = reader.ReadInt32();

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"grants section: {failure}";

            return false;
        }

        section = new WorldGrants.WorldGrantsCheckpoint(
            Budgets: budgets,
            ChannelReach: channelReach,
            DriveGates: driveGates,
            EventBudgets: eventBudgets,
            Exclusive: exclusive,
            GroupMembership: groupMembership,
            GroupReach: groupReach,
            HoldCeilings: holdCeilings,
            KindMasks: kindMasks,
            OwnedGroups: ownedGroups,
            PoolCeilings: poolCeilings,
            Principals: principals,
            Revision: revision,
            SeededSections: seededSections,
            WriteMasks: writeMasks
        );
        reason = string.Empty;

        return true;
    }
    // ---- escrow section ----

}
