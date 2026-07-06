using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// A GamingBrick emulator machine as a viewport content source — a <see cref="ViewportSource"/> kind interchangeable
/// with a virtual SDF camera at the viewport seam, mirroring <see cref="LiveCameraSource"/>. Pure data: the cartridge
/// ROM to load, the console costume to wear (<c>dmg</c>, <c>cgb</c>, or <c>agb</c> — the same machine, different
/// hardware truth), and how the 160×144 output maps into the viewport rect. The host builds and steps the machine;
/// this record never references the emulator.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GamingBrickSource : ViewportSource {
    /// <summary>The console models a source may name.</summary>
    public static readonly IReadOnlyList<string> SupportedModels = ["dmg", "cgb", "agb"];
    /// <summary>The speed policies a source may name.</summary>
    public static readonly IReadOnlyList<string> SupportedSpeeds = ["hardware", "dmg"];
    /// <summary>The cartridge peripheral feeds a source may name.</summary>
    public static readonly IReadOnlyList<string> SupportedPeripherals = ["camera", "none", "world"];

    /// <summary>The console costume the machine wears: <c>dmg</c>, <c>cgb</c>, or <c>agb</c>.</summary>
    public string Model { get; init; } = "cgb";
    /// <summary>How the machine's 160×144 output is fit into the viewport rect.</summary>
    public CameraFit Fit { get; init; } = CameraFit.Sample;
    // NOTE for the optional fields below: a polymorphic-derived record deserialized through the run-document
    // parse path does NOT run property initializers (the out-of-order-metadata handling creates the instance without
    // the parameterless constructor), so an omitted member arrives NULL regardless of any initializer. Optional
    // document fields must therefore be declared nullable, validated only when present, and normalized at consumption.
    /// <summary>The PRE-INSERTED cartridge ROM image path (<c>.gb</c>/<c>.gbc</c>), or null for a stand that starts
    /// EMPTY — the player must carry a cartridge from the <see cref="OverworldNode.Library"/> shelf and insert it before
    /// the stand can boot. A document with any empty stand must declare a non-empty library (an empty stand with no
    /// library to draw from is dead weight).</summary>
    public string? RomPath { get; init; }
    /// <summary>The tick→cycle speed policy: <c>hardware</c> (the default, and what null means — the KEY1 double-speed
    /// latch doubles the budget, matching real wall-clock pacing) or <c>dmg</c> (the FAIRNESS mode: the budget is
    /// pinned to the DMG's 2²² T-cycles per second regardless of KEY1, so every machine in a run consumes identical
    /// cycle counts per engine tick — a double-speed section then runs at half wall-rate instead of gaining ground).</summary>
    public string? Speed { get; init; }
    /// <summary>The capability the machine actually BOOTS as, independent of the costume the source displays as
    /// (<see cref="Model"/> keeps naming the stand's identity/accent). Null (the default) boots the costume itself;
    /// <c>dmg</c> on a Color costume is the UNIFORM-DEMOTE debuff: the machine seeds the DMG boot handoff, so a
    /// dual-mode cartridge takes its monochrome code path — every demoted machine runs the SAME code path as a real
    /// DMG and stays bit-locked with one. Any supported model is accepted (a promotion is just as expressible).</summary>
    public string? RunAs { get; init; }
    /// <summary>An optional FOURTH-WALL exit instrumentation: the host polls one work-RAM address after each stepped
    /// frame and requests a clean shutdown once the comparison holds — the seam that lets an in-game moment (e.g.
    /// a representative cartridge committing a save flag: a work-RAM byte at <c>0xDA22</c> going nonzero) end the run. Honored
    /// by the world path's brick viewports; a deterministic READ of emulated state, never a write into it.</summary>
    public BrickExitCondition? Exit { get; init; }
    /// <summary>An optional 128-bit WIN condition (see <see cref="BrickVictoryCondition"/>): the host reads the top 16 bytes
    /// of the cartridge's external RAM (the highest SRAM address — bank <c>0x0F</c> of a 128&#160;KiB MBC5 cart) after each
    /// stepped frame and fires once the game converges them onto the gate constant. <c>solo</c> wins the cabinet alone;
    /// <c>meta</c> wins the room when the XOR across the group's cabinets equals the target. Honored by the overworld
    /// (meta needs the room to combine cabinets); a deterministic READ of emulated state, never a write into it.</summary>
    public BrickVictoryCondition? Victory { get; init; }
    /// <summary>An optional cartridge PERIPHERAL/sensor feed — the seam through which the outside world reaches the
    /// emulated machine. <c>camera</c> (also what null means for a Pocket Camera cartridge, header <c>0xFC</c>) binds the
    /// PC webcam as the M64282FP image source; <c>none</c> keeps the built-in deterministic sensor so a capture is
    /// reproducible (validation, golden strips); <c>world</c> binds the WORLD→MACHINE membrane, writing the room player's
    /// position into the cartridge's work-RAM sensor page each frame so a world-lens ROM mirrors the room it sits in.
    /// Camera/none are ignored by a cartridge without a sensor; world by a cartridge that never reads the sensor page.</summary>
    public string? Peripheral { get; init; }

    /// <summary>Whether this stand starts with a cartridge already seated (<see cref="RomPath"/> is present).</summary>
    [JsonIgnore]
    public bool IsPreInserted => (RomPath is not null);

    internal override void Validate(string path, ValidationErrors errors) {
        if ((RomPath is not null) && string.IsNullOrWhiteSpace(value: RomPath)) {
            errors.Add(path: $"{path}.romPath", message: "romPath, when present, must name a cartridge ROM image (or be omitted for an empty stand)");
        }

        if (!SupportedModels.Contains(value: Model, comparer: StringComparer.OrdinalIgnoreCase)) {
            errors.Add(path: $"{path}.model", message: $"model '{Model}' is not one of: {string.Join(separator: ", ", values: SupportedModels)}");
        }

        if ((Speed is not null) && !SupportedSpeeds.Contains(value: Speed, comparer: StringComparer.OrdinalIgnoreCase)) {
            errors.Add(path: $"{path}.speed", message: $"speed '{Speed}' is not one of: {string.Join(separator: ", ", values: SupportedSpeeds)}");
        }

        if ((RunAs is not null) && !SupportedModels.Contains(value: RunAs, comparer: StringComparer.OrdinalIgnoreCase)) {
            errors.Add(path: $"{path}.runAs", message: $"runAs '{RunAs}' is not one of: {string.Join(separator: ", ", values: SupportedModels)}");
        }

        if ((Peripheral is not null) && !SupportedPeripherals.Contains(value: Peripheral, comparer: StringComparer.OrdinalIgnoreCase)) {
            errors.Add(path: $"{path}.peripheral", message: $"peripheral '{Peripheral}' is not one of: {string.Join(separator: ", ", values: SupportedPeripherals)}");
        }

        Exit?.Validate(path: $"{path}.exit", errors: errors);
        Victory?.Validate(path: $"{path}.victory", errors: errors);
    }
}

