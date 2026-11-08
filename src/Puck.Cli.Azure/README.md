# Azure CLI provider

`AzureCliExtension` contributes the `azure` `WorldReleaseHost` and its `puck azure`
command tree. `AzureCommand` owns Azure deployment, resource configuration and
the runtime adapter that drives `WorldReleaseCoordinator`. Registration performs
no cloud access. The provider-neutral CLI holds no reference to this project.

Install from the repository root:

```sh
dotnet publish src/Puck.Cli.Azure -c Release -o extensions/Puck.Cli.Azure
puck azure --help
puck world release status --host azure
```

Release operations select a host with `--host`; an omitted name requires exactly
one installed host. Official content scanning reads that host's root document
name. Without this extension, `azure` is absent and selecting release host
`azure` is refused by name. Reading a supplied deployment-group file remains
available without a provider.

The [Azure deployment procedure](../../docs/development/ci.md#azure-production-deployment)
owns CI installation and operator commands. The [extension reference](../../docs/reference/extensions.md)
owns discovery, composition and selection.
