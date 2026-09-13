using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Puck.Mcp;

public static partial class RemoteMcpServer {
    /// <summary>Reads bounded deployment JSON, closes the file, and resolves paths relative to its directory.</summary>
    /// <param name="configurationPath">The deployment document.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>A validated snapshot.</returns>
    public static async Task<RemoteMcpOptions> ReadOptionsAsync(string configurationPath, CancellationToken cancellationToken = default) {
        var path = Path.GetFullPath(path: configurationPath);
        await using var file = new FileStream(
            access: FileAccess.Read,
            bufferSize: 4096,
            mode: FileMode.Open,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan,
            path: path,
            share: FileShare.ReadWrite | FileShare.Delete
        );

        if (file.Length is <= 0 or > 65536) { throw new InvalidDataException(message: "Remote MCP configuration must be 1..65536 bytes."); }
        var data = new byte[((int)file.Length)];

        await file.ReadExactlyAsync(
            buffer: data,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        if (file.ReadByte() != -1) { throw new InvalidDataException(message: "Remote MCP configuration changed while being read."); }
        var options = (JsonSerializer.Deserialize(
            data,
            OperatorMcpJson.Default.RemoteMcpOptions
        ) ?? throw new InvalidDataException(message: "Missing remote MCP configuration."));
        var directory = Path.GetDirectoryName(path: path)!;

        return (options with {
            CertificatePath = ((options.CertificatePath is { } certificate)
            ? Path.GetFullPath(
                certificate,
                directory
            )
            : null),
        }).Validate();
    }
    /// <summary>Reloads gateway access every second. Invalid configuration revokes access; other changes require restart.</summary>
    /// <param name="app">The running resource server.</param>
    /// <param name="configurationPath">Its deployment document.</param>
    /// <param name="initial">The validated startup snapshot.</param>
    /// <param name="cancellationToken">Stops monitoring.</param>
    /// <returns>The monitor lifetime.</returns>
    public static async Task WatchConfigurationAsync(WebApplication app, string configurationPath, RemoteMcpOptions initial, CancellationToken cancellationToken) {
        var policy = app.Services.GetRequiredService<RemoteMcpAccessPolicy>();
        var fingerprint = ConfigurationIdentity(options: initial);
        var failed = false;
        using var timer = new PeriodicTimer(period: TimeSpan.FromSeconds(seconds: 1));

        try {
            while (await timer.WaitForNextTickAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) {
                try {
                    var updated = await ReadOptionsAsync(
                        cancellationToken: cancellationToken,
                        configurationPath: configurationPath
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    if (!fingerprint.AsSpan().SequenceEqual(other: ConfigurationIdentity(options: updated))) { throw new InvalidDataException(message: "MCP identity or host settings changed; restart is required."); }
                    policy.Replace(subjects: updated.AllowedSubjects);
                    failed = false;
                } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)) {
                    policy.Replace(subjects: []);
                    if (!failed) { app.Logger.LogWarning("MCP configuration reload failed; all gateway access revoked. Check configuration and restart for identity or endpoint changes."); }
                    failed = true;
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static byte[] ConfigurationIdentity(RemoteMcpOptions options) => JsonSerializer.SerializeToUtf8Bytes(
        options with { AllowedSubjects = [] },
        OperatorMcpJson.Default.RemoteMcpOptions
    );
}
