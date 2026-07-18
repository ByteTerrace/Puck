using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The camera cartridge (MAC-GBD) mapper with a faithful <b>M64282FP</b> image sensor. Banking is MBC3-like — a
/// six-bit ROM bank (a written zero reading as one) and a four-bit RAM bank — plus the camera's register block, which
/// bit&#160;4 of the RAM-bank register maps over the whole <c>0xA000</c>–<c>0xBFFF</c> window (no RAM enable required,
/// mirroring every <c>0x80</c> bytes). Register&#160;0's bit&#160;0 arms a capture and reads back as the busy flag; the
/// other camera registers are write-only and read as <c>0x00</c>.
/// <para>
/// A capture <see cref="ICameraSensor.Read">latches</see> the sensor plane, runs the M64282FP processing — per-pixel
/// gain (register&#160;1), exposure (registers&#160;2–3, divided by <c>0x1000</c>), optional edge enhancement
/// (registers&#160;1 and&#160;4), and the 4×4 dithering/threshold matrix (registers&#160;6–<c>0x35</c>) — and deposits the
/// result as native 2bpp tiles at <c>0xA100</c> in bank&#160;0. Everything is <b>integer</b>: the analog gain curve is a
/// fixed-point (16.16) table and the edge ratios are exact quarters, so the only non-deterministic input is the sensor
/// readout itself, latched at the trigger instant. The busy bit clears after the real exposure-dependent delay, driven
/// off the emulated LCD clock like the MBC3's RTC — so a ROM's poll-until-ready loop terminates deterministically.
/// The register file, the banking registers, and the busy countdown are all snapshotted; the deposited image lives in
/// save RAM and is snapshotted with it.
/// </para>
/// </summary>
public sealed class CameraCartridge : CartridgeBase, IClockedComponent {
    private const int CameraRegisterCount = 0x36;
    private const int RamBankSize = 0x2000;
    private const int RomBankSize = 0x4000;

    // Camera register indices (M64282FP working registers, as the MAC-GBD exposes them at 0xA000+).
    private const int ShootRegister = 0x00;         // bit 0: capture trigger / busy; bits 1-2: 1-D / edge mode
    private const int GainAndEdgeRegister = 0x01;   // bits 0-4: analog gain index; bits 5-7 == 0xE0: edge enhancement on
    private const int ExposureHighRegister = 0x02;  // exposure time, high byte
    private const int ExposureLowRegister = 0x03;   // exposure time, low byte
    private const int EdgeRatioRegister = 0x04;     // bits 4-6: edge-enhancement exclusive-ratio index
    private const int DitherPatternStart = 0x06;    // registers 6..0x35: 16 cells × 3 thresholds = the dither matrix

    // Busy timing (in LCD dots ≈ CPU T-cycles at normal speed), matching real-hardware countdown timing: a fixed base, a penalty
    // when 1-D processing is off (register 1 bit 7 clear), and 64 dots per unit of the 16-bit exposure register. The
    // real cart's sub-DIV alignment jitter is a host-clock artifact and is deliberately not modeled (it would import
    // non-determinism for no observable ROM benefit).
    private const int BusyBaseDots = 129792;
    private const int BusyDotsPerExposureUnit = 64;
    private const int BusyOneDimensionalPenaltyDots = 2048;

    // The analog gain curve as 16.16 fixed-point (gain × 65536), so `(pixel * gain) >> 16` is exact integer arithmetic.
    // These are the M64282FP's 32 gain steps (from a hardware-accurate reference table), rounded once at authoring time — index 4 is exactly 1.0.
    private static readonly int[] GainTable = [
        57733, 59960, 61981, 63830,
        65536, 67118, 68593, 69976,
        71276, 73664, 75818, 77778,
        79577, 81240, 83518, 86228,
        88639, 90810, 92785, 94597,
        96270, 97824, 99275, 100635,
        101916, 103126, 104273, 105362,
        106400, 107391, 108339, 109247,
    ];
    // The eight edge-enhancement exclusive ratios {0.5, 0.75, 1, 1.25, 2, 3, 4, 5} in quarter units, so the enhancement
    // term is `ratioQuarters × (4·color − neighbours) / 4` — the ratios are exact quarters, so nothing is lost.
    private static readonly int[] EdgeRatioQuarters = [2, 3, 4, 5, 8, 12, 16, 20];
    private readonly byte[] m_cameraRegisters;
    private readonly byte[] m_plane;         // scratch: the latched sensor readout (rebuilt every capture)
    private readonly byte[] m_tiles;         // scratch: the packed 2bpp image (rebuilt every capture)
    private readonly int m_ramBankWrapMask;
    private int m_busyDots;
    private bool m_cameraSelected;
    private ICameraSensor m_sensor;
    private int m_ramBank;
    private bool m_ramEnabled;
    private int m_romBank;

