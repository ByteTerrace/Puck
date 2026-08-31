namespace Puck.AdvancedGamingBrick.Post;

// --dump-snapshot [--frames N] [--rom <path>] [--out <file>]: write a snapshot image + section-table sidecar for
// offline cross-build diffing.
internal static partial class Diagnostics {
    // The frame budget a snapshot dump runs when --frames is absent.
    private const int DefaultDumpSnapshotFrames = 300;

    // Parses --dump-snapshot's knobs, boots the machine, runs the requested frames, and writes the snapshot image plus
    // its section-table sidecar. Returns 2 when --rom names a missing file, otherwise 0.
    private static int DumpSnapshot(string[] args) =>
        SnapshotDumpDiagnostic.Run<AgbMachineSnapshot, AgbMachineIdentity, long>(
            args: args,
            capture: static (rom, _, frames) => {
                using var machine = PostMachine.Build(
                    bios: BiosImage,
                    rom: rom
                );

                machine.RunFrames(frames: frames);

                return (machine.Machine.Snapshot(), $"{frames} frames");
            },
            defaultArtifactsSubpath: "gba-post",
            defaultFrames: DefaultDumpSnapshotFrames,
            syntheticRom: SyntheticRom.Create
        );
}
