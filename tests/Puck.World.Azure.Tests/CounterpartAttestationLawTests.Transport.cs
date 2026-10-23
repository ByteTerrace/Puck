using System.Net;
using Puck.Attestation;
using Puck.Testing;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed partial class CounterpartAttestationLawTests {
    [Theory]
    [InlineData(-1, HttpStatusCode.OK)]
    [InlineData(1, HttpStatusCode.OK)]
    [InlineData(524288, HttpStatusCode.OK)]
    [InlineData(-1, HttpStatusCode.NotFound)]
    [InlineData(-1, HttpStatusCode.InternalServerError)]
    public void CounterpartBody_IsBoundedBeforeBufferingOrEnvelopeDecode(long declaredLength, HttpStatusCode status) {
        using var body = new CounterpartBody(content: new byte[2 * WorldApiCounterpartResolver.MaximumEnvelopeBytes]);
        using var content = new StreamContent(content: body);
        if (declaredLength >= 0) { content.Headers.ContentLength = declaredLength; }
        using var client = new HttpClient(handler: new CounterpartResponse(content: content, status: status)) {
            BaseAddress = new Uri(uriString: "https://unused.invalid/"),
        };
        var resolver = new WorldApiCounterpartResolver(httpClient: client, admissionEntries: [], credential: new FakeTokenCredential());

        var result = resolver.Resolve(document: $"owner/{Guid.NewGuid():D}/world");

        Assert.Equal(expected: WorldNeighbourResolutionKind.Unavailable, actual: result.Kind);
        Assert.True(condition: body.BytesRead <= WorldApiCounterpartResolver.MaximumEnvelopeBytes + 1,
            userMessage: $"read {body.BytesRead} bytes before refusal");
        if ((status != HttpStatusCode.OK) || (declaredLength > WorldApiCounterpartResolver.MaximumEnvelopeBytes)) {
            Assert.Equal(expected: 0, actual: body.BytesRead);
        }
        if (status == HttpStatusCode.OK) { Assert.Contains(expectedSubstring: "byte limit", actualString: result.Reason); }
    }

    [Fact]
    public void CounterpartBody_ValidSignedEnvelopeWithoutContentLengthVerifies() {
        var owner = Guid.NewGuid();
        var oracle = new FakeOracle();
        var (_, _, _, corner) = OwnerCorner(owner: owner, world: "corner");
        Assert.True(condition: WorldCounterpartAttestation.TryCompose(attestation: out var attestation, definition: corner,
            document: $"owner/{owner:D}/corner", reason: out var reason), userMessage: reason);
        var wrapper = oracle.SignCounterpartClaim(attestation: attestation!, subject: owner.ToString(format: "D"));
        using var body = new CounterpartBody(content: wrapper);
        using var client = new HttpClient(handler: new CounterpartResponse(content: new StreamContent(content: body), status: HttpStatusCode.OK)) {
            BaseAddress = new Uri(uriString: "https://unused.invalid/"),
        };
        var resolver = new WorldApiCounterpartResolver(httpClient: client, admissionEntries: [oracle.VouchingAdmissionEntry], credential: new FakeTokenCredential());

        var result = resolver.Resolve(document: $"owner/{owner:D}/corner");

        Assert.Equal(expected: WorldNeighbourResolutionKind.VerifiedAttested, actual: result.Kind);
        Assert.Equal(expected: wrapper.Length, actual: body.BytesRead);
    }

    [Fact]
    public void CounterpartBody_CeilingAccommodatesThreeMaximumSizeAttestations() {
        var entry = new byte[AttestationResourceLimits.AttestationBytes];
        var wrapper = AttestationChainEnvelope.Encode(claim: entry, chain: [entry, entry]);
        using var body = new CounterpartBody(content: wrapper);
        using var client = new HttpClient(handler: new CounterpartResponse(content: new StreamContent(content: body), status: HttpStatusCode.OK)) {
            BaseAddress = new Uri(uriString: "https://unused.invalid/"),
        };
        var resolver = new WorldApiCounterpartResolver(httpClient: client, admissionEntries: [], credential: new FakeTokenCredential());

        var result = resolver.Resolve(document: $"owner/{Guid.NewGuid():D}/world");

        // The outer transport accepts this size; the zero-filled individual attestations then fail decoding.
        Assert.Equal(expected: wrapper.Length, actual: body.BytesRead);
        Assert.Contains(expectedSubstring: "claim/chain does not decode", actualString: result.Reason);
    }

    [Fact]
    public async Task CounterpartBody_StalledReadKeepsTheOperationDeadline() {
        var clock = new VirtualClock();
        using var body = new CounterpartBody(content: [], stall: true);
        using var client = new HttpClient(handler: new CounterpartResponse(content: new StreamContent(content: body), status: HttpStatusCode.OK)) {
            BaseAddress = new Uri(uriString: "https://unused.invalid/"), Timeout = Timeout.InfiniteTimeSpan,
        };
        var resolver = new WorldApiCounterpartResolver(httpClient: client, admissionEntries: [], credential: new FakeTokenCredential(), timeProvider: clock);
        var resolve = Task.Run(function: () => resolver.Resolve(document: $"owner/{Guid.NewGuid():D}/world"), cancellationToken: TestContext.Current.CancellationToken);
        await body.ReadStarted.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        await clock.ExpireAsync(dueTime: TimeSpan.FromSeconds(seconds: 15), pending: resolve, ct: TestContext.Current.CancellationToken);
        var result = await resolve;

        Assert.Equal(expected: WorldNeighbourResolutionKind.Unavailable, actual: result.Kind);
        Assert.Contains(expectedSubstring: "timed out", actualString: result.Reason);
        Assert.Equal(expected: 0, actual: body.BytesRead);
    }

    private sealed class CounterpartResponse(HttpContent content, HttpStatusCode status) : HttpMessageHandler {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(statusCode: status) { Content = content };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(result: Send(request: request, cancellationToken: cancellationToken));
    }

    private sealed class CounterpartBody(byte[] content, bool stall = false) : Stream {
        public int BytesRead { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer: buffer.AsSpan(start: offset, length: count));
        public override int Read(Span<byte> buffer) {
            if (stall) { throw new IOException(message: "the stalled body needs a cancellable asynchronous read"); }
            var count = Math.Min(val1: buffer.Length, val2: content.Length - BytesRead);
            content.AsSpan(start: BytesRead, length: count).CopyTo(destination: buffer);
            BytesRead += count;
            return count;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            ReadStarted.TrySetResult();
            if (stall) { await Task.Delay(delay: Timeout.InfiniteTimeSpan, cancellationToken: cancellationToken); }
            return Read(buffer: buffer.Span);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
