using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.HumbleGamingBrick.Forge;

public sealed partial class HgbCartridgeCompiler {
    // Leaves 1 in the accumulator while the infrared receiver sees light, and 0 otherwise. Every read arms RP's
    // data-read-enable bits with the lamp off, since a lit lamp would read back as its own light.
    private static void Light(Sm83Emitter emitter) {
        emitter.LoadAImmediate(value: 0xC0);
        emitter.StoreAToHighPage(port: Hw.PortInfrared);
        emitter.LoadAFromHighPage(port: Hw.PortInfrared);
        emitter.ArithmeticImmediate(
            op: AluOp.And,
            value: 0x02
        );
        emitter.RotateRightCircularA();
        emitter.ArithmeticImmediate(
            op: AluOp.Xor,
            value: 0x01
        );
    }
    // Leaves 1 in the accumulator while the button satisfies the mode, and 0 otherwise.
    private static void Button(Sm83Emitter emitter, string button, string mode) {
        var zero = emitter.NewLabel();
        var done = emitter.NewLabel();

        emitter.LoadAFromAddress(address: ((mode == "pressed")
            ? FrameworkMemoryMap.InputPressed
            : FrameworkMemoryMap.InputHeld));
        if (mode == "released") {
            emitter.ComplementA();
            emitter.Load(
                destination: Reg8.B,
                source: Reg8.A
            );
            emitter.LoadAFromAddress(address: HeldInputAddress);
            emitter.Arithmetic(
                op: AluOp.And,
                source: Reg8.B
            );
        }

        emitter.ArithmeticImmediate(
            op: AluOp.And,
            value: Key(key: button)
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: zero
        );
        emitter.LoadAImmediate(value: 1);
        emitter.JumpRelative(label: done);
        emitter.MarkLabel(label: zero);
        emitter.XorA();
        emitter.MarkLabel(label: done);
    }
}
