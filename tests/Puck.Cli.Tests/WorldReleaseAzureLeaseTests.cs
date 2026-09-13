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

    [Fact]
    [Trait("Category", "Docker")]
    public async Task BlobLeaseExcludesCompetitorsAndAStaleOwnerCannotReleaseItsSuccessor() {
        var token = TestContext.Current.CancellationToken;
        try { _ = await DockerAsync(["image", "inspect", Image], token); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) {
            Assert.Skip($"Load {Image} and start Docker to run the Azure lease integration law."); return;
        }
        var name = "puck-release-lease-" + Guid.NewGuid().ToString("N");
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        try {
            await DockerAsync(["run", "--detach", "--rm", "--name", name, "--read-only", "--tmpfs", "/data:rw,nosuid,nodev,size=64m",
                "--cap-drop", "ALL", "--pids-limit", "128", "--memory", "512m", "-p", "127.0.0.1::10000", "-e", "AZURITE_ACCOUNTS=releaseaccount:" + key,
                Image, "azurite-blob", "--blobHost", "0.0.0.0", "--location", "/data", "--silent", "--disableTelemetry", "--skipApiVersionCheck"], token);
            var address = (await DockerAsync(["port", name, "10000/tcp"], token)).Trim();
            var options = new BlobClientOptions();
            options.Retry.MaxRetries = 0; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(2);
            var service = new BlobServiceClient($"DefaultEndpointsProtocol=http;AccountName=releaseaccount;AccountKey={key};BlobEndpoint=http://{address}/releaseaccount;", options);
            var container = service.GetBlobContainerClient("release-tests");
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(token);
            readiness.CancelAfter(TimeSpan.FromSeconds(20));
            while (true) {
                try { await container.CreateIfNotExistsAsync(cancellationToken: readiness.Token); break; }
                catch (RequestFailedException error) when (error.Status is 0 or 503) { await Task.Delay(100, readiness.Token); }
            }
            await container.DeleteAsync(cancellationToken: token);
            var blob = container.GetBlobClient("group.controller");
            var stale = await WorldReleaseControllerLease.AcquireAsync(new AzureCommand.AzureWorldReleaseLeaseBackend(blob), token);
            try {
                await Assert.ThrowsAsync<InvalidOperationException>(() => WorldReleaseControllerLease.AcquireAsync(new AzureCommand.AzureWorldReleaseLeaseBackend(blob), token));
                await stale.EnsureHeldAsync();
                await blob.GetBlobLeaseClient().BreakAsync(TimeSpan.Zero, cancellationToken: token);
                await using var successor = await WorldReleaseControllerLease.AcquireAsync(new AzureCommand.AzureWorldReleaseLeaseBackend(blob), token);
                await Assert.ThrowsAsync<RequestFailedException>(() => stale.EnsureHeldAsync());
                Assert.True(stale.Token.IsCancellationRequested);
                await stale.DisposeAsync();
                Assert.NotNull(stale.ReleaseFailure);
                await successor.EnsureHeldAsync();
            } finally { await stale.DisposeAsync(); }
        } finally {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await DockerAsync(["rm", "--force", name], cleanup.Token);
        }
    }

    private static Task<string> DockerAsync(string[] arguments, CancellationToken token) =>
        CliProcess.RunCheckedAsync(Environment.CurrentDirectory, "docker", arguments, capture: true, cancellationToken: token);
}
