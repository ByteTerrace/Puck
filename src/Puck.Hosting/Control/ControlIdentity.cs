namespace Puck.Hosting;

/// <summary>A transport-validated delegated identity. Contains no bearer token; hosts must authorize it for the addressed resource.</summary>
/// <param name="Issuer">The exact validated OAuth issuer.</param>
/// <param name="Subject">The validated subject within that issuer.</param>
public sealed record ControlIdentity(string Issuer, string Subject);
