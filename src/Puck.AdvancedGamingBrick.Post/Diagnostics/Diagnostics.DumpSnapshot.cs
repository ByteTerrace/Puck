using Puck.Snapshots;

namespace Puck.AdvancedGamingBrick.Post;

// --dump-snapshot [--frames N] [--rom <path>] [--out <file>]: write a snapshot image + section-table sidecar for
// offline cross-build diffing.
internal static partial class Diagnostics {
    // The frame budget a snapshot dump runs when --frames is absent.
    private const int DefaultDumpSnapshotFrames = 300;

    // Parses --dump-snapshot's knobs, boots the machine, runs the requested frames, and writes the snapshot image plus
    // its section-table sidecar. Returns 2 when --rom names a missing file, otherwise 0.
    private static int DumpSnapshot(string[] args) {
        var romPath = ArgValue(args: args, name: "--rom");
        byte[] rom;
        string romLabel;

        if (string.IsNullOrEmpty(value: romPath)) {
            rom = SyntheticRom.Create();
            romLabel = "synthetic";
        } else if (File.Exists(path: romPath)) {
            rom = File.ReadAllBytes(path: romPath);
            romLabel = Path.GetFileName(path: romPath);
        } else {
            Console.WriteLine(value: $"  [SKIP] --dump-snapshot: rom not found at {romPath}");

            return 2;
        }

        var framesArg = ArgValue(args: args, name: "--frames");
        var frames = (((framesArg is not null) && int.TryParse(s: framesArg, result: out var parsedFrames)) ? parsedFrames : DefaultDumpSnapshotFrames);
        var imagePath = (ArgValue(args: args, name: "--out") ?? Path.Combine("artifacts", "gba-post", "snapshot.bin"));
        var imageDirectory = Path.GetDirectoryName(path: Path.GetFullPath(path: imagePath));

        if (!string.IsNullOrEmpty(value: imageDirectory)) {
            Directory.CreateDirectory(path: imageDirectory);
        }

        using var machine = PostMachine.Build(bios: BiosImage, rom: rom);

        machine.RunFrames(frames: frames);

        var snapshot = machine.Machine.Snapshot();

        File.WriteAllBytes(path: imagePath, bytes: snapshot.Data.ToArray());

        var sectionsPath = $"{imagePath}.sections.txt";

        WriteSectionTable(path: sectionsPath, sections: snapshot.Sections);

        // The same repo fingerprint HashDivergenceProbe hashes a snapshot with, so a --dump-snapshot fingerprint and a
        // --hash-divergence report describe the same instant the same way.
        var fingerprint = StateFingerprint.Compute(data: snapshot.Data);

        Console.WriteLine(value: $"  dump-snapshot {romLabel} ({frames} frames) -> {imagePath} ({snapshot.Size:N0} bytes) [fingerprint 0x{fingerprint:X16}], sections -> {sectionsPath}");

        return 0;
    }
    // One line per section: name, offset, length — enough to localize an offline byte-shift between two snapshot
    // images to the component that owns it (a cross-build diff has no running machine to walk).
    private static void WriteSectionTable(string path, IReadOnlyList<SnapshotSection> sections) {
        using var writer = new StreamWriter(path: path);

        foreach (var section in sections) {
            writer.WriteLine(value: $"{section.Name}\t{section.Offset}\t{section.Length}");
        }
    }
}
