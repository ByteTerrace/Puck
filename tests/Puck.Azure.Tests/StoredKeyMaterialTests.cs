using System.Net;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Puck.Azure.Functions.HttpTriggers;
using Xunit;

namespace Puck.Azure.Tests;

public sealed class StoredKeyMaterialTests {
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(1024 * 1024)]
    public async Task KeyMaterialDownloadsReadOnlyOneBytePastTheirLimit(int size) {
        using var content = new CountedContent(new byte[size]);
        using var handler = new BlobHandler(content);
        using var http = new HttpClient(handler);
        var blob = new BlobClient(new Uri("https://storage.invalid/account/key"),
            new BlobClientOptions { Transport = new HttpClientTransport(http) });

        if (size > 8) {
            await Assert.ThrowsAsync<InvalidDataException>(() => AttestKeys.ReadKeyMaterialAsync(blob, 8, TestContext.Current.CancellationToken));
        } else {
            var result = await AttestKeys.ReadKeyMaterialAsync(blob, 8, TestContext.Current.CancellationToken);

            Assert.Equal(8, result.Length);
        }
        Assert.Equal("bytes=0-8", handler.RequestedRange);
        Assert.Equal(Math.Min(size, 9), content.BytesRead);
    }

    private sealed class BlobHandler(Stream content) : HttpMessageHandler {
        public string? RequestedRange { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestedRange = request.Headers.GetValues("x-ms-range").Single();
            // Deliberately ignore the range, proving the stream cap still holds independently of storage metadata.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content) });
        }
    }

    private sealed class CountedContent(byte[] bytes) : Stream {
        private readonly MemoryStream m_content = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public int BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) {
            var read = m_content.Read(buffer, offset, count);

            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            var count = await m_content.ReadAsync(buffer, cancellationToken);

            BytesRead += count;
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            if (disposing) { m_content.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
