using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>The guest verbs a deploying controller runs on a hosting machine through its provider's remote command
/// (<c>puck world host guard|probe|request</c>): each reads the machine record (<see cref="WorldHostMachine"/>) for its
/// store, owner and ports, so a guest command carries only what changes per effect, and each prints what the controller
/// parses. They replace the shell, Python and curl a guest command once carried.</summary>
public static class WorldHostGuest {
    private static readonly HttpClient Loopback = new(handler: new SocketsHttpHandler { UseProxy = false });

    /// <summary>Reads the engine the silo unit runs: the payload identity of the installed version its unit starts in,
    /// which a reinstall cannot change under a running silo.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The engine's payload identity, or <see langword="null"/> when no unit names an installed version.</returns>
    public static async Task<string?> RunningEngineAsync(CancellationToken cancellationToken) {
        var shown = await CliProcess.RunAsync(arguments: ["show", "-p", "WorkingDirectory", "--value", WorldHostLayout.SiloUnit], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false);

        return (((shown.ExitCode == 0) && (shown.Stdout.Trim() is { Length: > 0 } version) && Directory.Exists(path: version)) ? WorldHostEngine.Of(version: version) : null);
    }
    /// <summary>Sends one request to the silo's lifecycle listener on loopback.</summary>
    /// <param name="port">The listener's port.</param>
    /// <param name="path">The route.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="timeout">The request's bound; <see langword="null"/> is thirty seconds.</param>
    /// <returns>The status code and body; a request that got no answer has code 0 and the error as its body.</returns>
    public static async Task<(int Code, string Body)> GetAsync(int port, string path, CancellationToken cancellationToken, string method = "GET", TimeSpan? timeout = null) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

        deadline.CancelAfter(delay: (timeout ?? TimeSpan.FromSeconds(seconds: 30)));
        try {
            using var request = new HttpRequestMessage(method: new HttpMethod(method: method), requestUri: $"http://127.0.0.1:{port.ToString(provider: CultureInfo.InvariantCulture)}{path}");
            using var response = await Loopback.SendAsync(cancellationToken: deadline.Token, request: request).ConfigureAwait(continueOnCapturedContext: false);

            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false));
        } catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException) {
            return (0, exception.Message);
        }
    }
    /// <summary>Checks a delayed effect's request against the group record; the caller holds the release lock.</summary>
    /// <param name="request">The base64 JSON <see cref="WorldReleaseGuestGuardRequest"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>0 when the effect may run; 1 when it is refused, with the reason on standard error.</returns>
    public static async Task<int> GuardAsync(string request, CancellationToken cancellationToken) {
        var machine = WorldHostMachine.Read(path: WorldHostMachine.Path);
        var guard = (JsonSerializer.Deserialize<WorldReleaseGuestGuardRequest>(utf8Json: Convert.FromBase64String(s: request)) ?? throw new InvalidDataException(message: "the guard request is empty"));

        foreach (var (name, value) in machine.Environment) { Environment.SetEnvironmentVariable(variable: name, value: value); }
        var store = WorldHostStore.Open(owner: machine.Owner, store: machine.Store);

        if (await WorldReleaseGuestGuard.CheckAsync(cancellationToken: cancellationToken, groups: store.Groups, request: guard).ConfigureAwait(continueOnCapturedContext: false) is { } refusal) {
            Console.Error.WriteLine(value: $"world release guest guard refused: {refusal}");
            return 1;
        }
        return 0;
    }
    /// <summary>Prints, as one JSON object, the engine the machine runs and whether its silo unit is active, then (when it
    /// is) the release status and private health: <c>{process:{engine, running}, status:{code, body}, health:{code, body}}</c>.</summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>0.</returns>
    public static async Task<int> ProbeAsync(CancellationToken cancellationToken) {
        var port = ReadPort();
        var running = ((await CliProcess.RunAsync(arguments: ["is-active", "--quiet", WorldHostLayout.SiloUnit], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false)).ExitCode == 0);
        var idle = (0, "the world host is not running");
        var status = (running ? await GetAsync(cancellationToken: cancellationToken, path: "/release/status", port: port).ConfigureAwait(continueOnCapturedContext: false) : idle);
        var health = (running ? await GetAsync(cancellationToken: cancellationToken, path: "/private-healthz", port: port).ConfigureAwait(continueOnCapturedContext: false) : idle);

        Console.Out.Write(value: new JsonObject {
            ["process"] = new JsonObject { ["engine"] = await RunningEngineAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false), ["running"] = running },
            ["status"] = new JsonObject { ["code"] = status.Item1, ["body"] = status.Item2 },
            ["health"] = new JsonObject { ["code"] = health.Item1, ["body"] = health.Item2 },
        }.ToJsonString());
        return 0;
    }
    /// <summary>Sends one release-control request to the silo and prints its body; a failed request prints the body, the
    /// worker's refusal reason, to standard error.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The route.</param>
    /// <param name="timeoutSeconds">The request's bound, in seconds.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>0 when the silo answered with success; otherwise 1.</returns>
    public static async Task<int> RequestAsync(string method, string path, int timeoutSeconds, CancellationToken cancellationToken) {
        var (code, body) = await GetAsync(cancellationToken: cancellationToken, method: method, path: path, port: ReadPort(), timeout: TimeSpan.FromSeconds(seconds: timeoutSeconds)).ConfigureAwait(continueOnCapturedContext: false);

        if (code is >= 200 and < 300) {
            Console.Out.Write(value: body);
            return 0;
        }
        Console.Error.WriteLine(value: $"{method} {path} answered {code}: {body}");
        return 1;
    }
    // A machine whose bootstrap never wrote its record answers on no port: the probe then reports it idle.
    private static int ReadPort() {
        try { return WorldHostMachine.Read(path: WorldHostMachine.Path).Inputs.HealthPort; } catch (InvalidDataException) { return 0; }
    }

    /// <summary>Encodes a guard request for <see cref="GuardAsync"/>'s argument.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The base64 JSON.</returns>
    public static string Encode(WorldReleaseGuestGuardRequest request) => Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: JsonSerializer.Serialize(value: request)));
}
