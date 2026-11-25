using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.Assets;
using Puck.Launcher.Release;

namespace Puck.Cli.Automation;

/// <summary>One signed host release as a machine installs it: the channel it is published on, the root key that channel
/// is pinned to, the content pin and size of its signed manifest (an object the channel keeps for good, so the release
/// stays installable after the channel moves on), the installer its payload carries, and the engine identity its payload
/// has. A hosting machine and a controller that qualifies the release both install it through <see cref="InstallAsync"/>'s
/// arguments, the same installer and update chain a player's machine uses.</summary>
/// <param name="Source">The channel tree's root: an <c>https</c> URL or a directory.</param>
/// <param name="Trust">The channel's root key fingerprint the install pins.</param>
/// <param name="Manifest">The content pin of the signed manifest's bytes.</param>
/// <param name="ManifestSize">The signed manifest's byte length.</param>
/// <param name="Installer">The content pin of the installer (<c>launcher/Puck.Launcher.Setup</c>) the payload carries.</param>
/// <param name="Engine">The payload identity of the release's payload for the machine's runtime.</param>
public sealed record WorldHostRelease(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("trust")] string Trust,
    [property: JsonPropertyName("manifest")] string Manifest,
    [property: JsonPropertyName("manifestSize")] long ManifestSize,
    [property: JsonPropertyName("installer")] string Installer,
    [property: JsonPropertyName("engine")] string Engine
) {
    /// <summary>The runtime a hosting machine runs.</summary>
    public const string MachineRid = "linux-x64";

    /// <summary>Reads a signed host release from the channel tree <c>puck channel push</c> signed.</summary>
    /// <param name="tree">The release tree's root, holding <c>server/manifest.json</c> and <c>server/trust.json</c>.</param>
    /// <param name="source">The address machines install the tree from (its published URL, or the tree itself).</param>
    /// <param name="rid">The runtime of the machines that install it; <see langword="null"/> is <see cref="MachineRid"/>.</param>
    /// <returns>The release.</returns>
    /// <exception cref="InvalidDataException">The tree's manifest is unsigned, carries no payload for the runtime or no
    /// installer, or its trust file is missing.</exception>
    public static WorldHostRelease FromTree(string tree, string source, string? rid = null) {
        rid ??= MachineRid;
        // The installer inside a payload, as the launcher directory lays it.
        var installerPath = $"{PlayerBuildLayout.LauncherDirectoryName}/Puck.Launcher.Setup{(rid.StartsWith(value: "win", comparisonType: StringComparison.Ordinal) ? ".exe" : "")}";
        var directory = Path.Combine(path1: tree, path2: WorldHostLayout.Channel);
        var bytes = File.ReadAllBytes(path: Path.Combine(path1: directory, path2: ChannelSubscription.ManifestFileName));
        var manifest = (JsonSerializer.Deserialize<ReleaseManifest>(options: DocumentJsonOptions.Shared, utf8Json: bytes) ?? throw new InvalidDataException(message: "the host release manifest is empty"));

        if (manifest.Signature is null) { throw new InvalidDataException(message: $"{directory} holds an unsigned manifest; push the channel first."); }
        var anchor = (ChannelSubscription.ParseAnchor(bytes: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: ChannelSubscription.TrustFileName)))
            ?? throw new InvalidDataException(message: $"{directory} holds no trust anchor."));
        var payload = (manifest.Payloads.SingleOrDefault(predicate: payload => payload.Rid == rid) ?? throw new InvalidDataException(message: $"the host release carries no {rid} payload"));
        var installer = (payload.Files.SingleOrDefault(predicate: file => file.Path == installerPath) ?? throw new InvalidDataException(message: $"the host release carries no {installerPath}"));

        return new(Engine: ReleasePayloadIdentity.Of(payload: payload, stateGeneration: manifest.StateGeneration), Installer: installer.Hash,
            Manifest: ContentPin.Compute(content: bytes).ToString(), ManifestSize: bytes.LongLength, Source: source.TrimEnd('/'), Trust: anchor.Domain);
    }
    /// <summary>Returns the address of the installer object in the channel's object space.</summary>
    /// <returns>The installer's URL or path.</returns>
    public string InstallerAddress() => $"{Source}/{ContentAddressedStore.ObjectRelativePath(pin: ContentPin.Parse(text: Installer))}";
    /// <summary>Returns the installer's file name as a download names it: the last segment of its address.</summary>
    public string InstallerFileName => ContentPin.Parse(text: Installer).Hex;
    /// <summary>Returns the installer's arguments that install exactly this release into an install root.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <returns>The arguments, in order.</returns>
    public string[] InstallerArguments(string installRoot) => [
        Source, "--to", installRoot, "--channel", WorldHostLayout.Channel, "--trust", Trust, "--app", WorldHostLayout.App,
        "--release", Manifest, "--release-size", ManifestSize.ToString(provider: CultureInfo.InvariantCulture), "--start", WorldHostLayout.Executable,
    ];
    /// <summary>Installs this release into an install root on this machine through the installer the release carries,
    /// fetched by its pin and checked against it, and returns the installed version's directory.</summary>
    /// <param name="installRoot">The install root; an existing root must follow the same channel.</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <returns>The installed version's directory.</returns>
    /// <exception cref="InvalidDataException">The installer does not match its pin, or the install does not hold this
    /// release's engine afterwards.</exception>
    /// <exception cref="InvalidOperationException">The installer failed.</exception>
    public async Task<string> InstallAsync(string installRoot, CancellationToken cancellationToken) {
        var root = Path.GetFullPath(path: installRoot);

        if ((WorldHostEngine.Current(installRoot: root) is { } current) && (current.Identity == Engine)) { return current.Version; }
        var scratch = Directory.CreateTempSubdirectory(prefix: "puck-host-installer-");

        try {
            return await InstallFromAsync(installer: Path.Combine(path1: scratch.FullName, path2: InstallerFileName), root: root, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            scratch.Delete(recursive: true);
        }
    }
    private async Task<string> InstallFromAsync(string installer, string root, CancellationToken cancellationToken) {
        byte[] bytes;

        if (ChannelSubscription.IsHttpSource(source: Source)) {
            bytes = await HttpReleaseSource.SharedClient.GetByteArrayAsync(cancellationToken: cancellationToken, requestUri: InstallerAddress()).ConfigureAwait(continueOnCapturedContext: false);
        } else {
            bytes = await File.ReadAllBytesAsync(cancellationToken: cancellationToken, path: InstallerAddress()).ConfigureAwait(continueOnCapturedContext: false);
        }
        if (ContentPin.Compute(content: bytes).ToString() != Installer) { throw new InvalidDataException(message: $"the installer read from {InstallerAddress()} does not match its pin"); }
        await File.WriteAllBytesAsync(bytes: bytes, cancellationToken: cancellationToken, path: installer).ConfigureAwait(continueOnCapturedContext: false);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, path: installer); }
        var run = await CliProcess.RunAsync(arguments: InstallerArguments(installRoot: root), cancellationToken: cancellationToken, capture: false, fileName: installer).ConfigureAwait(continueOnCapturedContext: false);

        if (run.ExitCode != 0) { throw new InvalidOperationException(message: $"the host release installer exited with {run.ExitCode}"); }
        return WorldHostEngine.Resolve(expected: Engine, installRoot: root);
    }
}
