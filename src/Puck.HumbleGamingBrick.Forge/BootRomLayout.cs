namespace Puck.HumbleGamingBrick.Forge;

/// <summary>The cartridge header a calibration probe presents, and the vertical line the seeded handoff puts the
/// picture processor on for that header class.</summary>
/// <param name="Title">The cartridge title, padded into <c>0x0134</c>-<c>0x0142</c>.</param>
/// <param name="ColorFlag">The color flag at <c>0x0143</c>.</param>
/// <param name="OldLicenseeCode">The legacy licensee code at <c>0x014B</c>.</param>
/// <param name="NewLicenseeCode">The two-character new licensee code at <c>0x0144</c>.</param>
/// <param name="HandoffLine">The <c>LY</c> value the seeded handoff carries for this header class; zero means the
/// handoff sits on the first line, which the image reaches by re-enabling the LCD in its epilogue rather than by
/// timing the enable.</param>
public readonly record struct BootRomProbe(string Title, byte ColorFlag, byte OldLicenseeCode, string NewLicenseeCode, byte HandoffLine) {
    /// <summary>Gets a value indicating whether the probe cartridge advertises Color enhancements.</summary>
    public bool SupportsColor =>
        ((ColorFlag == 0x80) || (ColorFlag == 0xC0));
}
/// <summary>The straight-line machine-cycle counts the emitted program subtracts from its computed delays, solved by
/// booting the image. <see cref="DividerTail"/> covers everything between the divider reset and the unmap;
/// <see cref="EnableToHandoffColor"/> and <see cref="EnableToHandoffMonochrome"/> cover everything between the LCD
/// enable and the unmap, one per header class because the two classes hand off on different lines.</summary>
/// <param name="DividerTail">Machine cycles subtracted from the divider delay.</param>
/// <param name="EnableToHandoffColor">Machine cycles subtracted from the enable delay for a color cartridge.</param>
/// <param name="EnableToHandoffMonochrome">Machine cycles subtracted from the enable delay for a cartridge without the
/// color flag.</param>
public readonly record struct BootRomCalibration(int DividerTail, int EnableToHandoffColor, int EnableToHandoffMonochrome) {
    /// <summary>Gets the starting point a solve begins from, before any image has been booted.</summary>
    public static BootRomCalibration Zero =>
        default;

    /// <summary>Returns the calibration with the divider tail moved by a machine-cycle delta.</summary>
    /// <param name="machineCycles">The delta to add.</param>
    /// <returns>The adjusted calibration.</returns>
    public BootRomCalibration WithDividerAdjustment(int machineCycles) =>
        (this with { DividerTail = (DividerTail + machineCycles) });
    /// <summary>Returns the calibration with one header class's enable distance moved by a machine-cycle delta.</summary>
    /// <param name="machineCycles">The delta to add.</param>
    /// <param name="colorCartridge">Whether the delta applies to the color-cartridge class.</param>
    /// <returns>The adjusted calibration.</returns>
    public BootRomCalibration WithEnableAdjustment(int machineCycles, bool colorCartridge) =>
        (colorCartridge
        ? (this with { EnableToHandoffColor = (EnableToHandoffColor + machineCycles) })
        : (this with { EnableToHandoffMonochrome = (EnableToHandoffMonochrome + machineCycles) }));
}
/// <summary>
/// Everything about a revision's boot program that the emitter reads: the register file it hands the cartridge, which
/// hardware checks its boot ROM performs, whether its handoff counter is a constant or a function of the header, and
/// the probe cartridges the builder boots to solve its timing.
/// </summary>
public sealed class BootRomLayout {
    private BootRomLayout(ConsoleModel model, BootRomProbe[] probes) {
        Model = model;
        Probes = probes;
    }

    /// <summary>Gets the revision the program is emitted for.</summary>
    public ConsoleModel Model { get; }
    /// <summary>Gets the probe cartridges the builder boots to solve the program's timing. Every header class whose
    /// timing or handoff line differs needs one, and a second probe in the same class proves the solved constant is
    /// header-independent.</summary>
    public BootRomProbe[] Probes { get; }

