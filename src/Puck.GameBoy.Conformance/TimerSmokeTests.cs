namespace Puck.GameBoy.Conformance;

/// <summary>
/// Self-contained timer checks driving a <see cref="Timer"/> directly: the divider increment and reset, the
/// <c>TIMA</c> rate selected by <c>TAC</c>, the overflow reload from <c>TMA</c> with the timer interrupt, and
/// the falling-edge quirk where writing <c>DIV</c> while the selected bit is high ticks <c>TIMA</c>. These guard
/// the edge-detector model ahead of the mooneye timer suite (which needs the external asset path).
/// </summary>
internal static class TimerSmokeTests {
    private const ushort Divider = 0xFF04;
    private const ushort TimerCounter = 0xFF05;
    private const ushort TimerModulo = 0xFF06;
    private const ushort TimerControl = 0xFF07;

    public static IReadOnlyList<(string Name, Func<string?> Run)> All =>
        [
            ("DIV high byte increments every 256 T-cycles", static () => {
                var (timer, _) = Make();

                timer.Step(tCycles: 256);

                return (timer.ReadRegister(address: Divider) == 1)
                    ? null
                    : $"DIV=0x{timer.ReadRegister(address: Divider):X2} (expected 0x01)";
            }),
            ("writing DIV resets the counter", static () => {
                var (timer, _) = Make();

                timer.Step(tCycles: 300);
                timer.WriteRegister(address: Divider, value: 0xFF);

                return (timer.ReadRegister(address: Divider) == 0)
                    ? null
                    : $"DIV=0x{timer.ReadRegister(address: Divider):X2} (expected 0x00)";
            }),
            ("TIMA increments at the TAC-selected rate", static () => {
                var (timer, _) = Make();

                // TAC = enable | select 01 -> increment every 16 T-cycles.
                timer.WriteRegister(address: TimerControl, value: 0x05);
                timer.Step(tCycles: 16);

                var afterOne = timer.ReadRegister(address: TimerCounter);

                timer.Step(tCycles: 16);

                var afterTwo = timer.ReadRegister(address: TimerCounter);

                return ((afterOne == 1) && (afterTwo == 2))
                    ? null
                    : $"TIMA after 16={afterOne}, after 32={afterTwo} (expected 1, 2)";
            }),
            ("TIMA overflow reloads TMA and raises the timer interrupt", static () => {
                var (timer, interrupts) = Make();

                timer.WriteRegister(address: TimerControl, value: 0x05);
                timer.WriteRegister(address: TimerModulo, value: 0xAB);
                timer.WriteRegister(address: TimerCounter, value: 0xFF);
                timer.Step(tCycles: 16);
                timer.Step(tCycles: 8);

                var reloaded = timer.ReadRegister(address: TimerCounter);
                var interruptRaised = ((interrupts.InterruptFlag & (byte)InterruptKind.Timer) != 0);

                return ((reloaded == 0xAB) && interruptRaised)
                    ? null
                    : $"TIMA=0x{reloaded:X2} (want 0xAB), timerIF={interruptRaised}";
            }),
            ("writing DIV while the selected bit is high ticks TIMA", static () => {
                var (timer, _) = Make();

                // Selected bit (3) is high while the counter is in 8..15; resetting DIV drops it -> falling edge.
                timer.WriteRegister(address: TimerControl, value: 0x05);
                timer.Step(tCycles: 8);
                timer.WriteRegister(address: Divider, value: 0x00);

                return (timer.ReadRegister(address: TimerCounter) == 1)
                    ? null
                    : $"TIMA=0x{timer.ReadRegister(address: TimerCounter):X2} (expected 0x01 from DIV-reset edge)";
            }),
            ("TIMA write on the reload cycle is ignored (TMA wins)", static () => {
                var timer = OverflowedTimer(modulo: 0x77);

                // The reload lands during this machine cycle; the coincident write must be ignored.
                timer.Step(tCycles: 4);
                timer.WriteRegister(address: TimerCounter, value: 0x99);

                return (timer.ReadRegister(address: TimerCounter) == 0x77)
                    ? null
                    : $"TIMA=0x{timer.ReadRegister(address: TimerCounter):X2} (expected 0x77 = TMA)";
            }),
            ("TMA write on the reload cycle lands in TIMA", static () => {
                var timer = OverflowedTimer(modulo: 0x77);

                timer.Step(tCycles: 4);
                timer.WriteRegister(address: TimerModulo, value: 0x88);

                return (timer.ReadRegister(address: TimerCounter) == 0x88)
                    ? null
                    : $"TIMA=0x{timer.ReadRegister(address: TimerCounter):X2} (expected 0x88 = new TMA)";
            }),
            ("TIMA write one cycle before reload aborts it", static () => {
                var timer = OverflowedTimer(modulo: 0x77);

                // Write during the delay window (before the reload cycle): the write takes effect and cancels reload.
                timer.WriteRegister(address: TimerCounter, value: 0x99);
                timer.Step(tCycles: 4);

                return (timer.ReadRegister(address: TimerCounter) == 0x99)
                    ? null
                    : $"TIMA=0x{timer.ReadRegister(address: TimerCounter):X2} (expected 0x99, reload aborted)";
            }),
        ];

    // Returns a timer that has just overflowed TIMA (value 0x00, reload pending) with the given modulo, by
    // driving 16 T-cycles at the select-01 rate from TIMA = 0xFF.
    private static Timer OverflowedTimer(byte modulo) {
        var (timer, _) = Make();

        timer.WriteRegister(address: TimerControl, value: 0x05);
        timer.WriteRegister(address: TimerModulo, value: modulo);
        timer.WriteRegister(address: TimerCounter, value: 0xFF);

        for (var cycle = 0; cycle < 4; cycle += 1) {
            timer.Step(tCycles: 4);
        }

        return timer;
    }

    private static (Timer Timer, InterruptController Interrupts) Make() {
        var interrupts = new InterruptController();
        var timer = new Timer(interrupts: interrupts);

        return (timer, interrupts);
    }
}
