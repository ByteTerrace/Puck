using System.CommandLine;
using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Launcher.Release;

namespace Puck.Cli.Channel;

/// <summary><c>puck channel push</c>: signs the release a <c>puck publish</c> tree holds with a channel host's root key and
/// writes the tree through that host, objects it lacks first and the manifest last, so consumers following the channel
/// (the installer, a World running from an install) see the release only once every file it names is there.</summary>
public static class ChannelCommand {
    private const string Verb = "channel push";

    /// <summary>Creates the <c>channel</c> verb and its <c>push</c> subverb.</summary>
    /// <returns>The verb.</returns>
    public static Command Create() => new(description: "Publish signed release channels.", name: "channel") { PushCommand() };

    private static Command PushCommand() {
        var tree = new Option<string>(name: "--tree") { Description = "The release-source tree `puck publish` wrote.", Required = true };
        var channel = new Option<string>(name: "--channel") { Description = "The channel whose manifest is signed and pushed.", Required = true };
        var sequence = new Option<ulong>(name: "--sequence") { Description = "The manifest's replay sequence: larger than every sequence this root signed for the application before (a CI run number).", Required = true };
        var host = new Option<string?>(name: "--host") { Description = "The installed channel host to write to and take the root key from (a deployment provider's, such as azure). Absent with --to, the local directory host; absent otherwise, the single installed host." };
        var to = new Option<string?>(name: "--to") { Description = "Mirror the channel into this directory instead of a provider's host, signed by --signing-key." };
        var key = new Option<string?>(name: "--signing-key") { Description = "With --to: the PKCS#8 root key file, created on first use." };
        var validity = CliOptions.Count(defaultValue: ((int)ReleaseSigner.DefaultValidity.TotalDays), description: "Days the signature verifies; at most the consumers' replay horizon.", maximum: ((int)ReleaseSigner.DefaultValidity.TotalDays), minimum: 1, name: "--validity-days");
        var command = new Command(name: "push", description: "Sign a puck publish tree's release with a channel host's root key and write the tree through the host.") {
            tree, channel, sequence, host, to, key, validity,
        };

        command.Detail(detail: """
            Reads <tree>/<channel>/manifest.json (unsigned), signs it with the host's root key (a fresh issuing and
            application key vouched for by the root), writes the signed manifest and <tree>/<channel>/trust.json (the
            root's anchor, which a first install pins), then writes the tree through the host: every object under
            <tree>/objects the host lacks, the files under <tree>/<channel>/ other than the manifest, and the manifest last.
            Exit codes: 0 pushed, 1 the host refused or failed, 2 a usage error or an unreadable tree.
            """);
        command.SetAction(action: async (parse, cancellationToken) => {
            var root = Path.GetFullPath(path: parse.GetRequiredValue(option: tree));
            var name = parse.GetRequiredValue(option: channel);
            var directory = parse.GetValue(option: to);
            var keyFile = parse.GetValue(option: key);
            ReleaseChannelHost selected;

            if ((directory is not null) && (keyFile is null)) { return CliExit.Refuse(verb: Verb, what: "--to", why: "needs --signing-key, the root key that signs the mirror."); }
            try {
                selected = ReleaseChannelHost.Open(directory: directory, keyFile: keyFile, name: parse.GetValue(option: host));
            } catch (InvalidOperationException exception) {
                return CliExit.Refuse(verb: Verb, what: "--host", why: exception.Message);
            }
            return await PushAsync(cancellationToken: cancellationToken, channel: name, host: selected, sequence: parse.GetRequiredValue(option: sequence), tree: root,
                validity: TimeSpan.FromDays(value: parse.GetValue(option: validity))).ConfigureAwait(continueOnCapturedContext: false);
        });
        return command;
    }
    /// <summary>Signs and pushes a channel's release.</summary>
    /// <param name="tree">The release-source tree's root.</param>
    /// <param name="channel">The channel's name.</param>
    /// <param name="host">The host to write through.</param>
    /// <param name="sequence">The manifest's replay sequence.</param>
    /// <param name="validity">How long the signature verifies.</param>
    /// <param name="cancellationToken">Cancels the push.</param>
    /// <returns>The verb's exit code.</returns>
    public static async Task<int> PushAsync(string tree, string channel, ReleaseChannelHost host, ulong sequence, TimeSpan validity, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: host);
        var manifestPath = Path.Combine(path1: tree, path2: channel, path3: ChannelSubscription.ManifestFileName);
        ReleaseManifest manifest;

        try {
            manifest = (JsonSerializer.Deserialize<ReleaseManifest>(options: DocumentJsonOptions.Shared, utf8Json: File.ReadAllBytes(path: manifestPath)) ?? throw new JsonException(message: "the manifest is empty"));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: manifestPath), why: $"is not a release manifest: {exception.Message}");
        }

        var objects = new List<string>();

        foreach (var file in manifest.Payloads.SelectMany(selector: static payload => payload.Files)) {
            var path = Puck.Assets.ContentAddressedStore.ObjectRelativePath(pin: Puck.Assets.ContentPin.Parse(text: file.Hash));

            if (!File.Exists(path: Path.Combine(path1: tree, path2: path))) {
                return CliExit.Refuse(verb: Verb, what: file.Path, why: $"is named by the manifest, but its object {path} is not in the tree.");
            }
            objects.Add(item: path);
        }

        try {
            using var key = await host.LoadSigningKeyAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            var signed = ReleaseSigner.Sign(manifest: (manifest with { Signature = null }), root: key, sequence: sequence, validity: validity);

            File.WriteAllBytes(path: manifestPath, bytes: JsonSerializer.SerializeToUtf8Bytes(options: DocumentJsonOptions.Shared, value: signed.Manifest));
            File.WriteAllBytes(path: Path.Combine(path1: tree, path2: channel, path3: ChannelSubscription.TrustFileName), bytes: ChannelSubscription.SerializeAnchor(anchor: signed.TrustAnchor));

            var channelDirectory = Path.Combine(path1: tree, path2: channel);
            var documents = Directory.EnumerateFiles(path: channelDirectory, searchOption: SearchOption.AllDirectories, searchPattern: "*")
                .Select(selector: path => Puck.Abstractions.PuckPaths.Relative(path: path, relativeTo: tree))
                .Where(predicate: path => !path.EndsWith(value: $"/{ChannelSubscription.ManifestFileName}", comparisonType: StringComparison.Ordinal))
                .Order(comparer: StringComparer.Ordinal)
                .Append(element: $"{channel}/{ChannelSubscription.ManifestFileName}")
                .ToArray();

            Console.Out.WriteLine(value: $"{Verb}: signed {manifest.App} {channel} {manifest.Version} (sequence {sequence}, root key {signed.TrustAnchor.Domain}); writing {objects.Distinct(comparer: StringComparer.Ordinal).Count()} object(s) the host lacks and {documents.Length} document(s) to {host.Describe}.");
            await host.UploadAsync(cancellationToken: cancellationToken, documents: documents, objects: [.. objects.Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal)], tree: tree).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or System.Security.Cryptography.CryptographicException) {
            Console.Error.WriteLine(value: $"{Verb}: {exception.Message}");
            return 1;
        }
        Console.Out.WriteLine(value: $"{Verb}: {manifest.App} {channel} {manifest.Version} is published to {host.Describe}.");
        return 0;
    }
}