    /// <summary>Gets a value indicating whether the revision has Color hardware, which selects the 2304-byte image
    /// shape.</summary>
    public bool SupportsColor =>
        Model.SupportsColor();
    /// <summary>Gets a value indicating whether the boot program verifies the cartridge logo and header checksum. The
    /// companion console's boot ROM forwards the header instead of checking it.</summary>
    public bool VerifiesHeader =>
        !Model.IsSuperGameBoy();
    /// <summary>Gets a value indicating whether the handoff counter is a function of the cartridge header rather than a
    /// per-revision constant, so the program must carry the prediction tables and compute its own target.</summary>
    public bool TimesFromHeader =>
        (Model.IsSuperGameBoy() || Model.SupportsColor());
    /// <summary>Gets a value indicating whether the program times its LCD enable so the handoff lands mid vertical
    /// blank, rather than re-enabling in the epilogue to hand off on the first line.</summary>
    public bool TimesLcdEnable =>
        (Probes[0].HandoffLine != 0);
    /// <summary>Gets the T-cycles this revision's boot ROM runs beyond what the shared Color tables give for the same
    /// header, which the emitted program adds to its own table walk rather than leaving to the solved tail.</summary>
    public ushort HandoffCounterExtra =>
        ((Model == ConsoleModel.Cgb0)
        ? BootDivPrediction.Cgb0Extra
        : (Model.HasAgbBootHandoff()
            ? BootDivPrediction.AgbExtra
            : (ushort)0));
    /// <summary>Gets the handoff counter every cartridge produces on this revision, for a revision whose boot time is a
    /// constant.</summary>
    public ushort ConstantCounter =>
        ((Model == ConsoleModel.Dmg0)
        ? BootDivPrediction.Dmg0Counter
        : BootDivPrediction.DmgCounter);

    /// <summary>Creates the layout for a revision.</summary>
    /// <param name="model">The revision.</param>
    /// <returns>The layout.</returns>
    public static BootRomLayout For(ConsoleModel model) =>
        new(
        model: model,
        probes: ProbesFor(model: model)
    );

    private static BootRomProbe[] ProbesFor(ConsoleModel model) {
        if (model.SupportsColor()) {
            // The Color handoff sits four lines further into vertical blank for a cartridge without the color flag,
            // because the compatibility path runs longer; each class needs its own solved enable distance.
            return [
                new BootRomProbe(
                    ColorFlag: 0x80,
                    HandoffLine: 0x90,
                    NewLicenseeCode: "01",
                    OldLicenseeCode: 0x33,
                    Title: "PUCK COLOR"
                ),
                new BootRomProbe(
                    ColorFlag: 0x00,
                    HandoffLine: 0x94,
                    NewLicenseeCode: "  ",
                    OldLicenseeCode: 0x01,
                    Title: "PUCK MONO"
                ),
            ];
        }

        if (model.IsSuperGameBoy()) {
            // Two titles with different forwarded bit counts, so a solved constant that secretly depended on the header
            // fails to converge instead of passing on one probe.
            return [
                new BootRomProbe(
                    ColorFlag: 0x00,
                    HandoffLine: 0x00,
                    NewLicenseeCode: "  ",
                    OldLicenseeCode: 0x01,
                    Title: "PUCK"
                ),
                new BootRomProbe(
                    ColorFlag: 0x00,
                    HandoffLine: 0x00,
                    NewLicenseeCode: "77",
                    OldLicenseeCode: 0x33,
                    Title: "PUCK SUPER TEST"
                ),
            ];
        }

        return [
            new BootRomProbe(
                ColorFlag: 0x00,
                HandoffLine: ((model == ConsoleModel.Dmg0)
                    ? ((byte)0x91)
                    : ((byte)0x00)),
                NewLicenseeCode: "  ",
                OldLicenseeCode: 0x01,
                Title: "PUCK"
            ),
        ];
    }
}
