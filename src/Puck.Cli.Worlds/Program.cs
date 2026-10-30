using Puck.Cli;
using Puck.Confinement;

// This apphost ships inside the CLI's installed payload, so it registers as a holder of its version before it loads
// anything else, as `puck` does.
if (!InstallRegistration.TryRegisterProcess(refusal: out var refusal)) {
    Console.Error.WriteLine(value: refusal);
    return 2;
}
// A verb that compiles a tree in a process of its own runs this assembly alone; `puck` composes the same verbs beside
// every other.
WorldsRoot.Start();
return await CliRoot.InvokeAsync(
    args: args,
    root: CliRoot.Compose(description: "The Puck world-authoring verbs.", verbs: WorldsRoot.Verbs())
);
