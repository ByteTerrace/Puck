namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // Seeded slider and occupancy words on an eight-by-eight grid in all eight directions and on a 64-cell strip both
    // ways, against a walk of each slider's ray cell by cell that calls no OccludedFill member.
    public static string? OccludedFillSlideVsRayWalk() {
        var random = Pcg32XshRr.Create(state: 0x5EED_B0A2DUL, stream: 7UL);
        (int Dx, int Dy)[] grid = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1)];

        for (var trial = 0; (trial < 256); ++trial) {
            var sliders = (Word(random: ref random) & Word(random: ref random));
            var occupied = (Word(random: ref random) & Word(random: ref random)) | sliders;

            foreach (var (dx, dy) in grid) {
                var landing = 0UL;

                for (var cell = 0; (cell < 64); ++cell) {
                    if (OnGrid(x: (cell % 8) + dx, y: (cell / 8) + dy)) { landing |= (1UL << (cell + dx + (8 * dy))); }
                }
                var expected = Walk(sliders: sliders, occupied: occupied, next: cell => (OnGrid(x: (cell % 8) + dx, y: (cell / 8) + dy) ? (cell + dx + (8 * dy)) : -1));
                var actual = OccludedFill.Slide(empty: ~occupied, landing: landing, sliders: sliders, step: (dx + (8 * dy)));

                if (actual != expected) { return $"grid step ({dx},{dy}) sliders {sliders:X16} occupied {occupied:X16}: {actual:X16}, expected {expected:X16}"; }
            }
            foreach (var step in (int[])[1, -1]) {
                var expected = Walk(sliders: sliders, occupied: occupied, next: cell => (((cell + step) is >= 0 and < 64) ? (cell + step) : -1));
                var landing = ((step > 0) ? ~1UL : ~(1UL << 63));
                var actual = OccludedFill.Slide(empty: ~occupied, landing: landing, sliders: sliders, step: step);

                if (actual != expected) { return $"strip step {step} sliders {sliders:X16} occupied {occupied:X16}: {actual:X16}, expected {expected:X16}"; }
            }
        }
        foreach (var refused in (Action[])[() => OccludedFill.Slide(empty: 0UL, landing: 0UL, sliders: 1UL, step: 0), () => OccludedFill.Slide(empty: 0UL, landing: 0UL, sliders: 1UL, step: 64)]) {
            try {
                refused();

                return "a step outside -63 to 63, or zero, was accepted";
            } catch (ArgumentOutOfRangeException) { }
        }
        return null;

        static bool OnGrid(int x, int y) => ((x is >= 0 and < 8) && (y is >= 0 and < 8));
        static ulong Word(ref Pcg32XshRr random) => ((((ulong)random.NextUInt32()) << 32) | random.NextUInt32());
        static ulong Walk(ulong sliders, ulong occupied, Func<int, int> next) {
            var attacked = 0UL;

            for (var origin = 0; (origin < 64); ++origin) {
                if (((sliders >> origin) & 1UL) == 0UL) { continue; }
                for (var cell = next(arg: origin); (cell >= 0); cell = next(arg: cell)) {
                    attacked |= (1UL << cell);
                    if (((occupied >> cell) & 1UL) != 0UL) { break; }
                }
            }
            return attacked;
        }
    }
}
