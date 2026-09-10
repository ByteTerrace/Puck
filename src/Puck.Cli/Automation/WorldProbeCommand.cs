using System.CommandLine;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Puck.Cli.Automation;

internal static class WorldProbeCommand {
    public static Command Create() {
        var hostArgument = new Argument<string>(name: "host") { Description = "The world's public host name or address." };
        var portArgument = new Argument<int>(name: "port") { Description = "The world's QUIC port." };
        var keyArgument = new Argument<string>(name: "public-key-file") { Description = "The SubjectPublicKeyInfo the world must prove possession of." };
        var command = new Command(description: "Prove UDP reachability and possession of this world's key, rather than just a running container.", name: "probe") { hostArgument, portArgument, keyArgument };

        command.SetAction(action: (parseResult, _) => RunAsync(host: parseResult.GetRequiredValue(argument: hostArgument), keyFile: parseResult.GetRequiredValue(argument: keyArgument), port: parseResult.GetRequiredValue(argument: portArgument)));
        return command;
    }

    private static async Task<int> RunAsync(string host, string keyFile, int port) {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !QuicConnection.IsSupported) {
            throw new PlatformNotSupportedException(message: "The probe requires QUIC support (libmsquic on Linux).");
        }
        var expectedKey = File.ReadAllBytes(path: keyFile);
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=puck-ci-probe", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(item: new X509KeyUsageExtension(critical: false, keyUsages: X509KeyUsageFlags.DigitalSignature));
        request.CertificateExtensions.Add(item: new X509EnhancedKeyUsageExtension([new Oid(oid: "1.3.6.1.5.5.7.3.2")], false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(minutes: -1), DateTimeOffset.UtcNow.AddMinutes(minutes: 10));
        // Schannel requires a persisted key binding when presenting a client certificate.
        using var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(contentType: X509ContentType.Pkcs12), null, X509KeyStorageFlags.DefaultKeySet);
        using var timeout = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 20));
        await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions {
            RemoteEndPoint = new DnsEndPoint(host: host, port: port),
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions {
                TargetHost = "puck-peer",
                ApplicationProtocols = [new SslApplicationProtocol(protocol: "puck-peer")],
                ClientCertificates = [certificate],
                RemoteCertificateValidationCallback = (_, presented, _, _) => ((presented is X509Certificate2 peer) &&
                    CryptographicOperations.FixedTimeEquals(left: peer.PublicKey.ExportSubjectPublicKeyInfo(), right: expectedKey)),
            },
        }, timeout.Token);

        await connection.CloseAsync(0, timeout.Token);
        Console.WriteLine(value: "PASS: the primary Puck world accepts QUIC connections using its expected federation key.");
        return 0;
    }
}
