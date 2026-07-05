using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the Pocket Camera (M64282FP) sensor captures authentically. Driving the mapper's register protocol
/// directly against a known sensor plane, this proves the whole capture path is real and deterministic: a flat readout
/// with a uniform dither matrix packs to the exact 2bpp tiles the four shades demand at <c>0xA100</c>; halving the
/// exposure register darkens a pixel into the next shade; the 4×4 threshold matrix places a per-column dither pattern
/// into the tile bytes; the busy bit stays set for the real exposure-dependent window and then clears; enabling edge
/// enhancement changes a gradient's output; and a capture snapshotted mid-busy resumes to a byte-identical image. The
/// only non-deterministic ingredient of a real camera — the live readout — is replaced here by a fixed plane, so the
/// entire capture is reproducible. (Bit-exact parity against SameBoy/mGBA is evidence, not a gate; this battery is the
/// gate.)
/// </summary>
internal sealed class CameraCaptureStage : IPostStage {
    private const ushort CameraBlockSelect = 0x4000; // WriteControl: bit 4 maps the camera registers over the RAM window
    private const ushort RamEnable = 0x0000;         // WriteControl: 0x0A enables the RAM window
    private const int DitherByteCount = 48;

    /// <inheritdoc/>
    public string Name =>
        "camera-capture";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (CheckUniformShades() is { } uniformFailure) {
            return PostStageOutcome.Fail(detail: uniformFailure);
        }

        if (CheckExposureResponds() is { } exposureFailure) {
            return PostStageOutcome.Fail(detail: exposureFailure);
        }

        if (CheckDitherMatrix() is { } ditherFailure) {
            return PostStageOutcome.Fail(detail: ditherFailure);
        }

        if (CheckBusyTiming() is { } busyFailure) {
            return PostStageOutcome.Fail(detail: busyFailure);
        }

        if (CheckEdgeEnhancement() is { } edgeFailure) {
            return PostStageOutcome.Fail(detail: edgeFailure);
        }

        if (CheckSnapshotMidCapture() is { } snapshotFailure) {
            return PostStageOutcome.Fail(detail: snapshotFailure);
        }

