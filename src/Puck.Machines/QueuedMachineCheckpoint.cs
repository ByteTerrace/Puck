using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions.Machines;
using Puck.Hosting;

namespace Puck.Machines;

internal sealed record QueuedMachineCheckpoint(string Identity, byte[] CoreState, ulong CycleRemainder,
    ulong CycleScale, long CompletedSteps, int FastForwardFactor, int RunaheadFrames) {
    private const string Format = "puck.queued-machine.v4";
    private const int MaximumBytes = ((128 * 1024) * 1024);

    public static (QueuedMachineCheckpoint Checkpoint, MachinePads Inputs, int Seats) Decode(ReadOnlyMemory<byte> bytes) {
        if (
            (bytes.Length is < 32 or > MaximumBytes) ||
            !SHA256.HashData(source: bytes.Span[..^32]).AsSpan().SequenceEqual(other: bytes.Span[^32..])
        ) {
            throw new InvalidDataException(message: "machine checkpoint is truncated or its content hash does not match");
        }
        using var stream = new MemoryStream(
            bytes[..^32].ToArray(),
            writable: false
        );
        using var reader = new BinaryReader(
            stream,
            Encoding.UTF8
        );

        if (reader.ReadString() != Format) { throw new InvalidDataException(message: "machine checkpoint format is unsupported"); }
        var identity = reader.ReadString();
        var remainder = reader.ReadUInt64();
        var scale = reader.ReadUInt64();
        var completed = reader.ReadInt64();
        var factor = reader.ReadInt32();
        var runahead = reader.ReadInt32();
        var seats = reader.ReadInt32();

        if (seats is < 1 or > MachinePads.MaxSeats) {
            throw new InvalidDataException(message: "machine checkpoint declares an unsupported seat count");
        }
        var inputs = MachinePads.Neutral;

        for (var seat = 0; (seat < seats); ++seat) {
            inputs[seat] = ReadPad(reader: reader);
        }
        var count = reader.ReadInt32();

        if (
            string.IsNullOrWhiteSpace(value: identity) ||
            (identity.Length > 256) ||
            (scale is 0UL or > (((ulong)long.MaxValue) / EngineTicks.PerSecond)) ||
            (remainder >= (EngineTicks.PerSecond * scale)) ||
            (completed < 0) ||
            (factor is < 1 or > MachineTimeTravel<MachinePads>.MaxFastForwardFactor) ||
            (runahead is < 0 or > MachineTimeTravel<MachinePads>.MaxRunaheadFrames) ||
            (count <= 0) ||
            (count != (stream.Length - stream.Position))
        ) {
            throw new InvalidDataException(message: "machine checkpoint has invalid identity, pacing, or payload bounds");
        }
        return (new(
            identity,
            reader.ReadBytes(count: count),
            remainder,
            scale,
            completed,
            factor,
            runahead
        ), inputs, seats);
    }
    public byte[] Encode(in MachinePads inputs, int seats) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: seats
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: MachinePads.MaxSeats,
            value: seats
        );

        using var buffer = new MemoryStream();

        using (var writer = new BinaryWriter(
            buffer,
            Encoding.UTF8,
            leaveOpen: true
        )) {
            writer.Write(value: Format);
            writer.Write(value: Identity);
            writer.Write(value: CycleRemainder);
            writer.Write(value: CycleScale);
            writer.Write(value: CompletedSteps);
            writer.Write(value: FastForwardFactor);
            writer.Write(value: RunaheadFrames);
            writer.Write(value: seats);
            for (var seat = 0; (seat < seats); ++seat) {
                WritePad(
                    pad: inputs[seat],
                    writer: writer
                );
            }
            writer.Write(value: CoreState.Length);
            writer.Write(buffer: CoreState);
        }
        if (buffer.Length > (MaximumBytes - 32)) { throw new InvalidDataException(message: "machine checkpoint exceeds the supported size"); }
        var hash = SHA256.HashData(source: buffer.GetBuffer().AsSpan(
            0,
            ((int)buffer.Length)
        ));

        buffer.Write(buffer: hash);
        return buffer.ToArray();
    }
    private static MachinePadState ReadPad(BinaryReader reader) {
        var buttons = ((MachineButtons)reader.ReadUInt32());
        var left = new Vector2(
            x: reader.ReadSingle(),
            y: reader.ReadSingle()
        );
        var right = new Vector2(
            x: reader.ReadSingle(),
            y: reader.ReadSingle()
        );
        var leftTrigger = reader.ReadSingle(); var rightTrigger = reader.ReadSingle();
        var tilt = new Vector2(
            x: reader.ReadSingle(),
            y: reader.ReadSingle()
        );
        var light = reader.ReadByte();
        var onScreen = reader.ReadBoolean();
        var pointerX = reader.ReadUInt16();
        var pointerY = reader.ReadUInt16();

        if (
            !onScreen &&
            ((pointerX != 0) || (pointerY != 0))
        ) {
            throw new InvalidDataException(message: "machine checkpoint carries an off-screen pointer with a position");
        }

        return new MachinePadState(
            Buttons: buttons,
            LeftStick: left,
            LeftTrigger: leftTrigger,
            LightLevel: light,
            Pointer: (onScreen
                ? new MachinePointer(
                    x: pointerX,
                    y: pointerY
                )
                : MachinePointer.Off
            ),
            RightStick: right,
            RightTrigger: rightTrigger,
            Tilt: tilt
        );
    }
    private static void WritePad(in MachinePadState pad, BinaryWriter writer) {
        writer.Write(value: ((uint)pad.Buttons));
        writer.Write(value: pad.LeftStick.X); writer.Write(value: pad.LeftStick.Y);
        writer.Write(value: pad.RightStick.X); writer.Write(value: pad.RightStick.Y);
        writer.Write(value: pad.LeftTrigger); writer.Write(value: pad.RightTrigger);
        writer.Write(value: pad.Tilt.X); writer.Write(value: pad.Tilt.Y);
        writer.Write(value: pad.LightLevel);
        writer.Write(value: pad.Pointer.OnScreen); writer.Write(value: pad.Pointer.X); writer.Write(value: pad.Pointer.Y);
    }
}
