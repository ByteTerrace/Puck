using System.Diagnostics;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the four-quad demo's multicast contract, in miniature. Three machines — one per costume (Dmg, Cgb,
/// Agb) — plus a Dmg twin are fed the SAME deterministic per-frame joypad script (the multicast path: one input
/// stream, every machine). After the run: the Dmg pair is byte-identical (input injection is deterministic — the
/// joypad is machine state, so the full-snapshot compare covers it), and the Cgb/Agb framebuffers are bit-identical
/// (carry-forward under input, not just in attract mode). The wall-clock for stepping all four machines is reported
/// as the trio-throughput evidence the demo's frame budget rests on.
/// </summary>
internal sealed class TrioLockstepStage : IPostStage {
    private const int Frames = 300;

    /// <inheritdoc/>
    public string Name =>
        "trio-lockstep";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var rom = SyntheticRom.Create();

        using var dmg = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);
        using var dmgTwin = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);
        using var cgb = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);
        using var agb = PostMachine.Build(model: ConsoleModel.Agb, rom: rom);

        MachineInstance[] machines = [dmg, dmgTwin, cgb, agb];
        var joypads = Array.ConvertAll(array: machines, converter: static machine => machine.GetRequiredService<IJoypad>());
        var stopwatch = Stopwatch.StartNew();

        for (var frame = 0; (frame < Frames); frame++) {
            // A deterministic, edge-rich script: every button bit toggles over the run (multiplying by an odd
            // constant walks all 256 patterns), exercising the joypad lines and their interrupt edges identically
            // on every machine.
            var buttons = (JoypadButtons)(byte)(frame * 37);

            foreach (var joypad in joypads) {
                joypad.SetButtons(pressed: buttons);
            }

            foreach (var machine in machines) {
                machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
            }
        }

        stopwatch.Stop();

        if (!dmg.Machine.Snapshot().ContentEquals(other: dmgTwin.Machine.Snapshot())) {
            return PostStageOutcome.Fail(detail: $"two Dmg machines fed the same joypad script diverged after {Frames} frames");
        }

        var cgbPixels = cgb.GetRequiredService<IFramebuffer>().Pixels;
        var agbPixels = agb.GetRequiredService<IFramebuffer>().Pixels;

        if (!cgbPixels.SequenceEqual(other: agbPixels)) {
            return PostStageOutcome.Fail(detail: $"Cgb and Agb framebuffers diverged after {Frames} frames of the same joypad script");
        }

        // 4 machines * Frames emulated frames in the measured wall time = the frames/second one thread sustains.
        var framesPerSecond = ((4.0 * Frames) / stopwatch.Elapsed.TotalSeconds);

        return PostStageOutcome.Pass(
            detail: $"Dmg pair byte-identical and Cgb≡Agb at the pixels under a shared {Frames}-frame joypad script; {framesPerSecond:F0} machine-frames/s single-threaded ({(framesPerSecond / PostMachine.HardwareFps):F1} machines at realtime)"
        );
    }
}
