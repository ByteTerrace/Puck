using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The <see cref="IMachineEngine"/> for the SM83-family GamingBrick — the first implementation of the neutral
/// screen-machine contract. Its <see cref="Id"/> is <c>gaming-brick</c>, and its options vocabulary is a hardware
/// revision (a family token <c>dmg</c>/<c>cgb</c>/<c>agb</c>, default <c>dmg</c>, or a specific revision such as
/// <c>cgb0</c> or <c>sgb2</c>) plus <c>cold</c>/<c>fast</c> startup.
/// Bundled Puck firmware is the default; an optional final <c>bios=&lt;path&gt;</c> selects an external image.
/// A host resolves this engine by id and hands it cartridge bytes; the machine it builds is a <see cref="MachineHost"/>.
/// It also carries <see cref="IMachineLinkingEngine"/>: two of its running machines can be cable-linked into one
/// <see cref="LinkedMachineGroup"/>, which takes ownership of both cores and advances them over the deterministic
/// <see cref="SerialLinkSession"/> interleave until the link is disposed.
/// </summary>
public sealed partial class GamingBrickEngine : IMachineEngine, IMachineImageRecognizer, IMachineLinkingEngine, IMachineOperationProvider {
    // The serial port carries exactly one peer, so a cable link is a pair.
    private const int CableSeats = 2;
    // The revision a bare family token selects: the two steppings the accuracy work targets, plus the Advanced console.
    private const ConsoleModel ColorFamilyDefault = ConsoleModel.CgbE;
    private const ConsoleModel DefaultModel = DmgFamilyDefault;
    private const ConsoleModel DmgFamilyDefault = ConsoleModel.DmgC;

    // The option vocabulary: the three family tokens, then one token per revision named after its enum member.
    private static readonly Dictionary<string, ConsoleModel> ModelTokens = new(comparer: StringComparer.OrdinalIgnoreCase) {
        ["dmg"] = DmgFamilyDefault,
        ["cgb"] = ColorFamilyDefault,
        ["agb"] = ConsoleModel.Agb,
        ["dmg0"] = ConsoleModel.Dmg0,
        ["dmgb"] = ConsoleModel.DmgB,
        ["dmgc"] = ConsoleModel.DmgC,
        ["mgb"] = ConsoleModel.Mgb,
        ["sgb"] = ConsoleModel.Sgb,
        ["sgb2"] = ConsoleModel.Sgb2,
        ["cgb0"] = ConsoleModel.Cgb0,
        ["cgba"] = ConsoleModel.CgbA,
        ["cgbb"] = ConsoleModel.CgbB,
        ["cgbc"] = ConsoleModel.CgbC,
        ["cgbd"] = ConsoleModel.CgbD,
        ["cgbe"] = ConsoleModel.CgbE,
        ["ags"] = ConsoleModel.Ags,
    };

    /// <inheritdoc/>
    public string Id => "gaming-brick";
    /// <inheritdoc/>
    public int ImageHeaderLength => CartridgeHeader.Length;

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="options"/> holds an unknown token, or
    /// <paramref name="contentBytes"/> is not a cartridge the machine can run.</exception>
    public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
        var (model, boot) = ParseOptions(options: options);

