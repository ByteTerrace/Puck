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
        var path = Path.GetFullPath(configurationPath);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is <= 0 or > 65536) { throw new InvalidDataException("Remote MCP configuration must be 1..65536 bytes."); }
        var data = new byte[(int)file.Length];
        await file.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        if (file.ReadByte() != -1) { throw new InvalidDataException("Remote MCP configuration changed while being read."); }
        var options = JsonSerializer.Deserialize(data, OperatorMcpJson.Default.RemoteMcpOptions) ?? throw new InvalidDataException("Missing remote MCP configuration.");
        var directory = Path.GetDirectoryName(path)!;
        return (options with {
            AttachmentPath = options.AttachmentPath.Length > 0 ? Path.GetFullPath(options.AttachmentPath, directory) : "",
            CertificatePath = options.CertificatePath is { } certificate ? Path.GetFullPath(certificate, directory) : null,
        }).Validate();
    }

    /// <summary>Reloads grants and a local capability path every second. Invalid configuration revokes grants; other changes require restart.</summary>
    /// <param name="app">The running resource server.</param>
    /// <param name="configurationPath">Its deployment document.</param>
    /// <param name="initial">The validated startup snapshot.</param>
    /// <param name="cancellationToken">Stops monitoring.</param>
    /// <returns>The monitor lifetime.</returns>
    public static async Task WatchConfigurationAsync(WebApplication app, string configurationPath, RemoteMcpOptions initial, CancellationToken cancellationToken) {
        var policy = app.Services.GetRequiredService<RemoteMcpAccessPolicy>();
        var host = app.Services.GetRequiredService<RemoteMcpHost>();
        var fingerprint = ConfigurationIdentity(initial);
        var failed = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
                try {
                    var updated = await ReadOptionsAsync(configurationPath, cancellationToken).ConfigureAwait(false);
                    if (!fingerprint.AsSpan().SequenceEqual(ConfigurationIdentity(updated))) { throw new InvalidDataException("MCP identity or host settings changed; restart is required."); }
                    if (host is LocalRemoteMcpHost local) { local.SetPath(updated.AttachmentPath); }
                    policy.Replace(updated.AllowedSubjects);
                    failed = false;
                } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException) {
                    policy.Replace([]);
                    if (!failed) { app.Logger.LogWarning("MCP configuration reload failed; all Operator grants revoked. Check configuration and restart for identity or endpoint changes."); }
                    failed = true;
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static byte[] ConfigurationIdentity(RemoteMcpOptions options) => JsonSerializer.SerializeToUtf8Bytes(
        options with { AllowedSubjects = [], AttachmentPath = "" }, OperatorMcpJson.Default.RemoteMcpOptions);
}
