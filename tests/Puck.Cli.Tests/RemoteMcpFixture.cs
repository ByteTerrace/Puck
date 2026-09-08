using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using Puck.Hosting;
using Puck.Mcp;

namespace Puck.Cli.Tests;

internal sealed class RemoteMcpFixture : IAsyncDisposable {
    internal const string Issuer = "https://issuer.example.test/tenant/v2.0";
    internal const string Audience = "https://mcp.example.test/mcp";
    internal const string Tenant = "c75f3c9e-c844-4023-bfdf-81d996d67eb9";
    internal readonly Channel<string> Entered = Channel.CreateUnbounded<string>();
    internal readonly ManualClock Clock = new();
    private readonly RSA m_key = RSA.Create(2048);
    private readonly LocalControlServer m_host;
    internal WebApplication App { get; private set; } = null!;
    internal RemoteMcpOptions Options { get; }
    internal int Active;
    internal int Opened;
    internal int MetadataReads;
    internal int KeyReads;
    private bool m_appDisposed;
    private X509Certificate2? m_certificate;
    private string? m_certificatePath;

    internal RemoteMcpFixture() {
        m_host = new(() => { Interlocked.Increment(ref Opened); Interlocked.Increment(ref Active); return new ProbeSession(this); });
        Options = new() { AttachmentPath = m_host.AttachmentPath, PublicUrl = Audience, ListenUrl = "http://127.0.0.1:0", Issuer = Issuer, Audience = Audience, Scope = "puck.operator", AllowedSubjects = ["alice", "bob"], TenantId = Tenant, IdleTimeoutSeconds = 10 };
    }
    internal async Task StartAsync(CancellationToken token, bool tls = false, bool entra = false, Action<WebApplicationBuilder>? configure = null, bool proxy = false, bool embedded = false) {
        var options = Options;
        if (tls) {
            var request = new CertificateRequest("CN=127.0.0.1", m_key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            m_certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            m_certificatePath = Path.GetTempFileName();
            await File.WriteAllBytesAsync(m_certificatePath, m_certificate.Export(X509ContentType.Pfx), token);
            options = options with { ListenUrl = "https://127.0.0.1:0", CertificatePath = m_certificatePath };
        }
        if (entra) { options = options with { SubjectClaim = "oid", AuthorizationScope = "api://test-api/puck.operator" }; }
        if (proxy) { options = options with { TrustedProxy = new() { Issuer = Issuer, Audience = Audience, SubjectClaim = "sub", Subject = "front-door", TenantId = Tenant } }; }
        void Configure(WebApplicationBuilder builder) {
            builder.Logging.ClearProviders();
            configure?.Invoke(builder);
            builder.Services.AddSingleton<TimeProvider>(Clock);
            builder.Services.Configure<JwtBearerOptions>(RemoteMcpServer.AuthenticationScheme, jwt => jwt.BackchannelHttpHandler = new IssuerHandler(this));
            if (proxy) { builder.Services.Configure<JwtBearerOptions>(RemoteMcpServer.ProxyAuthenticationScheme, jwt => jwt.BackchannelHttpHandler = new IssuerHandler(this)); }
        }
        if (embedded) {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            Configure(builder);
            RemoteMcpServer.AddServices(builder.Services, options with { ListenUrl = null });
            App = builder.Build();
            App.Run(RemoteMcpServer.CreateRequestDelegate(App.Services));
        } else { App = RemoteMcpServer.Build(options, Configure); }
        await App.StartAsync(token);
    }
    internal HttpClient Http(string? token = null) {
        var handler = new HttpClientHandler();
        if (m_certificate is not null) { handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == m_certificate.GetCertHashString(); }
        var http = new HttpClient(handler) { BaseAddress = new Uri(App.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Host = "mcp.example.test";
        if (token is not null) { http.DefaultRequestHeaders.Authorization = new("Bearer", token); }
        return http;
    }
    internal async Task<McpClient> ClientAsync(HttpClient http, string revision, CancellationToken token) => await McpClient.CreateAsync(
        new HttpClientTransport(new() { Endpoint = new(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http),
        new() { ProtocolVersion = revision }, cancellationToken: token);

    internal string Token(string subject = "alice", string? failure = null, int lifetimeSeconds = 300, string? audience = null) {
        using var wrongKey = failure == "signature" ? RSA.Create(2048) : null;
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["scope"] = "puck.operator", ["tid"] = Tenant };
        if (failure == "entra") { claims.Remove("scope"); claims["scp"] = "other puck.operator"; claims["oid"] = subject; claims["sub"] = "pairwise-client-subject"; }
        if (failure is "scope" or "app-only") { claims.Remove("scope"); claims["roles"] = new[] { "puck.operator" }; }
        if (failure == "tenant") { claims["tid"] = "ddc729ed-3bb7-4e22-8b1b-f47d23a9d6ad"; }
        if (failure == "duplicate-subject") { claims["sub"] = new[] { "alice", "bob" }; }
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
            Issuer = failure == "issuer" ? "https://wrong.example.test" : Issuer,
            Audience = audience ?? (failure == "audience" ? "https://management.azure.com/" : Audience),
            IssuedAt = DateTime.UtcNow.AddMinutes(-2),
            NotBefore = failure == "future" ? DateTime.UtcNow.AddMinutes(1) : DateTime.UtcNow.AddMinutes(-2),
            Expires = failure == "expired" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddSeconds(lifetimeSeconds),
            Claims = claims,
            SigningCredentials = new(new RsaSecurityKey(wrongKey ?? m_key) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256),
        });
    }
    public async ValueTask DisposeAsync() {
        await StopGatewayAsync(CancellationToken.None);
        m_host.Dispose(); m_key.Dispose();
        m_certificate?.Dispose();
        if (m_certificatePath is not null) { File.Delete(m_certificatePath); }
    }
    internal async Task StopGatewayAsync(CancellationToken token) {
        if (App is not null && !m_appDisposed) { m_appDisposed = true; await App.StopAsync(token); await App.DisposeAsync(); }
    }

    private sealed class IssuerHandler(RemoteMcpFixture owner) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            object document;
            if (request.RequestUri!.AbsoluteUri == Issuer + "/.well-known/openid-configuration") {
                Interlocked.Increment(ref owner.MetadataReads);
                document = new { issuer = Issuer, jwks_uri = Issuer + "/keys", authorization_endpoint = Issuer + "/authorize", token_endpoint = Issuer + "/token" };
            } else if (request.RequestUri.AbsoluteUri == Issuer + "/keys") {
                Interlocked.Increment(ref owner.KeyReads);
                var key = owner.m_key.ExportParameters(false);
                document = new { keys = new[] { new { kty = "RSA", kid = "test-key", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } };
            } else { throw new InvalidOperationException("Unexpected OIDC backchannel URL: " + request.RequestUri); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(document), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class ProbeSession(RemoteMcpFixture owner) : IControlSession {
        private string m_value = "fresh";
        private int m_disposed;
        public async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
            if (request.Command == "wait") { owner.Entered.Writer.TryWrite("wait"); await Task.Delay(Timeout.Infinite, cancellationToken); }
            if (request.Command?.StartsWith("set ", StringComparison.Ordinal) == true) { m_value = request.Command[4..]; }
            if (request.Operation == "capture") {
                return new(request.Id, "completed", "frame", Png: Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j5p8AAAAASUVORK5CYII="));
            }
            return new(request.Id, "completed", request.Command == "read" ? m_value : request.Command!);
        }
        public void Dispose() { if (Interlocked.Exchange(ref m_disposed, 1) == 0) { Interlocked.Decrement(ref owner.Active); } }
    }
    internal sealed class ManualClock : TimeProvider {
        private long m_offset;
        public override long GetTimestamp() => base.GetTimestamp() + Volatile.Read(ref m_offset);
        internal void Advance(TimeSpan time) => Interlocked.Add(ref m_offset, (long)(time.TotalSeconds * TimestampFrequency));
    }
}
