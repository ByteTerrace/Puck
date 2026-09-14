using System.Text;
using System.Text.Json.Nodes;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private const string WorldReleaseGuestLock = "exec 9>/run/puck-world-release.lock || exit $?\nflock -w 600 9 || exit $?\n";

    private static string WorldReleaseGuestGuard(JsonObject request) {
        var script = Convert.ToBase64String(inArray: File.ReadAllBytes(path: "build/Guard-WorldRelease.py"));
        var argument = Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: request.ToJsonString()));

        return $"printf '%s' '{script}' | base64 -d | python3 - '{argument}' || exit $?";
    }
}
