using Puck.Maths;

namespace Puck.GamingBricks.Post;

/// <summary>The shared body of a battery's <c>--dump-snapshot</c> diagnostic: parse the knobs
/// (<c>--rom</c>/<c>--frames</c>/<c>--out</c>, with a synthetic-ROM fallback), run the family's capture, and write the
/// snapshot image plus its section-table sidecar for offline cross-build diffing.</summary>
public static class SnapshotDumpDiagnostic {
    /// <summary>Runs the dump: resolves the ROM (synthetic when <c>--rom</c> is absent), the frame budget, and the
    /// output path, invokes <paramref name="capture"/>, and writes the image, the section sidecar, and the fingerprint
    /// line.</summary>
    /// <typeparam name="TSnapshot">The family's snapshot type.</typeparam>
    /// <typeparam name="TIdentity">The family's machine identity type.</typeparam>
    /// <typeparam name="TClock">The family's captured clock type.</typeparam>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="defaultFrames">The frame budget when <c>--frames</c> is absent.</param>
    /// <param name="defaultArtifactsSubpath">The <c>artifacts</c> subdirectory the image lands in when <c>--out</c> is
    /// absent (e.g. <c>"gb-post"</c>).</param>
    /// <param name="syntheticRom">Creates the family's synthetic ROM, used when <c>--rom</c> is absent.</param>
    /// <param name="capture">Builds the machine from the ROM bytes (second argument: whether the ROM is the synthetic
    /// fallback), runs the frame budget, and returns the snapshot plus the parenthesized run description for the
    /// report line (e.g. <c>"Dmg, 300 frames"</c>).</param>
    /// <returns><c>2</c> when <c>--rom</c> names a missing file, otherwise <c>0</c>.</returns>
    public static int Run<TSnapshot, TIdentity, TClock>(
        string[] args,
        int defaultFrames,
        string defaultArtifactsSubpath,
        Func<byte[]> syntheticRom,
        Func<byte[], bool, int, (TSnapshot Snapshot, string RunDescription)> capture
    )
        where TSnapshot : MachineSnapshot<TSnapshot, TIdentity, TClock>
        where TIdentity : IEquatable<TIdentity>
        where TClock : IEquatable<TClock> {
        var romPath = CommandLineArguments.Value(
            args: args,
            name: "--rom"
        );
        byte[] rom;
        string romLabel;
        bool isSynthetic;

        if (string.IsNullOrEmpty(value: romPath)) {
            rom = syntheticRom();
            romLabel = "synthetic";
            isSynthetic = true;
        } else if (File.Exists(path: romPath)) {
            rom = File.ReadAllBytes(path: romPath);
            romLabel = Path.GetFileName(path: romPath);
            isSynthetic = false;
        } else {
            Console.WriteLine(value: $"  [SKIP] --dump-snapshot: rom not found at {romPath}");

            return 2;
        }

        var framesArg = CommandLineArguments.Value(
            args: args,
            name: "--frames"
        );
        var frames = (((framesArg is not null) && int.TryParse(
            result: out var parsedFrames,
            s: framesArg
        ))
            ? parsedFrames
            : defaultFrames);
        var imagePath = (CommandLineArguments.Value(
            args: args,
            name: "--out"
        ) ?? Path.Combine(
            path1: "artifacts",
            path2: defaultArtifactsSubpath,
            path3: "snapshot.bin"
        ));
        var imageDirectory = Path.GetDirectoryName(path: Path.GetFullPath(path: imagePath));

        if (!string.IsNullOrEmpty(value: imageDirectory)) {
            Directory.CreateDirectory(path: imageDirectory);
        }

        var (snapshot, runDescription) = capture(
            arg1: rom,
            arg2: isSynthetic,
            arg3: frames
        );

        File.WriteAllBytes(
            path: imagePath,
            bytes: snapshot.Data.ToArray()
        );

        var sectionsPath = $"{imagePath}.sections.txt";

        WriteSectionTable(
            path: sectionsPath,
            sections: snapshot.Sections
        );

        // The same repo fingerprint HashDivergenceReport hashes a snapshot with, so a --dump-snapshot fingerprint and a
        // --hash-divergence report describe the same instant the same way.
        var fingerprint = Fnv1aHash.Compute(values: snapshot.Data);

        Console.WriteLine(value: $"  dump-snapshot {romLabel} ({runDescription}) -> {imagePath} ({snapshot.Size:N0} bytes) [fingerprint 0x{fingerprint:X16}], sections -> {sectionsPath}");

        return 0;
    }
    /// <summary>Writes one line per section — name, offset, length — enough to localize an offline byte-shift between
    /// two snapshot images to the component that owns it (a cross-build diff has no running machine to walk).</summary>
    /// <param name="path">The sidecar file path.</param>
    /// <param name="sections">The snapshot's section table.</param>
    public static void WriteSectionTable(string path, IReadOnlyList<SnapshotSection> sections) {
        using var writer = new StreamWriter(path: path);

        foreach (var section in sections) {
            writer.WriteLine(value: $"{section.Name}\t{section.Offset}\t{section.Length}");
        }
    }
}
