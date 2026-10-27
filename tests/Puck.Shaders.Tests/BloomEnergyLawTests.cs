namespace Puck.Shaders.Tests;

// CPU radiometric oracle for Shared/bloom-filter.hlsli and the pyramid: test integrated HDR energy,
// not a tonemapped PNG's mean. Constants alone cannot distinguish a conservative filter from edge clamping.
public sealed class BloomEnergyLawTests {
    [InlineData(320, 225)]
    [InlineData(160, 112)]
    [InlineData(64, 45)]
    [InlineData(1, 1)]
    [InlineData(1, 112)]
    [Theory]
    public void ReducingPlacementIntegratesTheWholeEnvelope(int width, int height) {
        var constant = new Image(640, 450, Enumerable.Repeat(count: (640 * 450), element: .5d).ToArray());
        var flat = Resample(Convolve(constant, down: false), width, height, linear: true, half: false, store: false);

        Assert.All(flat.Pixels, value => Assert.Equal(.5d, value, tolerance: 1e-6d));
        foreach (var (x, y) in new[] { (0, 0), (1, 1), (320, 225), (639, 449) }) {
            var input = new Image(Height: 450, Pixels: new double[(640 * 450)], Width: 640);

            input.Pixels[((y * 640) + x)] = 1d;
            var placed = Resample(Convolve(input, down: false), width, height, linear: true, half: false, store: false);
            // A destination pixel covers more of the same normalized image domain during minification.
            Assert.Equal(1d, (((placed.Pixels.Sum() * 640d) * 450d) / (width * height)), tolerance: 1e-4d);
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ConstantsAndTheReviewImpulseWitnessesKeepTheirMeanThroughEveryOctave(bool half) {
        var constant = new Image(1280, 900, Enumerable.Repeat(count: (1280 * 900), element: .5d).ToArray());
        var flat = Bloom(constant, low: false, half: half);

        Assert.All(flat.Pixels, value => Assert.Equal(.5d, value, tolerance: 1e-6d));
        foreach (var witness in new[] {
            (1280, 900, 0, 0, false), (1280, 900, 1, 1, false),
            (1280, 900, 640, 450, false), (1280, 900, 640, 100, false),
            (1024, 1024, 512, 512, false), (1280, 900, 641, 450, true),
            (1281, 901, 1280, 900, true),
        }) {
            var (width, height, x, y, low) = witness;
            var input = new Image(Height: height, Pixels: new double[(width * height)], Width: width);

            input.Pixels[((y * width) + x)] = 1d;
            var filtered = Bloom(half: half, low: low, source: input);
            // Half storage necessarily rounds tiny scatter tails. The FP32 path has no such storage floor.
            Assert.Equal(1d, filtered.Pixels.Sum(), tolerance: (half ? .005d : 1e-4d));
            Assert.Equal(1d, (.96d + (.04d * filtered.Pixels.Sum())), tolerance: (half ? .0002d : 4e-6d));
        }
    }
    [Fact]
    public void OddGridResamplingPreservesConstantsAndEveryInputsIntegratedContribution() {
        foreach (var (source, target, linear) in new[] {
            (225, 112, false), (900, 225, false), (112, 225, true), (225, 450, true), (1, 3, true),
            (640, 320, true), (450, 225, true), (640, 160, true), (450, 112, true), (640, 64, true), (450, 45, true),
            (640, 1, true), (450, 1, true),
        }) {
            var weights = Weights(linear: linear, source: source, target: target);
            var columns = new double[source];

            foreach (var row in weights) {
                Assert.Equal(1d, row.Sum(selector: tap => tap.Weight), tolerance: 1e-12d);
                foreach (var tap in row) { columns[Reflect(cell: tap.Index, extent: source)] += ((tap.Weight * source) / target); }
            }
            Assert.All(columns, energy => Assert.Equal(1d, energy, tolerance: 1e-11d));
            var packed = PairedWeights(linear: linear, source: source, target: target);

            for (var pixel = 0; (pixel < target); pixel++) {
                var expected = new double[source];
                var actual = new double[source];

                foreach (var tap in weights[pixel]) { expected[Reflect(cell: tap.Index, extent: source)] += tap.Weight; }
                foreach (var tap in packed[pixel]) { actual[Reflect(cell: tap.Index, extent: source)] += tap.Weight; }
                for (var cell = 0; (cell < source); cell++) {
                    Assert.Equal(expected[cell], actual[cell], tolerance: 8e-5d);
                }
            }
        }
    }

    private readonly record struct Tap(int Index, double Weight);
    private sealed record Image(int Width, int Height, double[] Pixels) {
        public double At(int x, int y) => Pixels[((Reflect(cell: y, extent: Height) * Width) + Reflect(cell: x, extent: Width))];
    }

    private static int Reflect(int cell, int extent) {
        cell = (((cell % (2 * extent)) + (2 * extent)) % (2 * extent));
        return ((cell < extent) ? cell : (((2 * extent) - cell) - 1));
    }
    private static double HatIntegral(double x) => ((x < 0d)
        ? (.5d * Math.Pow(x: Math.Clamp(max: 1d, min: 0d, value: (x + 1d)), y: 2d))
        : (1d - (.5d * Math.Pow(x: Math.Clamp(max: 1d, min: 0d, value: (1d - x)), y: 2d))));
    // Independent quadrature on cell/basis overlaps. The shader pairs adjacent contributions into
    // hardware-linear samples, which must evaluate this same operator, including the final place tent.
    private static Tap[][] Weights(int source, int target, bool linear) {
        var result = new Tap[target][];

        for (var pixel = 0; (pixel < target); pixel++) {
            var left = ((((double)pixel) * source) / target);
            var right = ((((double)(pixel + 1)) * source) / target);
            var first = ((int)Math.Floor(d: (left - (linear ? .5d : 0d))));
            var last = ((int)Math.Ceiling(a: (right - (linear ? .5d : 0d))));
            var taps = new List<Tap>();

            for (var cell = first; (cell <= last); cell++) {
                var weight = (linear
                    ? (HatIntegral(x: ((right - .5d) - cell)) - HatIntegral(x: ((left - .5d) - cell)))
                    : Math.Max(val1: 0d, val2: (Math.Min(val1: right, val2: (cell + 1d)) - Math.Max(val1: left, val2: cell))));

                if (weight > 0d) { taps.Add(item: new Tap(Index: cell, Weight: (weight / (right - left)))); }
            }
            result[pixel] = [.. taps];
        }
        return result;
    }
    // The actual shader's FP32 packing into hardware-linear sample coordinates. Check it against the
    // independent overlap quadrature above, and use it for the end-to-end review witnesses below.
    private static Tap[][] PairedWeights(int source, int target, bool linear) {
        var result = new Tap[target][];

        for (var pixel = 0; (pixel < target); pixel++) {
            var numerator = (pixel * source);
            var first = (numerator / target);
            var left = (((float)(numerator % target)) / target);
            var length = (((float)source) / target);

            if (linear) {
                if (left < .5f) { first--; left += 1f; }
                left -= .5f;
            }
            var right = (left + length);
            var taps = new List<Tap>();

            var pairs = (linear ? (int)MathF.Ceiling(x: ((right + 1f) * .5f)) : 3);

            for (var pair = 0; (pair < pairs); pair++) {
                var cell = (pair * 2);
                var a = (linear ? (float)(HatIntegral(x: (right - cell)) - HatIntegral(x: (left - cell))) :
                    MathF.Max(x: 0f, y: (MathF.Min(x: right, y: (cell + 1f)) - MathF.Max(x: left, y: cell))));
                var b = (linear ? (float)(HatIntegral(x: ((right - cell) - 1f)) - HatIntegral(x: ((left - cell) - 1f))) :
                    MathF.Max(x: 0f, y: (MathF.Min(x: right, y: (cell + 2f)) - MathF.Max(x: left, y: (cell + 1f)))));
                var weight = (a + b);

                if (weight <= 0f) { continue; }
                var position = ((((first + cell) + .5f) + (b / weight)) / source);
                var sample = ((position * source) - .5f);
                var index = ((int)MathF.Floor(x: sample));
                var fraction = (sample - index);

                taps.Add(item: new Tap(Index: index, Weight: ((weight / length) * (1f - fraction))));
                taps.Add(item: new Tap(Index: (index + 1), Weight: ((weight / length) * fraction)));
            }
            result[pixel] = [.. taps];
        }
        return result;
    }
    private static Image Resample(Image source, int width, int height, bool linear, bool half, Image? fine = null, double mix = 1d, bool store = true) {
        var result = new Image(Height: height, Pixels: new double[(width * height)], Width: width);
        var xs = PairedWeights(source.Width, width, linear);
        var ys = PairedWeights(source.Height, height, linear);

        for (var y = 0; (y < height); y++) {
            for (var x = 0; (x < width); x++) {
                var value = 0d;

                foreach (var ty in ys[y]) {
                    foreach (var tx in xs[x]) { value += ((tx.Weight * ty.Weight) * source.At(x: tx.Index, y: ty.Index)); }
                }
                if (fine is not null) { value = (((1d - mix) * fine.Pixels[((y * width) + x)]) + (mix * value)); }
                result.Pixels[((y * width) + x)] = (!store ? value : (half ? (double)((Half)value) : (float)value));
            }
        }
        return result;
    }
    private static Image Convolve(Image source, bool down, double radius = 1d) {
        var result = new Image(source.Width, source.Height, new double[source.Pixels.Length]);
        (int X, int Y, double Weight)[] kernel = (down
            ? [(0, 0, .125d), (-2, -2, .03125d), (2, -2, .03125d), (-2, 2, .03125d), (2, 2, .03125d),
                (-2, 0, .0625d), (2, 0, .0625d), (0, -2, .0625d), (0, 2, .0625d),
                (-1, -1, .125d), (1, -1, .125d), (-1, 1, .125d), (1, 1, .125d)]
            : [(-1, -1, .0625d), (0, -1, .125d), (1, -1, .0625d), (-1, 0, .125d), (0, 0, .25d),
                (1, 0, .125d), (-1, 1, .0625d), (0, 1, .125d), (1, 1, .0625d)]);
        var spacing = (down ? 1 : (int)Math.Floor(d: radius));
        var fraction = (down ? 0d : (radius - spacing));

        for (var y = 0; (y < source.Height); y++) {
            for (var x = 0; (x < source.Width); x++) {
                var value = 0d;

                foreach (var tap in kernel) {
                    value += (tap.Weight * (((1d - fraction) * source.At(x: (x + (tap.X * spacing)), y: (y + (tap.Y * spacing)))) +
                        (fraction * source.At(x: (x + (tap.X * (spacing + 1))), y: (y + (tap.Y * (spacing + 1)))))));
                }
                result.Pixels[((y * source.Width) + x)] = value;
            }
        }
        return result;
    }
    private static Image Bloom(Image source, bool low, bool half) {
        var levels = new Image[(low ? 5 : 6)];
        var next = source;

        for (var level = 0; (level < levels.Length); level++) {
            var divisor = Math.Pow(x: 2d, y: (level + (low ? 2 : 1)));

            next = levels[level] = Resample(Convolve(next, down: true), Math.Max(val1: 1, val2: ((int)Math.Round(a: (source.Width / divisor)))),
                Math.Max(val1: 1, val2: ((int)Math.Round(a: (source.Height / divisor)))), linear: false, half: half);
        }
        var radius = Math.Max(val1: .5d, val2: ((.08d * Math.Min(val1: source.Width, val2: source.Height)) / 64d));

        for (var level = (levels.Length - 2); (level >= 0); level--) {
            var fine = levels[level];
            var mix = (.65d * Math.Clamp(((.08d * Math.Min(val1: fine.Width, val2: fine.Height)) / 2d), 0d, 1d));

            next = Resample(Convolve(next, down: false, radius), fine.Width, fine.Height, linear: true, half: half, fine: fine, mix: mix);
        }
        return Resample(Convolve(next, down: false), source.Width, source.Height, linear: true, half: half, store: false);
    }
}
