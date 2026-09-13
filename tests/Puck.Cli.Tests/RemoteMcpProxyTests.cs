using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Puck.Hosting;
using Puck.Mcp;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class RemoteMcpProxyTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [InlineData("missing-origin", 403)]
    [InlineData("wrong-origin", 403)]
    [InlineData("origin-audience", 403)]
    [InlineData("expired-origin", 403)]
    [InlineData("missing-caller", 401)]
    [InlineData("invalid-caller", 401)]
    [InlineData("duplicate-caller", 401)]
    [InlineData("ungranted-caller", 403)]
    [Theory]
    public async Task BothIdentitiesMustPassIndependently(string failure, int status) {
        await using var fixture = new RemoteMcpFixture();

        await fixture.StartAsync(
            Token,
            proxy: true,
            embedded: true
        );
        var origin = ((failure == "missing-origin")
            ? null
            : fixture.Token(
                ((failure == "wrong-origin")
                ? "mallory"
                : "front-door"),
                ((failure == "expired-origin")
                ? "expired"
                : "app-only"),
                audience: ((failure == "origin-audience")
                ? "wrong"
                : null)
            )
        );
        using var http = fixture.Http(token: origin);

        if (failure != "missing-caller") {
            http.DefaultRequestHeaders.Add(
                name: "ClientAuthorization",
                value: ("Bearer " + fixture.Token(
                    ((failure == "ungranted-caller")
                ? "mallory"
                : "alice"),
                    ((failure == "invalid-caller")
                ? "signature"
                : null)
                ))
            );
        }
        if (failure == "duplicate-caller") { http.DefaultRequestHeaders.Add(
            name: "ClientAuthorization",
            value: ("Bearer " + fixture.Token("bob"))
        ); }
        using var response = await http.PostAsync(
            "/mcp",
            new StringContent(
                content: """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json"
            ),
            Token
        );

        Assert.Equal(
            status,
            ((int)response.StatusCode)
        );
        Assert.Equal(
            actual: fixture.Opened,
            expected: 0
        );
    }
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public async Task DelegationUsesValidatedCallerInsteadOfOrigin(bool embedded, bool originHasScope) {
        await using var fixture = new RemoteMcpFixture();
        var host = new AssertionHost();

        await fixture.StartAsync(
            Token,
            proxy: true,
            embedded: embedded,
            configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(implementationInstance: host)
        );
        var caller = fixture.Token();
        // Standards-based issuers may include scope on client-credentials tokens. The configured
        // issuer/subject/audience identifies the proxy; Entra's claim shape is not a universal rule.
        using var http = fixture.Http(token: fixture.Token(
            "front-door",
            (originHasScope
            ? null
            : "app-only")
        ));

        http.DefaultRequestHeaders.Add(
            name: "ClientAuthorization",
            value: ("Bearer " + caller)
        );
        await using var client = await fixture.ClientAsync(
            http: http,
            revision: "2026-07-28",
            token: Token
        );

        Assert.Single(collection: await client.ListToolsAsync(cancellationToken: Token));
        var result = await client.CallToolAsync(
            "assertion_probe",
            cancellationToken: Token
        );

        Assert.NotEqual(
            true,
            result.IsError
        );
        Assert.Equal(
            actual: host.Assertion,
            expected: caller
        );
        Assert.Equal(
            actual: host.Subject,
            expected: "alice"
        );
        Assert.Equal(
            actual: fixture.Opened,
            expected: 0
        );
    }
    [Fact]
    public async Task EmbeddedHostKeepsItsAuthenticationDefaultsAndBodyLimit() {
        await using var fixture = new RemoteMcpFixture();

        await fixture.StartAsync(
            Token,
            embedded: true,
            configure: builder => {
            builder.Services.AddAuthentication().AddCookie(authenticationScheme: "host-auth");
            builder.Services.Configure<AuthenticationOptions>(configureOptions: options => {
                options.DefaultAuthenticateScheme = "host-auth";
                options.DefaultChallengeScheme = "host-challenge";
                options.DefaultForbidScheme = "host-forbid";
            });
        }
        );
        var options = fixture.App.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        Assert.Equal(
            "host-auth",
            options.DefaultAuthenticateScheme
        );
        Assert.Equal(
            "host-challenge",
            options.DefaultChallengeScheme
        );
        Assert.Equal(
            "host-forbid",
            options.DefaultForbidScheme
        );
        using var http = fixture.Http(token: fixture.Token());
        using var request = new HttpRequestMessage(
            method: HttpMethod.Post,
            requestUri: "/mcp"
        ) {
            Content = new StreamContent(content: new MemoryStream(buffer: new byte[65537])),
        };

        request.Headers.TransferEncodingChunked = true;
        request.Content.Headers.ContentType = new(mediaType: "application/json");
        using var response = await http.SendAsync(
            request,
            Token
        );

        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            response.StatusCode
        );
        Assert.Equal(
            actual: fixture.Opened,
            expected: 0
        );
    }
    [Fact]
    public async Task MetadataNeedsOriginAuthenticationButNoCallerToken() {
        await using var fixture = new RemoteMcpFixture();

        await fixture.StartAsync(
            Token,
            proxy: true,
            embedded: true
        );
        using var anonymous = fixture.Http();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await anonymous.GetAsync(
                "/.well-known/oauth-protected-resource/mcp",
                Token
            )).StatusCode
        );
        using var origin = fixture.Http(token: fixture.Token(
            "front-door",
            "app-only"
        ));

        Assert.Equal(
            HttpStatusCode.OK,
            (await origin.GetAsync(
                "/.well-known/oauth-protected-resource/mcp",
                Token
            )).StatusCode
        );
    }

    private sealed class AssertionHost : RemoteMcpHost {
        internal string? Assertion;
        internal string? Subject;

        public override bool IsReady => true;
        public override IReadOnlyList<Tool> ServiceTools => [new() { Name = "assertion_probe", InputSchema = JsonElement.Parse("""{"type":"object","additionalProperties":false}""") }];
        public override bool SupportsAttachments => false;

        public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => throw new InvalidOperationException(message: "No attachments.");
        public override ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) {
            Assertion = caller.UserAssertion;
            Subject = caller.Subject;
            return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "Verified." }] });
        }
    }
}