    /// <summary>Creates a camera cartridge with its registers at reset (ROM bank 1, RAM disabled, RAM window
    /// selected), a zeroed camera register file, and the deterministic default sensor.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public CameraCartridge(byte[] rom, CartridgeHeader header)
        : base(rom: rom, header: header) {
        m_cameraRegisters = new byte[CameraRegisterCount];
        m_plane = new byte[SensorImage.PixelCount];
        m_ramBankWrapMask = ComputeBankWrapMask(byteCount: header.RamByteCount, bankSize: RamBankSize);
        m_romBank = 1;
        m_sensor = new GradientCameraSensor();
        m_tiles = new byte[SensorImage.TiledByteCount];
    }

    /// <summary>Gets or sets the sensor whose readout a capture latches — the seam a host swaps for a live camera. Never
    /// null; the constructor installs the deterministic default. This is host input, not snapshot state, so it is not
    /// serialized (like the joypad's button source, it is owned by whoever drives the machine).</summary>
    /// <exception cref="ArgumentNullException">The assigned value is <see langword="null"/>.</exception>
    public ICameraSensor Sensor {
        get => m_sensor;
        set => m_sensor = (value ?? throw new ArgumentNullException(paramName: nameof(value)));
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Lcd;

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        (Header.HasRam && m_ramEnabled);

    /// <inheritdoc/>
    public void Tick() {
        // The exposure countdown runs on the fixed LCD clock (like the MBC3's RTC), so the busy window is the same
        // number of emulated dots on every run: a ROM polling register 0's busy bit always sees it clear at the same
        // deterministic point.
        if (m_busyDots <= 0) {
            return;
        }

        if (--m_busyDots == 0) {
            m_cameraRegisters[ShootRegister] &= 0xFE;
        }
    }

    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        switch (address >> 13) {
            case 0: // 0x0000-0x1FFF: RAM enable (the camera block ignores it)
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                break;
            case 1: // 0x2000-0x3FFF: six-bit ROM bank, zero reads as one
                m_romBank = value & 0x3F;

                if (m_romBank == 0) {
                    m_romBank = 1;
                }

                break;
            case 2: // 0x4000-0x5FFF: bit 4 maps the camera block over the window; bits 3-0 select the RAM bank
                m_cameraSelected = ((value & 0x10) != 0);
                m_ramBank = value & 0x0F;

                break;
            default: // 0x6000-0x7FFF: no register
                break;
        }
    }
    /// <summary>Reads from the external window: register&#160;0's busy flag while the camera block is selected and that
    /// register is addressed, <c>0x00</c> for the other (write-only) camera registers, otherwise banked RAM — which is
    /// where a completed capture's image lives (bank&#160;0, from <c>0xA100</c>).</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns>The busy flag, <c>0x00</c>, or the RAM byte.</returns>
    public override byte ReadRam(ushort address) {
        if (!m_cameraSelected) {
            return base.ReadRam(address: address);
        }

        var register = (address - MemoryMap.ExternalRamStart) & 0x7F;

        return ((register == ShootRegister) ? m_cameraRegisters[ShootRegister] : (byte)0x00);
    }
    /// <summary>Writes to the external window: a camera register while the camera block is selected (register&#160;0's
    /// bit&#160;0 arms a capture on its rising edge and cannot be cleared while a shoot is in progress), otherwise banked
    /// RAM.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <param name="value">The value to store.</param>
    public override void WriteRam(ushort address, byte value) {
        if (!m_cameraSelected) {
            base.WriteRam(address: address, value: value);

            return;
        }

        var register = (address - MemoryMap.ExternalRamStart) & 0x7F;

        if (register >= CameraRegisterCount) {
            return;
        }

        if (register != ShootRegister) {
            m_cameraRegisters[register] = value;

            return;
        }

        // Register 0 keeps only bits 0-2. A 0->1 edge on bit 0 fires the shoot (latch + process + start the busy
        // countdown); a real cart cannot cancel an in-progress shoot, so a bit-0 clear while busy is ignored.
        value &= 0x07;

        if (((value & 0x01) != 0) && ((m_cameraRegisters[ShootRegister] & 0x01) == 0)) {
            m_cameraRegisters[ShootRegister] = value;
            Capture();

            return;
        }

        if (((value & 0x01) == 0) && ((m_cameraRegisters[ShootRegister] & 0x01) != 0)) {
            value |= 0x01;
        }

        m_cameraRegisters[ShootRegister] = value;
    }
    /// <inheritdoc/>
    /// <remarks>Overridden: bit&#160;4 of the RAM-bank register can map the camera register block over the whole
    /// window, and register&#160;0 triggers a capture side effect — the window stays on the interface path.</remarks>
    public override bool TryComputeRamWindow(out int offset, out int length) {
        offset = 0;
        length = 0;

        return false;
    }

