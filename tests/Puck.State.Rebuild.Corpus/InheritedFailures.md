# Inherited `Puck.World.Tests` failures

`dotnet test tests/Puck.World.Tests -c Release` fails no test on a tree whose
solution was built first. A name that fails is the running package's own.

Two entries are conditional and count as inherited when they appear:

- `MachineHostSeamLawTests.DesktopWorldAssemblyReferencesNoCloudProviderProjects`
  and `DesktopWorldAssemblyReferencesNoOptionalExtensions` load `Puck.World.dll`
  from its own build output, so they fail only on a tree where `src/Puck.World`
  was never built. A run that builds the solution reports neither.
- `GeneratorExtendedLawTests.PerTickExtendedSite_AllocatesNothingAfterTheFirstRebuild`
  (`tests/Puck.State.Generators.Tests`) measures allocation after a warm-up and
  fails on roughly one run in eight.
