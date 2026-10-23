using System.Net;
using Puck.Assets;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

public sealed class HttpReleaseSourceTests {
    private sealed class UnseekableBody(byte[] content) : MemoryStream(content) {
        public override bool CanSeek => false;
        public long BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            var read = Read(buffer: buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(result: read);
        }
    }

    private sealed class ResponseHandler(HttpContent content) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(result: new HttpResponseMessage(statusCode: HttpStatusCode.OK) { Content = content });
    }

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(4, 3, false)]
    [InlineData(4, 5, false)]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    public async Task FileDownloadWithoutContentLength_EnforcesTheSignedLength(int length, int actualLength, bool accepted) {
        var bytes = new byte[actualLength];
        using var body = new UnseekableBody(content: bytes);
        using var client = new HttpClient(handler: new ResponseHandler(content: new StreamContent(content: body)));
        var source = new HttpReleaseSource(httpClient: client, baseUri: new Uri(uriString: "https://unused.invalid/releases/"));
        using var destination = new MemoryStream();

        Assert.Equal(expected: accepted, actual: await source.TryGetFileAsync(pin: ContentPin.Compute(content: bytes),
            destination: destination, length: length, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(condition: destination.Length <= length);
        Assert.True(condition: body.BytesRead <= (long)length + 1);
    }

    [Fact]
    public async Task ManifestWithoutContentLength_IsBoundedBeforeBufferingTheBody() {
        using var body = new UnseekableBody(content: new byte[2 * 1024 * 1024]);
        using var client = new HttpClient(handler: new ResponseHandler(content: new StreamContent(content: body)));
        var source = new HttpReleaseSource(httpClient: client, baseUri: new Uri(uriString: "https://unused.invalid/releases/"));

        var result = await source.TryGetLatestManifestAsync(channel: "stable", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: result.Found);
        Assert.Empty(collection: result.ManifestBytes);
        Assert.Contains(expectedSubstring: "byte limit", actualString: result.RefusalReason!);
        Assert.True(condition: body.BytesRead <= 1024 * 1024 + 1);
    }
}
