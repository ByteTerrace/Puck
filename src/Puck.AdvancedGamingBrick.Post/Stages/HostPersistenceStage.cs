using System.Text;
using Puck.GamingBricks;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Checks explicit BIOS configuration and battery-save replacement, restore, and retry through the host core.</summary>
internal sealed class HostPersistenceStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "host-persistence";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var directory = Path.Combine(path1: context.ArtifactsDirectory, path2: $"host-persistence-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(path: directory);
        try {
            CheckBios(directory: directory);
            CheckPersistence(directory: directory);
            return PostStageOutcome.Pass(detail: "bundled cold/fast defaults and explicit stub accepted, zero/wrong-sized BIOS rejected; new save, clean restore, forced flush and disposal persisted; replacement failure preserved old save and retried on Windows");
        } catch (InvalidOperationException exception) {
            return PostStageOutcome.Fail(detail: exception.Message);
        }
    }

    private static void CheckBios(string directory) {
        var engine = new AdvancedGamingBrickEngine();
        foreach (var options in new[] { "direct", "unknown", "fast cold", "bios=" }) {
            RequireRejected(engine: engine, options: options);
        }
        using var bundled = engine.Create(options: null);
        using var fast = engine.Create(options: "fast");
        using var stub = engine.Create(options: "stub", contentBytes: SyntheticRom.Create());
        var biosPath = Path.Combine(path1: directory, path2: "bios.bin");
        var bios = new byte[ReplacementBios.ImageSize];
        File.WriteAllBytes(path: biosPath, bytes: bios);
        RequireRejected(engine: engine, options: $"bios={biosPath}");
        File.WriteAllBytes(path: biosPath, bytes: new byte[1]);
        RequireRejected(engine: engine, options: $"bios={biosPath}");
        // A caller-supplied replacement need not match a retail hash; validation cannot prove arbitrary BIOS code.
        bios[0] = 1;
        File.WriteAllBytes(path: biosPath, bytes: bios);
        using var supplied = engine.Create(options: $"bios={biosPath}", contentBytes: SyntheticRom.Create());
    }

    private static void RequireRejected(AdvancedGamingBrickEngine engine, string? options) {
        try {
            using var machine = engine.Create(options: options);
        } catch (ArgumentException) {
            return;
        }
        throw new InvalidOperationException(message: $"BIOS option '{options}' was accepted unexpectedly");
    }

    private static void CheckPersistence(string directory) {
        var bios = new byte[ReplacementBios.ImageSize];
        var rom = SyntheticRom.Create();
        Encoding.ASCII.GetBytes(s: "SRAM_V").CopyTo(array: rom, index: rom.Length - 16);
        var path = Path.Combine(path1: directory, path2: "battery.sav");
        byte[] clean = [];
        using (var seed = new AdvancedGamingBrickCore(bios: bios, cartridgeRom: rom, savePath: path)) {
            seed.FlushSave(force: true);
            Require(condition: File.Exists(path: path), detail: "first forced flush did not create a save");
        }
        var initial = File.ReadAllBytes(path: path);
        initial[0] = 0x11;
        File.WriteAllBytes(path: path, bytes: initial);
        using (var core = new AdvancedGamingBrickCore(bios: bios, cartridgeRom: rom, savePath: path)) {
            var cleanLength = core.CaptureState(buffer: ref clean);
            using var sibling = PostMachine.BuildInstance(bios: bios, rom: rom);
            var cartridge = sibling.GetRequiredService<AgbCartridge>();
            cartridge.WriteSave(address: 0, value: 0x22);
            cartridge.MarkSaveClean();
            var writer = new StateWriter(capacity: 4096);
            sibling.Machine.SerializeState(writer: writer);
            var future = writer.ToArray();
            core.RestoreState(buffer: future, length: future.Length);
            core.FlushSave(force: false);
            Require(condition: File.ReadAllBytes(path: path)[0] == 0x22, detail: "restored state did not flush without a cartridge write");
            core.RestoreState(buffer: clean, length: cleanLength);
            core.FlushSave(force: false);
            Require(condition: File.ReadAllBytes(path: path)[0] == 0x11, detail: "restoring a clean snapshot left the abandoned future on disk");
            var after = new byte[clean.Length];
            var afterLength = core.CaptureState(buffer: ref after);
            Require(condition: afterLength == cleanLength && clean.AsSpan(start: 0, length: cleanLength).SequenceEqual(other: after.AsSpan(start: 0, length: afterLength)), detail: "host persistence changed clean snapshot bytes");

            if (OperatingSystem.IsWindows()) {
                core.RestoreState(buffer: future, length: future.Length);
                // Allow writes but forbid deletion: direct truncation would succeed, atomic replacement must fail.
                using (var held = new FileStream(path: path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.ReadWrite)) {
                    core.FlushSave(force: false);
                    Require(condition: File.ReadAllBytes(path: path)[0] == 0x11, detail: "failed save replacement damaged the previous file");
                }
                core.FlushSave(force: false);
                Require(condition: File.ReadAllBytes(path: path)[0] == 0x22, detail: "failed replacement was marked clean instead of retried");
            }
            core.RestoreState(buffer: clean, length: cleanLength);
            core.FlushSave(force: true);
            initial[0] = 0x33;
            File.WriteAllBytes(path: path, bytes: initial);
            if (OperatingSystem.IsWindows()) {
                using (var held = new FileStream(path: path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.ReadWrite)) {
                    core.FlushSave(force: true);
                    Require(condition: File.ReadAllBytes(path: path)[0] == 0x33, detail: "failed forced flush damaged the previous file");
                }
                core.FlushSave(force: false);
                Require(condition: File.ReadAllBytes(path: path)[0] == 0x11, detail: "failed clean forced flush did not retry");
                File.WriteAllBytes(path: path, bytes: initial);
            }
            core.FlushSave(force: true);
            Require(condition: File.ReadAllBytes(path: path)[0] == 0x11, detail: "forced flush skipped a clean existing save");
            core.RestoreState(buffer: future, length: future.Length);
        }
        Require(condition: File.ReadAllBytes(path: path)[0] == 0x22, detail: "disposal did not persist restored state");
        Require(condition: Directory.GetFiles(path: directory, searchPattern: ".agb-save-*.tmp").Length == 0, detail: "save replacement leaked temporary files");
    }

    private static void Require(bool condition, string detail) {
        if (!condition) {
            throw new InvalidOperationException(message: detail);
        }
    }
}
