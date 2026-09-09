#!/usr/bin/env dotnet
#:property PublishAot=false

using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Prove UDP reachability and possession of this world's key, rather than just a running container.
if (args.Length != 3) {
    Console.Error.WriteLine("Usage: dotnet run build/Test-WorldSilo.cs -- <host> <port> <public-key-file>");
    return 1;
}
if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !QuicConnection.IsSupported) {
    throw new PlatformNotSupportedException("The probe requires QUIC support (libmsquic on Linux).");
}
var expectedKey = File.ReadAllBytes(args[2]);
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var request = new CertificateRequest("CN=puck-ci-probe", key, HashAlgorithmName.SHA256);
request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
// Schannel requires a persisted key binding when presenting a client certificate.
using var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.DefaultKeySet);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions {
    RemoteEndPoint = new DnsEndPoint(args[0], int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)),
    DefaultCloseErrorCode = 0,
    DefaultStreamErrorCode = 0,
    ClientAuthenticationOptions = new SslClientAuthenticationOptions {
        TargetHost = "puck-peer",
        ApplicationProtocols = [new SslApplicationProtocol("puck-peer")],
        ClientCertificates = [certificate],
        RemoteCertificateValidationCallback = (_, presented, _, _) => presented is X509Certificate2 peer &&
            CryptographicOperations.FixedTimeEquals(peer.PublicKey.ExportSubjectPublicKeyInfo(), expectedKey),
    },
}, timeout.Token);
await connection.CloseAsync(0, timeout.Token);
Console.WriteLine("PASS: the primary Puck world accepts QUIC connections using its expected federation key.");
return 0;
