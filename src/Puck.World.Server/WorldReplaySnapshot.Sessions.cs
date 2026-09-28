using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldReplaySnapshot {
    // A session lifecycle entry: 16 admitted (the verdict's templates and the rows minted without a body), 17 embodied
    // (the body, its generation and the body-relative rows), 18 ended (the revoked rows). Each opens with the session
    // principal, which must be a session.
    private static WorldReplayEntry ReadSessionEntry(ref WireReader reader, byte kind) {
        var session = WorldWireCodec.ReadPrincipal(reader: ref reader);

        if (
            !reader.Failed &&
            (session.Kind != PrincipalKind.Session)
        ) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay session entry: {session.Describe()} is not a session principal.");
        }

        WorldServerEvent serverEvent = kind switch {
            16 => new WorldServerEvent.SessionAdmitted(
                Session: session,
                Templates: ReadTapeArray(
                    minimumBytesEach: 6,
                    readItem: static (ref WireReader r) => WorldAuthorityCheckpointCodec.ReadAdmissionGrant(reader: ref r),
                    reader: ref reader,
                    what: "session template"
                ),
                MintedGrants: ReadSessionGrants(
                    reader: ref reader,
                    revoke: false
                )
            ),
            17 => new WorldServerEvent.SessionEmbodied(
                Session: session,
                BodyIndex: reader.ReadInt32(),
                BodyGeneration: reader.ReadInt32(),
                MintedGrants: ReadSessionGrants(
                    reader: ref reader,
                    revoke: false
                )
            ),
            _ => new WorldServerEvent.SessionEnded(
                Session: session,
                RevokedGrants: ReadSessionGrants(
                    reader: ref reader,
                    revoke: true
                )
            ),
        };

        return new WorldReplayEntry.SessionEvent(Value: serverEvent);
    }
    private static WorldGrant[] ReadSessionGrants(ref WireReader reader, bool revoke) => ReadTapeArray(
        minimumBytesEach: 5,
        readItem: (ref WireReader r) => ReadGrantLeaf(
            reader: ref r,
            revoke: revoke
        ),
        reader: ref reader,
        what: "session grant"
    );
    private static void WriteSessionEntry(WireWriter writer, WorldServerEvent serverEvent) {
        switch (serverEvent) {
            case WorldServerEvent.SessionAdmitted admitted:
                writer.WriteByte(value: 16);
                WritePrincipal(
                    principal: admitted.Session,
                    writer: writer
                );
                writer.WriteArray(
                    items: admitted.Templates,
                    writeItem: WorldAuthorityCheckpointCodec.WriteAdmissionGrant
                );
                WriteSessionGrants(
                    grants: admitted.MintedGrants,
                    revoke: false,
                    writer: writer
                );

                break;
            case WorldServerEvent.SessionEmbodied embodied:
                writer.WriteByte(value: 17);
                WritePrincipal(
                    principal: embodied.Session,
                    writer: writer
                );
                writer.WriteInt32(value: embodied.BodyIndex);
                writer.WriteInt32(value: embodied.BodyGeneration);
                WriteSessionGrants(
                    grants: embodied.MintedGrants,
                    revoke: false,
                    writer: writer
                );

                break;
            case WorldServerEvent.SessionEnded ended:
                writer.WriteByte(value: 18);
                WritePrincipal(
                    principal: ended.Session,
                    writer: writer
                );
                WriteSessionGrants(
                    grants: ended.RevokedGrants,
                    revoke: true,
                    writer: writer
                );

                break;
            default:
                throw new WorldReplayCodecException(message: $"no .puckreplay encoding for server event '{serverEvent.GetType().Name}' as a session entry.");
        }
    }
    private static void WriteSessionGrants(WireWriter writer, IReadOnlyList<WorldGrant> grants, bool revoke) => writer.WriteArray(
        items: grants,
        writeItem: (w, grant) => WriteGrantLeaf(
            grant: grant,
            revoke: revoke,
            writer: w
        )
    );
}
