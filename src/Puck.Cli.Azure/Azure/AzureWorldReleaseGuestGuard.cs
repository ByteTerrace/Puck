using System.Text;
using System.Text.Json.Nodes;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    private const string WorldReleaseGuestLock = "exec 9>/run/puck-world-release.lock || exit $?\nflock -w 600 9 || exit $?\n";

    private static string WorldReleaseGuestGuard(JsonObject request) => WorldReleaseGuestGuard(
        request: request,
        script: File.ReadAllBytes(path: "build/Guard-WorldRelease.py")
    );
    private static string WorldReleaseGuestGuard(JsonObject request, byte[] script) {
        var argument = Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: request.ToJsonString()));

        return $"printf '%s' '{Convert.ToBase64String(inArray: script)}' | base64 -d | python3 - '{argument}' || exit $?";
    }
}