    /// <inheritdoc/>
    protected override int MapRomOffset(ushort address) =>
        ((address <= MemoryMap.RomBank0End)
            ? address
            : ((m_romBank * RomBankSize) + (address - MemoryMap.RomBankNStart)));
    /// <inheritdoc/>
    protected override int MapRamOffset(ushort address) =>
        (((m_ramBank & m_ramBankWrapMask) * RamBankSize) + (address - MemoryMap.ExternalRamStart));
    /// <inheritdoc/>
    protected override void SaveRegisters(StateWriter writer) {
        writer.WriteBytes(value: m_cameraRegisters);
        writer.WriteInt32(value: m_busyDots);
        writer.WriteBoolean(value: m_cameraSelected);
        writer.WriteInt32(value: m_ramBank);
        writer.WriteBoolean(value: m_ramEnabled);
        writer.WriteInt32(value: m_romBank);
    }
    /// <inheritdoc/>
    protected override void LoadRegisters(StateReader reader) {
        reader.ReadBytes(destination: m_cameraRegisters);
        m_busyDots = reader.ReadInt32();
        m_cameraSelected = reader.ReadBoolean();
        m_ramBank = reader.ReadInt32();
        m_ramEnabled = reader.ReadBoolean();
        m_romBank = reader.ReadInt32();
    }

    // Latches the sensor, processes the M64282FP image into 2bpp tiles, deposits them at 0xA100 in bank 0, and arms the
    // exposure-dependent busy countdown. Called synchronously on the capture trigger so the sensor readout is sampled at
    // one well-defined instant.
    private void Capture() {
        m_sensor.Read(destination: m_plane);

        var edgeEnabled = ((m_cameraRegisters[GainAndEdgeRegister] & 0xE0) == 0xE0);
        var ratioQuarters = EdgeRatioQuarters[(m_cameraRegisters[EdgeRatioRegister] >> 4) & 0x07];

        for (var tileY = 0; (tileY < SensorImage.TilesTall); ++tileY) {
            for (var tileX = 0; (tileX < SensorImage.TilesWide); ++tileX) {
                var tileBase = (((tileY * SensorImage.TilesWide) + tileX) * SensorImage.TileByteCount);

                for (var row = 0; (row < 8); ++row) {
                    var py = ((tileY * 8) + row);
                    var lowPlane = 0;
                    var highPlane = 0;

                    for (var column = 0; (column < 8); ++column) {
                        var px = ((tileX * 8) + column);
                        var color = GetProcessedColor(x: px, y: py);

                        if (edgeEnabled) {
                            var neighbours =
                                (((GetProcessedColor(x: (px - 1), y: py)
                                + GetProcessedColor(x: (px + 1), y: py))
                                + GetProcessedColor(x: px, y: (py - 1)))
                                + GetProcessedColor(x: px, y: (py + 1)));

                            color += ((ratioQuarters * ((4 * color) - neighbours)) / 4);
                        }

                        var patternBase = ((((px & 3) + ((py & 3) * 4)) * 3) + DitherPatternStart);
                        int shade;

                        if (color < m_cameraRegisters[patternBase]) {
                            shade = 3;
                        } else if (color < m_cameraRegisters[(patternBase + 1)]) {
                            shade = 2;
                        } else if (color < m_cameraRegisters[(patternBase + 2)]) {
                            shade = 1;
                        } else {
                            shade = 0;
                        }

                        var bit = (7 - column);

                        lowPlane |= ((shade & 0x01) << bit);
                        highPlane |= (((shade >> 1) & 0x01) << bit);
                    }

                    m_tiles[(tileBase + (row * 2))] = (byte)lowPlane;
                    m_tiles[((tileBase + (row * 2)) + 1)] = (byte)highPlane;
                }
            }
        }

        DepositExternalRam(offset: SensorImage.RamOffset, source: m_tiles);

        var exposure = (m_cameraRegisters[ExposureHighRegister] << 8) | m_cameraRegisters[ExposureLowRegister];

        m_busyDots =
            ((BusyBaseDots
            + (((m_cameraRegisters[GainAndEdgeRegister] & 0x80) != 0) ? 0 : BusyOneDimensionalPenaltyDots))
            + (exposure * BusyDotsPerExposureUnit));
    }

    // The M64282FP per-pixel analog readout: the latched sensor value scaled by the gain step and the exposure register.
    // Coordinates are edge-clamped so the edge-enhancement neighbour taps stay on the plane. Returns a signed value that
    // may fall outside 0..255 (the dither thresholds map it back into the four shades), so the accumulator is `long`.
    private long GetProcessedColor(int x, int y) {
        if (x < 0) {
            x = 0;
        } else if (x >= SensorImage.Width) {
            x = (SensorImage.Width - 1);
        }

        if (y < 0) {
            y = 0;
        } else if (y >= SensorImage.Height) {
            y = (SensorImage.Height - 1);
        }

        long color = m_plane[((y * SensorImage.Width) + x)];

        color = ((color * GainTable[m_cameraRegisters[GainAndEdgeRegister] & 0x1F]) >> 16);

        var exposure = (m_cameraRegisters[ExposureHighRegister] << 8) | m_cameraRegisters[ExposureLowRegister];

        return ((color * exposure) / 0x1000);
    }
}
