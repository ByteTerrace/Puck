using System.Text;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>
/// Runs Shay Green's (blargg's) MMC3 scanline-counter and CPU interrupt test ROMs from the nes-test-roms archive and
/// reads each one's verdict the way the suites publish it: once $6001-$6003 hold $DE $B0 $61, $6000 is $80 while the
/// test runs and a result code from $00 (passed) to $7F when it finishes, with the explanation as text from $6004.
/// <para>
/// The MMC3 ROMs are the second edition (<c>mmc3_test_2</c>). Its <c>6-MMC3_alt</c> checks the MMC3A, which a plain
/// mapper 4 header does not select, so it is not run; <c>5-MMC3</c> checks the MMC3B the header selects, the revision
/// in Super Mario Bros. 3. The report carries one case per ROM, and the stage fails while any of them fails or never
/// finishes.
/// </para>
/// https://github.com/christopherpow/nes-test-roms
/// </summary>
internal sealed class BlarggSuiteStage : IPostStage<PostContext> {
    private const int FrameLimit = 1_200;
    private const ulong FrameTicks = 357_366UL;

    private static readonly string[] Roms = [
        "mmc3_test_2/rom_singles/1-clocking.nes",
        "mmc3_test_2/rom_singles/2-details.nes",
        "mmc3_test_2/rom_singles/3-A12_clocking.nes",
        "mmc3_test_2/rom_singles/4-scanline_timing.nes",
        "mmc3_test_2/rom_singles/5-MMC3.nes",
        "cpu_interrupts_v2/rom_singles/1-cli_latency.nes",
        "cpu_interrupts_v2/rom_singles/2-nmi_and_brk.nes",
        "cpu_interrupts_v2/rom_singles/3-nmi_and_irq.nes",
        "cpu_interrupts_v2/rom_singles/4-irq_and_dma.nes",
        "cpu_interrupts_v2/rom_singles/5-branch_delays_irq.nes",
        "ppu_vbl_nmi/rom_singles/01-vbl_basics.nes",
        "ppu_vbl_nmi/rom_singles/02-vbl_set_time.nes",
        "ppu_vbl_nmi/rom_singles/03-vbl_clear_time.nes",
        "ppu_vbl_nmi/rom_singles/04-nmi_control.nes",
        "ppu_vbl_nmi/rom_singles/05-nmi_timing.nes",
        "ppu_vbl_nmi/rom_singles/06-suppression.nes",
        "ppu_vbl_nmi/rom_singles/07-nmi_on_timing.nes",
        "ppu_vbl_nmi/rom_singles/08-nmi_off_timing.nes",
        "ppu_vbl_nmi/rom_singles/09-even_odd_frames.nes",
        "ppu_vbl_nmi/rom_singles/10-even_odd_timing.nes",
        "apu_test/rom_singles/1-len_ctr.nes",
        "apu_test/rom_singles/2-len_table.nes",
        "apu_test/rom_singles/3-irq_flag.nes",
        "apu_test/rom_singles/4-jitter.nes",
        "apu_test/rom_singles/5-len_timing.nes",
        "apu_test/rom_singles/6-irq_flag_timing.nes",
        "apu_test/rom_singles/7-dmc_basics.nes",
        "apu_test/rom_singles/8-dmc_rates.nes",
    ];

    /// <inheritdoc/>
    public string Name => "blargg-suites";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (context.TestRomRoot is null) {
            return PostStageOutcome.Skip(detail: "no nes-test-roms corpus; use --fetch-corpora or --roms");
        }

        var cases = new List<PostCaseResult>(capacity: Roms.Length);
        var failures = new StringBuilder();
        var passed = 0;

        foreach (var rom in Roms) {
            var path = Path.Combine(path1: context.TestRomRoot, path2: rom);

            if (!File.Exists(path: path)) {
                return PostStageOutcome.Infra(detail: $"incomplete nes-test-roms corpus: requires {rom}");
            }

            var (code, text) = RunRom(image: File.ReadAllBytes(path: path));
            var verdict = ((code == 0) ? PostCaseVerdict.Pass : PostCaseVerdict.Mismatch);
            var detail = ((code < 0) ? $"never finished; {text}" : $"result {code}: {text}");

            if (verdict == PostCaseVerdict.Pass) {
                ++passed;
            } else {
                _ = failures.Append(value: $"{rom}: {detail}; ");
            }
            cases.Add(item: new(Detail: detail, Duration: TimeSpan.Zero, Name: rom, Verdict: verdict));
        }

        var summary = $"{passed} of {Roms.Length} ROMs pass";

        return ((failures.Length == 0)
            ? PostStageOutcome.Pass(cases: cases, detail: summary)
            : PostStageOutcome.Fail(cases: cases, detail: $"{summary}; failing: {failures}"));
    }

    // Returns the result code, or -1 when the ROM never finished, with the text it wrote from $6004.
    private static (int Code, string Text) RunRom(byte[] image) {
        using var instance = HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(
            cartridge: HgdCartridge.Load(image: image)));
        var machine = instance.Machine;

        for (var frame = 0; (frame < FrameLimit); ++frame) {
            machine.RunCycles(masterTicks: FrameTicks);

            var bus = machine.Bus;

            if ((bus.Peek(address: 0x6001) != 0xDE) || (bus.Peek(address: 0x6002) != 0xB0) || (bus.Peek(address: 0x6003) != 0x61)) {
                continue;
            }

            var status = bus.Peek(address: 0x6000);

            if (status < 0x80) {
                return (status, Text(machine: machine));
            }
        }

        return (-1, Text(machine: machine));
    }
    private static string Text(HgdMachine machine) {
        var text = new StringBuilder();

        for (var address = 0x6004; (address < 0x7000); ++address) {
            var value = machine.Bus.Peek(address: ((ushort)address));

            if (value == 0) {
                break;
            }

            _ = text.Append(value: ((value is >= 0x20 and < 0x7F) ? ((char)value) : ' '));
        }

        return text.ToString().Trim();
    }
}