        return PostStageOutcome.Pass(detail: $"M64282FP capture: uniform shades pack exactly to 2bpp tiles at 0xA100, exposure and the 4×4 dither matrix drive the output, the busy window is exposure-dependent, edge enhancement responds, and a mid-busy snapshot resumes to a byte-identical {SensorImage.TiledByteCount}-byte image");
    }

    // Each shade a flat sensor + uniform dither matrix can produce must pack to the exact bitplane bytes: shade 0 -> both
    // planes 0x00, shade 1 -> low 0xFF/high 0x00, shade 2 -> low 0x00/high 0xFF, shade 3 -> both 0xFF. Gain index 4 is
    // exactly 1.0 and exposure 0x1000 divides out, so the processed colour equals the raw sensor value.
    private static string? CheckUniformShades() {
        // (sensor value, expected shade) with the uniform thresholds {50,150,200}: 25<50 -> 3, 100<150 -> 2, 175<200 ->
        // 1, 240 -> 0.
        (byte Value, int Shade)[] cases = [(25, 3), (100, 2), (175, 1), (240, 0)];
        var dither = UniformDither(threshold0: 50, threshold1: 150, threshold2: 200);

        foreach (var (value, shade) in cases) {
            using var machine = BuildCamera(sensor: new FlatSensor(value: value));
            var cartridge = Cartridge(machine: machine);

            Configure(cartridge: cartridge, gain: 4, exposure: 0x1000, edge: false, edgeRatio: 0x00, dither: dither);
            Trigger(machine: machine, cartridge: cartridge);

            var image = ReadImage(cartridge: cartridge);
            var expectedLow = (byte)(((shade & 0x01) != 0) ? 0xFF : 0x00);
            var expectedHigh = (byte)(((shade & 0x02) != 0) ? 0xFF : 0x00);

            for (var index = 0; (index < image.Length); ++index) {
                var expected = (((index & 1) == 0) ? expectedLow : expectedHigh);

                if (image[index] != expected) {
                    return $"flat sensor {value} (shade {shade}) packed byte {index} = 0x{image[index]:X2}, expected 0x{expected:X2}";
                }
            }
        }

        return null;
    }

    // Lowering the exposure register scales the processed colour down, dropping a mid-value pixel across a threshold into
    // a darker shade — the auto-exposure feedback a real Camera ROM relies on.
    private static string? CheckExposureResponds() {
        var dither = UniformDither(threshold0: 50, threshold1: 150, threshold2: 200);

        // colour = 100 * exposure / 0x1000: at 0x1000 -> 100 (shade 2), at 0x0400 -> 25 (shade 3).
        var bright = CaptureFlat(value: 100, gain: 4, exposure: 0x1000, dither: dither);
        var dark = CaptureFlat(value: 100, gain: 4, exposure: 0x0400, dither: dither);

        if (bright[1] != 0xFF) {
            return $"bright capture: expected high-plane 0xFF (shade 2), got 0x{bright[1]:X2}";
        }

        if ((dark[0] != 0xFF) || (dark[1] != 0xFF)) {
            return $"dark capture: expected both planes 0xFF (shade 3), got low 0x{dark[0]:X2} high 0x{dark[1]:X2}";
        }

        return null;
    }

    // The dither matrix is indexed by (x&3, y&3). With cells whose x&3==0 forced to shade 3 and the rest to shade 0, each
    // tile row must pack to 0x88 on both planes (bits set at columns 0 and 4) — proving the ((x&3)+(y&3)*4)*3 register
    // mapping and the MSB-left 2bpp packing.
    private static string? CheckDitherMatrix() {
        var dither = new byte[DitherByteCount];

        for (var cellY = 0; (cellY < 4); ++cellY) {
            for (var cellX = 0; (cellX < 4); ++cellX) {
                var cellBase = (((cellX + (cellY * 4)) * 3));
                // colour is 100 everywhere; thresholds {200,200,200} -> shade 3, {50,50,50} -> shade 0.
                var threshold = (byte)((cellX == 0) ? 200 : 50);

                dither[cellBase] = threshold;
                dither[cellBase + 1] = threshold;
                dither[cellBase + 2] = threshold;
            }
        }

        using var machine = BuildCamera(sensor: new FlatSensor(value: 100));
        var cartridge = Cartridge(machine: machine);

        Configure(cartridge: cartridge, gain: 4, exposure: 0x1000, edge: false, edgeRatio: 0x00, dither: dither);
        Trigger(machine: machine, cartridge: cartridge);

        var image = ReadImage(cartridge: cartridge);

        for (var index = 0; (index < image.Length); ++index) {
            if (image[index] != 0x88) {
                return $"dither-matrix capture: byte {index} = 0x{image[index]:X2}, expected 0x88 (columns 0 and 4 dark)";
            }
        }

        return null;
    }

    // The busy bit (register 0, bit 0) reads set immediately after a trigger and stays set for the exposure-dependent
    // window (129792 + 2048 + exposure*64 dots at gain with 1-D off), then clears — the poll loop a ROM spins on.
    private static string? CheckBusyTiming() {
        const int Exposure = 0x0100;
        var busyDots = (129792 + 2048 + (Exposure * 64));

        using var machine = BuildCamera(sensor: new FlatSensor(value: 128));
        var cartridge = Cartridge(machine: machine);

        Configure(cartridge: cartridge, gain: 4, exposure: Exposure, edge: false, edgeRatio: 0x00, dither: UniformDither(threshold0: 64, threshold1: 128, threshold2: 192));
        Trigger(machine: machine, cartridge: cartridge);

        if ((ReadShootRegister(cartridge: cartridge) & 0x01) == 0) {
            return "the busy bit was clear immediately after a trigger";
        }

        // One frame is short of the busy window, so it must still be busy.
        PostMachine.RunFrames(instance: machine, frames: 1);

        if ((ReadShootRegister(cartridge: cartridge) & 0x01) == 0) {
            return $"the busy bit cleared after 1 frame ({PostMachine.TCyclesPerFrame} dots) but the window is {busyDots} dots";
        }

        // Run well past the window; the shoot must now be complete.
        var framesToClear = (((busyDots / PostMachine.TCyclesPerFrame) + 2));

        PostMachine.RunFrames(instance: machine, frames: framesToClear);

        if ((ReadShootRegister(cartridge: cartridge) & 0x01) != 0) {
            return $"the busy bit was still set after {framesToClear + 1} frames (window {busyDots} dots)";
        }

        return null;
    }

    // Edge enhancement combines each pixel with its four neighbours, so on a non-flat (gradient) sensor it must change
    // the captured image versus the same capture with enhancement disabled.
    private static string? CheckEdgeEnhancement() {
        var dither = UniformDither(threshold0: 64, threshold1: 128, threshold2: 192);

        using var plain = BuildCamera(sensor: new GradientCameraSensor());
        var plainCartridge = Cartridge(machine: plain);

        Configure(cartridge: plainCartridge, gain: 4, exposure: 0x1000, edge: false, edgeRatio: 0x40, dither: dither);
        Trigger(machine: plain, cartridge: plainCartridge);

        var plainImage = ReadImage(cartridge: plainCartridge);

        using var sharpened = BuildCamera(sensor: new GradientCameraSensor());
        var sharpenedCartridge = Cartridge(machine: sharpened);

        Configure(cartridge: sharpenedCartridge, gain: 4, exposure: 0x1000, edge: true, edgeRatio: 0x40, dither: dither);
        Trigger(machine: sharpened, cartridge: sharpenedCartridge);

        var sharpenedImage = ReadImage(cartridge: sharpenedCartridge);

        return plainImage.AsSpan().SequenceEqual(other: sharpenedImage)
            ? "enabling edge enhancement did not change a gradient capture"
            : null;
    }

    // A capture triggered and then snapshotted while still busy must, after restore, resume its countdown and produce a
    // byte-identical image — the deposited tiles and the busy countdown both survive the snapshot.
    private static string? CheckSnapshotMidCapture() {
        const int Exposure = 0x0100;
        var dither = UniformDither(threshold0: 60, threshold1: 120, threshold2: 180);
        var busyDots = (129792 + 2048 + (Exposure * 64));
        var framesToClear = (((busyDots / PostMachine.TCyclesPerFrame) + 2));

        using var machine = BuildCamera(sensor: new FlatSensor(value: 150));
        var cartridge = Cartridge(machine: machine);

        Configure(cartridge: cartridge, gain: 4, exposure: Exposure, edge: false, edgeRatio: 0x00, dither: dither);
        Trigger(machine: machine, cartridge: cartridge);

        // Snapshot one frame in, while still busy.
        PostMachine.RunFrames(instance: machine, frames: 1);

        var midCapture = machine.Machine.Snapshot();

        PostMachine.RunFrames(instance: machine, frames: framesToClear);

        var firstImage = ReadImage(cartridge: cartridge);

        machine.Machine.Restore(snapshot: midCapture);

        PostMachine.RunFrames(instance: machine, frames: framesToClear);

        var secondImage = ReadImage(cartridge: cartridge);

        return firstImage.AsSpan().SequenceEqual(other: secondImage)
            ? null
            : "a capture snapshotted mid-busy produced a different image after restore";
    }

    private static MachineInstance BuildCamera(ICameraSensor sensor) {
        // Header type 0xFC = Pocket Camera; RAM-size 0x04 = 128 KiB (16 banks), as the real cart carries.
        var machine = PostMachine.Build(model: ConsoleModel.Dmg, rom: SyntheticRom.Create(cartridgeType: 0xFC, ramSize: 0x04));

        ((PocketCameraCartridge)machine.GetRequiredService<ICartridge>()).Sensor = sensor;

        return machine;
    }

    private static PocketCameraCartridge Cartridge(MachineInstance machine) =>
        (PocketCameraCartridge)machine.GetRequiredService<ICartridge>();

    private static byte[] CaptureFlat(byte value, byte gain, int exposure, byte[] dither) {
        using var machine = BuildCamera(sensor: new FlatSensor(value: value));
        var cartridge = Cartridge(machine: machine);

        Configure(cartridge: cartridge, gain: gain, exposure: exposure, edge: false, edgeRatio: 0x00, dither: dither);
        Trigger(machine: machine, cartridge: cartridge);

        return ReadImage(cartridge: cartridge);
    }

    // Selects the camera block and writes the M64282FP registers (gain/edge flag, 16-bit exposure, edge ratio, and the
    // 48-byte dither matrix) — everything except the shoot trigger.
    private static void Configure(PocketCameraCartridge cartridge, byte gain, int exposure, bool edge, byte edgeRatio, byte[] dither) {
        cartridge.WriteControl(address: CameraBlockSelect, value: 0x10);
        cartridge.WriteRam(address: 0xA001, value: (byte)((gain & 0x1F) | (edge ? 0xE0 : 0x00)));
        cartridge.WriteRam(address: 0xA002, value: (byte)((exposure >> 8) & 0xFF));
        cartridge.WriteRam(address: 0xA003, value: (byte)(exposure & 0xFF));
        cartridge.WriteRam(address: 0xA004, value: edgeRatio);

        for (var index = 0; (index < DitherByteCount); ++index) {
            cartridge.WriteRam(address: (ushort)(0xA006 + index), value: dither[index]);
        }
    }

    private static void Trigger(MachineInstance machine, PocketCameraCartridge cartridge) {
        cartridge.WriteControl(address: CameraBlockSelect, value: 0x10);
        cartridge.WriteRam(address: 0xA000, value: 0x01);
    }

    private static byte ReadShootRegister(PocketCameraCartridge cartridge) {
        cartridge.WriteControl(address: CameraBlockSelect, value: 0x10);

        return cartridge.ReadRam(address: 0xA000);
    }

    // Reads the deposited image back through the RAM window (camera block deselected, RAM enabled, bank 0).
    private static byte[] ReadImage(PocketCameraCartridge cartridge) {
        cartridge.WriteControl(address: RamEnable, value: 0x0A);
        cartridge.WriteControl(address: CameraBlockSelect, value: 0x00);

        var image = new byte[SensorImage.TiledByteCount];

        for (var index = 0; (index < image.Length); ++index) {
            image[index] = cartridge.ReadRam(address: (ushort)(MemoryMap.ExternalRamStart + SensorImage.RamOffset + index));
        }

        return image;
    }

    private static byte[] UniformDither(byte threshold0, byte threshold1, byte threshold2) {
        var dither = new byte[DitherByteCount];

        for (var cell = 0; (cell < 16); ++cell) {
            dither[(cell * 3)] = threshold0;
            dither[(cell * 3) + 1] = threshold1;
            dither[(cell * 3) + 2] = threshold2;
        }

        return dither;
    }

    // A fixed sensor whose every photosite reads the same value — the flat field the arithmetic checks build on.
    private sealed class FlatSensor : ICameraSensor {
        private readonly byte m_value;

        public FlatSensor(byte value) {
            m_value = value;
        }

        public void Read(Span<byte> destination) {
            destination[..SensorImage.PixelCount].Fill(value: m_value);
        }
    }
}
