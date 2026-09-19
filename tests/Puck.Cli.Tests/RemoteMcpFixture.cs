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
using Puck.Commands;
using Puck.Hosting;
using Puck.Mcp;
using Puck.Networking;
using Puck.State;
using Puck.World;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.Cli.Tests;

internal sealed class RemoteMcpFixture : IAsyncDisposable {
    internal const string Audience = "https://mcp.example.test/mcp";
    internal const string Issuer = "https://issuer.example.test/tenant/v2.0";
    internal const string Tenant = "c75f3c9e-c844-4023-bfdf-81d996d67eb9";

    internal int Active;
    internal string CommandHelp = "read; set <value>; wait";
    internal int KeyReads;
    internal int MetadataReads;
    internal int Opened;
    internal bool UseRealWorldSession;
    internal WorldServer? RealWorldServer;

    private bool m_appDisposed;
    private X509Certificate2? m_certificate;
    private string? m_certificatePath;
    private RealWorldHost? m_realWorldHost;

    internal readonly Channel<string> Entered = Channel.CreateUnbounded<string>();
    internal readonly ManualClock Clock = new();

    private readonly RSA m_key = RSA.Create(keySizeInBits: 2048);

    internal WebApplication App { get; private set; } = null!;

    internal RemoteMcpOptions Options { get; }

    internal RemoteMcpFixture() {
        Options = new() { AllowedSubjects = ["alice", "bob"], Audience = Audience, IdleTimeoutSeconds = 10, Issuer = Issuer, ListenUrl = "http://127.0.0.1:0", PublicUrl = Audience, Scope = "user_impersonation", Target = "row", TenantId = Tenant };
    }

    internal async Task<McpClient> ClientAsync(HttpClient http, string revision, CancellationToken token) => await McpClient.CreateAsync(
        new HttpClientTransport(
            new() { Endpoint = new(
                baseUri: http.BaseAddress!,
                relativeUri: "/mcp"
            ), TransportMode = HttpTransportMode.StreamableHttp },
            http
        ),
        new() { ProtocolVersion = revision },
        cancellationToken: token
    );
    internal HttpClient Http(string? token = null) {
        var handler = new HttpClientHandler();

        if (m_certificate is not null) { handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => (certificate?.GetCertHashString() == m_certificate.GetCertHashString()); }
        var http = new HttpClient(handler: handler) { BaseAddress = new Uri(uriString: App.Urls.Single()), Timeout = TimeSpan.FromSeconds(seconds: 15) };

        http.DefaultRequestHeaders.Host = "mcp.example.test";
        if (token is not null) { http.DefaultRequestHeaders.Authorization = new(
            parameter: token,
            scheme: "Bearer"
        ); }
        return http;
    }
    internal async Task StartAsync(CancellationToken token, bool tls = false, bool entra = false, Action<WebApplicationBuilder>? configure = null, bool proxy = false, bool embedded = false) {
        var options = Options;

        if (tls) {
            var request = new CertificateRequest(
                "CN=127.0.0.1",
                m_key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(ipAddress: IPAddress.Loopback);
            request.CertificateExtensions.Add(item: names.Build());
            m_certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(minutes: -1),
                DateTimeOffset.UtcNow.AddHours(hours: 1)
            );
            m_certificatePath = Path.GetTempFileName();
            await File.WriteAllBytesAsync(
                m_certificatePath,
                m_certificate.Export(contentType: X509ContentType.Pfx),
                token
            );
            options = options with { ListenUrl = "https://127.0.0.1:0", CertificatePath = m_certificatePath };
        }
        if (entra) { options = options with { SubjectClaim = "oid", AuthorizationScope = "api://test-api/user_impersonation" }; }
        if (proxy) { options = options with { TrustedProxy = new() { Audience = Audience, Issuer = Issuer, Subject = "front-door", SubjectClaim = "sub", TenantId = Tenant } }; }
        void Configure(WebApplicationBuilder builder) {
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<RemoteMcpHost>(implementationInstance: (UseRealWorldSession
                ? (m_realWorldHost ??= new RealWorldHost(owner: this))
                : new ProbeHost(owner: this)));
            configure?.Invoke(builder);
            builder.Services.AddSingleton<TimeProvider>(implementationInstance: Clock);
            builder.Services.Configure<JwtBearerOptions>(
                RemoteMcpServer.AuthenticationScheme,
                jwt => jwt.BackchannelHttpHandler = new IssuerHandler(owner: this)
            );
            if (proxy) { builder.Services.Configure<JwtBearerOptions>(
                RemoteMcpServer.ProxyAuthenticationScheme,
                jwt => jwt.BackchannelHttpHandler = new IssuerHandler(owner: this)
            ); }
        }
        if (embedded) {
            var builder = WebApplication.CreateSlimBuilder();

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            Configure(builder: builder);
            RemoteMcpServer.AddServices(
                builder.Services,
                options with { ListenUrl = null }
            );
            App = builder.Build();
            App.Run(handler: RemoteMcpServer.CreateRequestDelegate(services: App.Services));
        } else { App = RemoteMcpServer.Build(
            configureBuilder: Configure,
            options: options
        ); }
        await App.StartAsync(cancellationToken: token);
    }
    internal async Task StopGatewayAsync(CancellationToken token) {
        if (
            (App is not null) &&
            !m_appDisposed
        ) { m_appDisposed = true; await App.StopAsync(cancellationToken: token); await App.DisposeAsync(); }
    }
    internal string Token(string subject = "alice", string? failure = null, int lifetimeSeconds = 300, string? audience = null) {
        using var wrongKey = ((failure == "signature")
            ? RSA.Create(keySizeInBits: 2048)
            : null
        );
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["scope"] = "user_impersonation", ["tid"] = Tenant };