/// <summary>
/// A gaming-brick viewport's fourth-wall exit instrumentation: when the byte at a work-RAM <see cref="Address"/>
/// satisfies <see cref="Op"/> against <see cref="Value"/> after a stepped frame, the host requests a clean shutdown.
/// Pure data; the host owns the polling and the exit.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrickExitCondition {
    /// <summary>The comparison operators a condition may name.</summary>
    public static readonly IReadOnlyList<string> SupportedOps = ["==", "!=", ">=", "<=", ">", "<"];

    /// <summary>The work-RAM address to poll, as a hex string (<c>"0xC000"</c>–<c>"0xDFFF"</c>). Read through the
    /// machine's CURRENT work-RAM banking (a DMG-mode game's switchable half is fixed to bank 1).</summary>
    public string Address { get; init; } = "";
    /// <summary>The comparison operator (one of <see cref="SupportedOps"/>).</summary>
    public string Op { get; init; } = ">=";
    /// <summary>The byte value the read is compared against (0–255).</summary>
    public int Value { get; init; }
    /// <summary>An optional label for the exit log line (e.g. <c>"starter selected"</c>).</summary>
    public string? Label { get; init; }

    /// <summary>Parses <see cref="Address"/> (validated documents always succeed).</summary>
    /// <param name="address">The parsed address.</param>
    /// <returns>Whether the address parsed as a <c>0x</c>-prefixed hex ushort.</returns>
    public bool TryParseAddress(out ushort address) {
        address = 0;

        return Address.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(s: Address.AsSpan(start: 2), style: System.Globalization.NumberStyles.HexNumber, provider: null, result: out address);
    }

    internal void Validate(string path, ValidationErrors errors) {
        if (!TryParseAddress(address: out var address) || (address < 0xC000) || (address > 0xDFFF)) {
            errors.Add(path: $"{path}.address", message: $"address '{Address}' must be a 0x-prefixed hex work-RAM address in [0xC000, 0xDFFF]");
        }

        if (!SupportedOps.Contains(value: Op, comparer: StringComparer.Ordinal)) {
            errors.Add(path: $"{path}.op", message: $"op '{Op}' is not one of: {string.Join(separator: ", ", values: SupportedOps)}");
        }

        if ((Value < 0) || (Value > 255)) {
            errors.Add(path: $"{path}.value", message: $"value {Value} is outside a byte's range [0, 255]");
        }
    }
}

/// <summary>
/// One cartridge on the overworld's shelf — a game the player can carry to a stand and insert, distinct from a stand's
/// PRE-INSERTED <see cref="GamingBrickSource.RomPath"/> (the boot-ready default). Pure data: a title for the
/// presentation layer plus the ROM image path, validated and loaded eagerly with every other cartridge at document
/// load (fail-fast stays at load time; only MACHINE assembly moves to insert time).
/// </summary>
/// <param name="Title">The cartridge's display title (the shelf/carry presentation label).</param>
/// <param name="RomPath">The cartridge ROM image path (<c>.gb</c>/<c>.gbc</c>). Multiple library entries may name the
/// SAME path (one ROM, several titled cartridges) — content-addressing the loaded image is a host-side concern, not
/// a document one.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeSource(string Title, string RomPath) {
    /// <summary>The most cartridges a shelf may hold.</summary>
    public const int MaxEntries = 8;

    internal void Validate(string path, ValidationErrors errors) {
        if (string.IsNullOrWhiteSpace(value: Title)) {
            errors.Add(path: $"{path}.title", message: "title must be non-empty");
        }

        if (string.IsNullOrWhiteSpace(value: RomPath)) {
            errors.Add(path: $"{path}.romPath", message: "romPath must name a cartridge ROM image");
        }
    }
}
