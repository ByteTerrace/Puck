using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    // Every delayed guest effect holds the machine's release lock across its guard and the effect.
    private static readonly string WorldReleaseGuestLock = $"exec 9>{WorldHostLayout.ReleaseLock} || exit $?\nflock -w 600 9 || exit $?\n";

    // The guard runs as the installed CLI's guest verb, which reads the group record through the machine's own store.
    private static string WorldReleaseGuestGuard(WorldReleaseGuestGuardRequest request) =>
        $"{WorldHostLayout.HostExecutable} world host guard '{WorldHostGuest.Encode(request: request)}' || exit $?";
}