        if (failure == "entra") { claims.Remove(key: "scope"); claims["scp"] = "other user_impersonation"; claims["oid"] = subject; claims["sub"] = "pairwise-client-subject"; }
        if (failure is "scope" or "app-only") { claims.Remove(key: "scope"); claims["roles"] = new[] { "user_impersonation" }; }
        if (failure == "tenant") { claims["tid"] = "ddc729ed-3bb7-4e22-8b1b-f47d23a9d6ad"; }
        if (failure == "duplicate-subject") { claims["sub"] = new[] { "alice", "bob" }; }
        return new JsonWebTokenHandler().CreateToken(tokenDescriptor: new SecurityTokenDescriptor {
            Issuer = ((failure == "issuer")
            ? "https://wrong.example.test"
            : Issuer),
            Audience = (audience ?? ((failure == "audience")
            ? "https://management.azure.com/"
            : Audience)),
            IssuedAt = DateTime.UtcNow.AddMinutes(value: -2),
            NotBefore = ((failure == "future")
            ? DateTime.UtcNow.AddMinutes(value: 1)
            : DateTime.UtcNow.AddMinutes(value: -2)),
            Expires = ((failure == "expired")
            ? DateTime.UtcNow.AddMinutes(value: -1)
            : DateTime.UtcNow.AddSeconds(value: lifetimeSeconds)),
            Claims = claims,
            SigningCredentials = new(
            new RsaSecurityKey(rsa: (wrongKey ?? m_key)) { KeyId = "test-key" },
            SecurityAlgorithms.RsaSha256
        ),
        });
    }

    public async ValueTask DisposeAsync() {
        await StopGatewayAsync(token: CancellationToken.None);
        m_realWorldHost?.Dispose();
        m_key.Dispose();
        m_certificate?.Dispose();
        if (m_certificatePath is not null) { File.Delete(path: m_certificatePath); }
    }

    private sealed class IssuerHandler(RemoteMcpFixture owner) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            object document;

            if (request.RequestUri!.AbsoluteUri == (Issuer + "/.well-known/openid-configuration")) {
                Interlocked.Increment(location: ref owner.MetadataReads);
                document = new { issuer = Issuer, jwks_uri = (Issuer + "/keys"), authorization_endpoint = (Issuer + "/authorize"), token_endpoint = (Issuer + "/token") };
            } else if (request.RequestUri.AbsoluteUri == (Issuer + "/keys")) {
                Interlocked.Increment(location: ref owner.KeyReads);
                var key = owner.m_key.ExportParameters(includePrivateParameters: false);

                document = new { keys = new[] { new { kty = "RSA", kid = "test-key", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(inArray: key.Modulus!), e = Base64UrlEncoder.Encode(inArray: key.Exponent!) } } };
            } else { throw new InvalidOperationException(message: ("Unexpected OIDC backchannel URL: " + request.RequestUri)); }
            return Task.FromResult(result: new HttpResponseMessage(statusCode: HttpStatusCode.OK) { Content = new StringContent(
                content: JsonSerializer.Serialize(document),
                encoding: Encoding.UTF8,
                mediaType: "application/json"
            ) });
        }
    }
    private sealed class ProbeSession(RemoteMcpFixture owner) : IControlSession {
        private int m_disposed;
        private string m_value = "fresh";

        public void Dispose() { if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) == 0) { Interlocked.Decrement(location: ref owner.Active); } }
        public async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
            if (request.Command == "wait") { owner.Entered.Writer.TryWrite(item: "wait"); await Task.Delay(
                cancellationToken: cancellationToken,
                millisecondsDelay: Timeout.Infinite
            ); }
            if (request.Command?.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "set "
            ) == true) { m_value = request.Command[4..]; }
            if (request.Operation == "capture") {
                return new(
                    request.Id,
                    "completed",
                    "frame",
                    Png: Convert.FromBase64String(s: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j5p8AAAAASUVORK5CYII=")
                );
            }
            return new(
                request.Id,
                "completed",
                ((request.Command == "read")
                ? m_value
                : request.Command!)
            );
        }
    }
    private sealed class ProbeHost(RemoteMcpFixture owner) : RemoteMcpHost {
        public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) {
            Interlocked.Increment(location: ref owner.Opened);
            Interlocked.Increment(location: ref owner.Active);
            return ValueTask.FromResult<IControlSession>(new ProbeSession(owner: owner));
        }
        public override ValueTask<ControlCapabilities> DescribeControlAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => ValueTask.FromResult(new ControlCapabilities(
            CommandHelp: owner.CommandHelp,
            SupportsCapture: true
        ));

        public override bool IsReady => true;
    }
    // Runs `puck_state_vector_write` against a real WorldServer's `world.state.cell.set` handler — the granted
    // and ungranted principals it maps callers to carry real WorldGrant rows, so a write's ultimate fate is
    // decided by WorldServer's own tick-boundary authorization, not by a session stub.
    private sealed class RealWorldHost : RemoteMcpHost, IDisposable {
        private const string EmbeddingRowName = "embedding";
        private const string SlotEmbeddingRowName = "slot_embedding";

        private readonly RemoteMcpFixture m_owner;
        private readonly WorldInstance m_instance;
        private readonly CancellationTokenSource m_pumpLifetime = new();
        private readonly Task m_pump;
        private readonly TextCommandSource m_source;
        private readonly string m_stateDirectory;

        internal RealWorldHost(RemoteMcpFixture owner) {
            m_owner = owner;

            var space = new StateSpace(Dimensions: 8, Model: "test-model", Name: CellName.Parse(candidate: "lore"), Revision: "1");
            var definition = new WorldDefinition(StateRaw: new WorldStateSection(
                Spaces: [space],
                World: [
                    new WorldStateRow(Capacity: 8, Kind: CellKind.Vector, Name: CellName.Parse(candidate: EmbeddingRowName), Space: "lore"),
                    new WorldStateRow(Kind: CellKind.Vector, Name: CellName.Parse(candidate: SlotEmbeddingRowName), Space: "lore"),
                ]
            ));
            var population = new WorldPopulation(definition: definition);

            m_stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-remote-mcp-tests-").FullName;

            var profiles = new WorldOwnedWorlds(template: definition, directory: m_stateDirectory, machineId: Guid.NewGuid());
            var machines = new WorldMachineHost(screens: definition.Screens, engines: []);
            var server = new WorldServer(definition: definition, population: population, profiles: profiles, envelope: new WorldRenderEnvelope(), machines: machines);

            GrantEdit(server: server, principal: AlicePrincipal, rowName: EmbeddingRowName);
            GrantEdit(server: server, principal: AlicePrincipal, rowName: SlotEmbeddingRowName);

            var link = new LoopbackTransport(server: server);

            m_instance = new WorldInstance(
                name: "remote-mcp-tests",
                origin: static () => "remote-mcp-tests",
                server: server,
                ownedMachines: machines,
                link: link,
                federation: new WorldFederationIdentity(Authenticator: new NullAuthenticator(), Subject: server.AuthorityIdentity),
                documentOrigin: new WorldFileOrigin(resolvedPath: "remote-mcp-tests")
            );

            var registry = new CommandRegistry(modules: [new WorldStateCommandModule(
                authority: new FixedAuthority(instance: m_instance),
                echoes: new WorldDeferredVerbEchoes(),
                link: link
            )]);

            m_source = new TextCommandSource(registry: registry);
            owner.RealWorldServer = server;
            m_pump = PumpAsync(token: m_pumpLifetime.Token);
        }

        // Enqueue only queues a line; nothing drains it without a caller that owns the frame boundary. A real host
        // drains its text source on its own tick pump — this stands in for that pump so an attached session's
        // ExecuteAsync actually resolves.
        private async Task PumpAsync(CancellationToken token) {
            while (!token.IsCancellationRequested) {
                m_source.Collect();

                try {
                    await Task.Delay(delay: TimeSpan.FromMilliseconds(value: 5), cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                } catch (OperationCanceledException) {
                    return;
                }
            }
        }

        internal static WorldPrincipal AlicePrincipal { get; } = WorldPrincipal.Peer(generation: 1, index: 1);
        internal static WorldPrincipal BobPrincipal { get; } = WorldPrincipal.Peer(generation: 1, index: 2);

        private static void GrantEdit(WorldServer server, WorldPrincipal principal, string rowName) {
            if (!WorldMutationKindCatalog.TryParseMask(text: "UpsertStateCell", mask: out var kindMask, unknown: out _)) {
                throw new InvalidOperationException(message: "UpsertStateCell is not a recognized mutation kind.");
            }

            server.Grant(actor: WorldPrincipal.Console, grant: new WorldGrant(
                Budget: 16,
                Capability: WorldCapability.Mutate,
                Exclusive: false,
                KindMask: kindMask,
                Principal: principal,
                Subject: GrantSubject.Section(section: WorldSection.State)
            ));
            server.Grant(actor: WorldPrincipal.Console, grant: new WorldGrant(
                Capability: WorldCapability.Edit,
                Exclusive: false,
                Principal: principal,
                Subject: GrantSubject.State(name: rowName)
            ));
        }

        public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) {
            Interlocked.Increment(location: ref m_owner.Opened);
            Interlocked.Increment(location: ref m_owner.Active);

            var principal = WorldPrincipalMapping.ToCommand(principal: (string.Equals(a: caller.Subject, b: "alice", comparisonType: StringComparison.Ordinal)
                ? AlicePrincipal
                : BobPrincipal
            ));
            var session = new ConsoleControlSession(
                source: m_source,
                capture: static _ => throw new NotSupportedException(message: "Capture is not exercised by the real-world session."),
                principal: principal
            );

            return ValueTask.FromResult<IControlSession>(new TrackedSession(inner: session, owner: m_owner));
        }
        public override ValueTask<ControlCapabilities> DescribeControlAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => ValueTask.FromResult(new ControlCapabilities(
            CommandHelp: m_owner.CommandHelp,
            SupportsCapture: false
        ));

        public override bool IsReady => true;

        public void Dispose() {
            m_pumpLifetime.Cancel();
            m_pump.Wait();
            m_pumpLifetime.Dispose();
            m_instance.Dispose();

            try {
                Directory.Delete(path: m_stateDirectory, recursive: true);
            } catch (IOException) {
                // Best-effort scratch cleanup.
            }
        }
    }
    private sealed class TrackedSession(IControlSession inner, RemoteMcpFixture owner) : IControlSession {
        private int m_disposed;

        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) => inner.ExecuteAsync(
            cancellationToken: cancellationToken,
            request: request
        );
        public void Dispose() {
            if (Interlocked.Exchange(location1: ref m_disposed, value: 1) == 0) {
                inner.Dispose();
                Interlocked.Decrement(location: ref owner.Active);
            }
        }
    }
    private sealed class FixedAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }
    private sealed class NullAuthenticator : IAuthenticator {
        public int ChallengeBytes => 0;
        public bool IsConfigured => false;

        public byte[] NewChallenge() => [];
        public byte[] Prove(ReadOnlySpan<byte> challenge) => throw new NotSupportedException();
        public bool TryVerify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, out string? sourceAuthority) {
            sourceAuthority = null;

            return false;
        }
    }

    internal sealed class ManualClock : TimeProvider {
        private long m_offset;

        internal void Advance(TimeSpan time) => Interlocked.Add(
            location1: ref m_offset,
            value: ((long)(time.TotalSeconds * TimestampFrequency))
        );

        public override long GetTimestamp() => (base.GetTimestamp() + Volatile.Read(location: ref m_offset));
    }
}