        return new MachineHost(
            audioSampleRate: audioSampleRate,
            cartridgeRom: RequireRunnable(image: contentBytes),
            bootMode: boot.Mode,
            bootRomPath: boot.ImagePath,
            model: model,
            savePath: savePath
        );
    }
    /// <inheritdoc/>
    public string? RefuseImage(ReadOnlySpan<byte> header, long imageLength) {
        if (imageLength < CartridgeHeader.Length) {
            return ShortImage(imageLength: imageLength);
        }
        if (!CartridgeHeader.IsCartridgeImage(
            header: header,
            imageLength: imageLength
        )) {
            return "no gaming-brick cartridge header: the header checksum at 0x14D does not hold";
        }

        return Unrunnable(header: CartridgeHeader.Parse(rom: header[..CartridgeHeader.Length].ToArray()));
    }
    /// <inheritdoc/>
    public bool TryLink(IReadOnlyList<IMachineRuntime> machines, out IMachineLink? link, out string reason) {
        link = null;

        if (
            (machines is null) ||
            (machines.Count < 2)
        ) {
            reason = "a cable link needs two or more machines";

            return false;
        }

        // The console's serial port is a point-to-point cable: it carries exactly one peer, so a wider set is refused by
        // name rather than silently linking the first two.
        if (machines.Count > CableSeats) {
            reason = $"the gaming-brick serial cable links exactly {CableSeats} machines; {machines.Count} were supplied";

            return false;
        }

        var hosts = new MachineHost[machines.Count];

        for (var index = 0; (index < machines.Count); index++) {
            if (machines[index] is not MachineHost host) {
                reason = $"member {index} is not a gaming-brick machine";

                return false;
            }

            if (!host.IsAssigned) {
                reason = $"member {index} has no cartridge loaded";

                return false;
            }

            if (host.Worker.IsLent) {
                reason = $"member {index} is already cable-linked";

                return false;
            }

            for (var earlier = 0; (earlier < index); earlier++) {
                if (ReferenceEquals(
                    objA: hosts[earlier],
                    objB: host
                )) {
                    reason = $"member {index} is the same machine as member {earlier}";

                    return false;
                }
            }

            hosts[index] = host;
        }

        try {
            link = new LinkedMachineGroup(
                createCore: static cores => new SerialLinkGroupCore(
                    first: ((HumbleGamingBrickCore)cores[0]),
                    second: ((HumbleGamingBrickCore)cores[1])
                ),
                machines: hosts,
                maximumPendingSteps: MachineHost.DefaultMaximumPendingSteps,
                workerName: "Puck GamingBrick link",
                workers: [.. hosts.Select(selector: static host => host.Worker)]
            );
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            reason = exception.Message;

            return false;
        }

        reason = string.Empty;

        return true;
    }

    /// <summary>Parses the space-separated options string (order-independent) into a hardware revision and the firmware
    /// startup options — the one options grammar, shared by <see cref="Create"/> and a host's live reconfigure. A model
    /// keyword sets the revision. An unknown token throws so a typo is loud, not silently defaulted.</summary>
    /// <param name="options">The engine-specific options string, or <see langword="null"/> for defaults.</param>
    /// <param name="bootDefaults">Construction-fixed startup settings to retain when reconfiguring only hardware.</param>
    /// <returns>The parsed revision and firmware startup options.</returns>
    /// <exception cref="ArgumentException">A token is not a recognized option.</exception>
    internal static (ConsoleModel Model, MachineBootOptions Boot) ParseOptions(string? options, MachineBootOptions? bootDefaults = null) {
        var model = DefaultModel;
        var boot = MachineBootOptions.Parse(
            defaults: bootDefaults,
            machineTokens: out var machineTokens,
            options: options
        );

        foreach (var token in machineTokens) {
            if (TryParseModelToken(
                model: out var parsed,
                token: token
            )) {
                model = parsed;
            } else {
                throw new ArgumentException(message: $"unknown gaming-brick option '{token}' — expected one of {string.Join(
                    separator: '|',
                    values: ModelTokens.Keys
                )}, cold, fast, or a final bios=<path>");
            }
        }

        return (Model: model, Boot: boot);
    }
    /// <summary>Formats a revision and its startup options back into the canonical options string — the inverse of
    /// <see cref="ParseOptions"/>, so a host's <c>screen.options</c> echo and <c>world.save</c> readback speak the same
    /// vocabulary an author wrote. A family's default revision formats as the bare family token.</summary>
    /// <param name="model">The current revision.</param>
    /// <param name="boot">The construction-fixed firmware startup options.</param>
    /// <returns>The options string (e.g. <c>cgb cold</c>).</returns>
    internal static string FormatOptions(ConsoleModel model, MachineBootOptions boot) {
        var modelToken = model switch {
            DmgFamilyDefault => "dmg",
            ColorFamilyDefault => "cgb",
            _ => model.ToString().ToLowerInvariant(),
        };

        return $"{modelToken} {boot.Format()}";
    }

    // The image is read before the host exists, so a cartridge the machine cannot run is refused by name without
    // starting a worker. A header checksum that does not hold is the firmware's to refuse, as hardware's boot does.
    private static byte[]? RequireRunnable(byte[]? image) {
        if (image is null) {
            return null;
        }
        if (((image.Length < CartridgeHeader.Length)
            ? ShortImage(imageLength: image.Length)
            : Unrunnable(header: CartridgeHeader.Parse(rom: image))) is { } reason) {
            throw new ArgumentException(message: $"gaming-brick cannot run the cartridge: {reason}");
        }

        return image;
    }
    private static string ShortImage(long imageLength) =>
        $"the image is {imageLength} bytes, shorter than its 0x{CartridgeHeader.Length:X}-byte header";
    private static string? Unrunnable(CartridgeHeader header) => ((header.Mapper == MapperKind.Unimplemented)
        ? $"cartridge type 0x{header.TypeCode:X2} names a mapper the machine does not implement"
        : null
    );
    private static bool TryParseModelToken(string token, out ConsoleModel model) =>
        ModelTokens.TryGetValue(
            key: token,
            value: out model
        );
}
