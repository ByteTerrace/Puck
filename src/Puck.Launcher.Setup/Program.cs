using Microsoft.Extensions.DependencyInjection;
using Puck.Confinement;
using Puck.Launcher;
using Puck.Launcher.Release;
using Puck.Launcher.Setup;

// Installs or updates a player build from a release channel into an install root, through the ordinary update chain:
// the channel's signed manifest is verified against the anchor the root pinned, its files are staged by content pin and
// the release record names the new version current. The stub the release carries is then laid beside the record, so the
// stub starts the installed version from then on.
SetupArguments arguments;

try {
    arguments = SetupArguments.Parse(args: args);
} catch (ArgumentException exception) {
    Console.Error.WriteLine(value: $"setup: {exception.Message}");
    Console.Error.WriteLine(value: SetupArguments.Usage);
    return 2;
}
if (arguments.Help) {
    Console.Out.WriteLine(value: SetupArguments.Usage);
    return 0;
}

var root = arguments.InstallRoot;
ChannelSubscription subscription;

if (ChannelSubscription.TryLoad(error: out var loadError, installRoot: root, subscription: out var pinned)) {
    if (((arguments.Source is { } named) && !string.Equals(a: named.TrimEnd('/'), b: pinned!.Source.TrimEnd('/'), comparisonType: StringComparison.Ordinal)) ||
        ((arguments.Channel is { } channel) && !string.Equals(a: channel, b: pinned!.Channel, comparisonType: StringComparison.Ordinal))) {
        Console.Error.WriteLine(value: $"setup: {root} already follows {pinned!.Source} ({pinned.Channel}); install another channel into another directory.");
        return 2;
    }
    subscription = pinned!;
} else if (loadError is not null) {
    Console.Error.WriteLine(value: $"setup: {Path.Combine(path1: root, path2: ChannelSubscription.FileName)} could not be read: {loadError}");
    return 2;
} else if (arguments.Source is null) {
    Console.Error.WriteLine(value: $"setup: {root} follows no channel yet; name the channel's address.");
    Console.Error.WriteLine(value: SetupArguments.Usage);
    return 2;
} else {
    var channel = (arguments.Channel ?? SetupArguments.DefaultChannel);
    var anchor = await SetupTrust.FetchAnchorAsync(channel: channel, source: arguments.Source, cancellationToken: CancellationToken.None);

    if (anchor.Refusal is { } refusal) {
        Console.Error.WriteLine(value: $"setup: {refusal}");
        return 1;
    }
    if ((arguments.Trust is { } expected) && !string.Equals(a: expected, b: anchor.Anchor!.Domain, comparisonType: StringComparison.OrdinalIgnoreCase)) {
        Console.Error.WriteLine(value: $"setup: the channel's root key is {anchor.Anchor.Domain}, not the {expected} --trust names; nothing was installed.");
        return 1;
    }
    subscription = new ChannelSubscription(Channel: channel, Source: arguments.Source, TrustAnchor: anchor.Anchor!);
    subscription.Write(installRoot: root);
    Console.Out.WriteLine(value: $"setup: {root} now follows {subscription.Source} ({subscription.Channel}), pinned to root key {subscription.TrustAnchor.Domain}.");
}

var current = (ReleasePolicy.ReadUnlocked(installRoot: root).Current ?? "0.0.0");
var services = new ServiceCollection();

var channelSource = subscription.ToReleaseSource(httpClient: HttpReleaseSource.SharedClient);

services.AddSelfUpdate(
    options: new UpdateOptions(App: arguments.App, CacheRoot: root, Channel: subscription.Channel, InstalledVersion: current, TrustAnchor: subscription.TrustAnchor),
    releaseSource: ((arguments.Release is { } release) ? new PinnedReleaseSource(inner: channelSource, length: arguments.ReleaseSize, pin: release) : channelSource)
);
UpdateApplyOutcome applied;

await using (var provider = services.BuildServiceProvider()) {
    var updater = provider.GetRequiredService<UpdateService>();
    var check = await updater.CheckAsync(cancellationToken: CancellationToken.None, now: DateTimeOffset.UtcNow);

    if (check.Outcome != UpdateCheckOutcome.Available) {
        applied = new UpdateApplyOutcome(Applied: false, Detail: $"{check.Outcome}: {check.Detail}");
    } else {
        Console.Out.WriteLine(value: $"setup: installing {check.Manifest!.Version} over {current}{((check.Manifest.Notes is { Length: > 0 } notes) ? $" ({notes})" : string.Empty)}.");
        applied = await updater.ApplyAsync(cancellationToken: CancellationToken.None, now: DateTimeOffset.UtcNow, waitForOtherAppliers: true);
    }
}

var installed = ReleasePolicy.ReadUnlocked(installRoot: root).Current;

if (!applied.Applied) {
    Console.Out.WriteLine(value: $"setup: nothing new was installed: {applied.Detail}");
    if (installed is null) { return 1; }
}
try {
    SetupInstall.LayLauncher(installRoot: root, start: arguments.Start, version: installed!);
} catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
    Console.Error.WriteLine(value: $"setup: version {installed} is installed, but its launcher could not be laid beside it: {exception.Message}");
    return 1;
}
Console.Out.WriteLine(value: $"setup: {installed} is current in {root}.");
Console.Out.WriteLine(value: $"setup: start it with {SetupInstall.StartCommand(installRoot: root, version: installed!)}");
return 0;
