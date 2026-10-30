// This apphost ships inside the CLI's installed payload, so it registers as a holder of its version before it loads
// anything else.
if (!Puck.Confinement.InstallRegistration.TryRegisterProcess(refusal: out var refusal)) {
    Console.Error.WriteLine(value: refusal);
    return 2;
}
return await Puck.World.Silo.WorldSiloApplication.RunAsync(args);
