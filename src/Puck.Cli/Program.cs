using Puck.Cli;
using Puck.Confinement;

// An installed CLI registers as a holder of its version before it loads anything else, so pruning never removes it.
if (!InstallRegistration.TryRegisterProcess(refusal: out var refusal)) {
    Console.Error.WriteLine(value: refusal);
    return 2;
}
WorldsRoot.Start();
return await PuckRootCommand.InvokeAsync(args: args);
