using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Puck.Cli.Azure;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Exercises the actual Azure SDK lease adapter against an isolated local Azurite container.</summary>
public sealed class WorldReleaseAzureLeaseTests {
    private const string Image = "mcr.microsoft.com/azure-storage/azurite:3.35.0";

    private static Task<string> DockerAsync(string[] arguments, CancellationToken token) =>
        CliProcess.RunCheckedAsync(
            workingDirectory: Environment.CurrentDirectory,
            fileName: "docker",
            arguments: arguments,
            capture: true,
            cancellationToken: token
        );

    [Fact]
    [Trait("Category", "Docker")]
    public async Task BlobLeaseExcludesCompetitorsAndAStaleOwnerCannotReleaseItsSuccessor() {
        var token = TestContext.Current.CancellationToken;

        try {
            _ = await DockerAsync(
            arguments: ["image", "inspect", Image],
            token: token
        );
        } catch (Exception error) when ((error is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            Assert.Skip(reason: $"Load {Image} and start Docker to run the Azure lease integration law."); return;
        }
        var name = ("puck-release-lease-" + Guid.NewGuid().ToString(format: "N"));
        var key = Convert.ToBase64String(inArray: RandomNumberGenerator.GetBytes(count: 32));

        try {
            await DockerAsync(
                arguments: ["run", "--detach", "--rm", "--name", name, "--read-only", "--tmpfs", "/data:rw,nosuid,nodev,size=64m",
                "--cap-drop", "ALL", "--pids-limit", "128", "--memory", "512m", "-p", "127.0.0.1::10000", "-e", ("AZURITE_ACCOUNTS=releaseaccount:" + key),
                Image, "azurite-blob", "--blobHost", "0.0.0.0", "--location", "/data", "--silent", "--disableTelemetry", "--skipApiVersionCheck"],
                token: token
            );
            var address = (await DockerAsync(
                arguments: ["port", name, "10000/tcp"],
                token: token
            )).Trim();
            var options = new BlobClientOptions();

            // No retry hides a failed lease call, and no network deadline decides one: the test's token bounds each call.
            options.Retry.MaxRetries = 0; options.Retry.NetworkTimeout = Timeout.InfiniteTimeSpan;
            var service = new BlobServiceClient(
                connectionString: $"DefaultEndpointsProtocol=http;AccountName=releaseaccount;AccountKey={key};BlobEndpoint=http://{address}/releaseaccount;",
                options: options
            );
            var container = service.GetBlobContainerClient(blobContainerName: "release-tests");

            // Azurite publishes no readiness signal: the first request it answers is the signal, and until then a refused
            // connection (0) or a starting service (503) is retried at a fixed pace for as long as the test runs.
            while (true) {
                try { await container.CreateIfNotExistsAsync(cancellationToken: token); break; } catch (RequestFailedException error) when ((error.Status is 0 or 503)) {
                    await Task.Delay(
                    cancellationToken: token,
                    millisecondsDelay: 100
                );
                }
            }
            await container.DeleteAsync(cancellationToken: token);
            var blob = container.GetBlobClient(blobName: "group.controller");
            var stale = await WorldReleaseControllerLease.AcquireAsync(
                new AzureCommand.AzureWorldReleaseLeaseBackend(blob: blob),
                token
            );

            try {
                await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => WorldReleaseControllerLease.AcquireAsync(
                    new AzureCommand.AzureWorldReleaseLeaseBackend(blob: blob),
                    token
                ));
                await stale.EnsureHeldAsync();
                await blob.GetBlobLeaseClient().BreakAsync(
                    TimeSpan.Zero,
                    cancellationToken: token
                );
                await using var successor = await WorldReleaseControllerLease.AcquireAsync(
                    new AzureCommand.AzureWorldReleaseLeaseBackend(blob: blob),
                    token
                );

                await Assert.ThrowsAsync<RequestFailedException>(testCode: () => stale.EnsureHeldAsync());
                Assert.True(condition: stale.Token.IsCancellationRequested);
                await stale.DisposeAsync();
                Assert.NotNull(@object: stale.ReleaseFailure);
                await successor.EnsureHeldAsync();
            } finally { await stale.DisposeAsync(); }
        } finally {
            // Removal runs even when the test was cancelled, or the detached container would outlive the run.
            await DockerAsync(
                arguments: ["rm", "--force", name],
                token: CancellationToken.None
            );
        }
    }
}
