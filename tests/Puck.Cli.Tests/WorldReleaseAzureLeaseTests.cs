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
            Environment.CurrentDirectory,
            "docker",
            arguments,
            capture: true,
            cancellationToken: token
        );

    [Fact]
    [Trait("Category", "Docker")]
    public async Task BlobLeaseExcludesCompetitorsAndAStaleOwnerCannotReleaseItsSuccessor() {
        var token = TestContext.Current.CancellationToken;

        try { _ = await DockerAsync(
            arguments: ["image", "inspect", Image],
            token: token
        ); } catch (Exception error) when ((error is InvalidOperationException or System.ComponentModel.Win32Exception)) {
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

            options.Retry.MaxRetries = 0; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(seconds: 2);
            var service = new BlobServiceClient(
                connectionString: $"DefaultEndpointsProtocol=http;AccountName=releaseaccount;AccountKey={key};BlobEndpoint=http://{address}/releaseaccount;",
                options: options
            );
            var container = service.GetBlobContainerClient(blobContainerName: "release-tests");
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(token: token);

            readiness.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 20));
            while (true) {
                try { await container.CreateIfNotExistsAsync(cancellationToken: readiness.Token); break; } catch (RequestFailedException error) when ((error.Status is 0 or 503)) { await Task.Delay(
                    100,
                    readiness.Token
                ); }
            }
            await container.DeleteAsync(cancellationToken: token);
            var blob = container.GetBlobClient(blobName: "group.controller");
            var stale = await WorldReleaseControllerLease.AcquireAsync(
                new AzureCommand.AzureWorldReleaseLeaseBackend(blob),
                token
            );

            try {
                await Assert.ThrowsAsync<InvalidOperationException>(() => WorldReleaseControllerLease.AcquireAsync(
                    new AzureCommand.AzureWorldReleaseLeaseBackend(blob),
                    token
                ));
                await stale.EnsureHeldAsync();
                await blob.GetBlobLeaseClient().BreakAsync(
                    TimeSpan.Zero,
                    cancellationToken: token
                );
                await using var successor = await WorldReleaseControllerLease.AcquireAsync(
                    new AzureCommand.AzureWorldReleaseLeaseBackend(blob),
                    token
                );

                await Assert.ThrowsAsync<RequestFailedException>(testCode: () => stale.EnsureHeldAsync());
                Assert.True(stale.Token.IsCancellationRequested);
                await stale.DisposeAsync();
                Assert.NotNull(stale.ReleaseFailure);
                await successor.EnsureHeldAsync();
            } finally { await stale.DisposeAsync(); }
        } finally {
            using var cleanup = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 20));

            await DockerAsync(
                arguments: ["rm", "--force", name],
                token: cleanup.Token
            );
        }
    }
}
