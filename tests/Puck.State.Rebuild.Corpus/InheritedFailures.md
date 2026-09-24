# Inherited `Puck.World.Tests` failures

`dotnet test tests/Puck.World.Tests -c Release` fails no test on a tree whose
solution was built first. A name that fails is the running package's own.

One entry is conditional and counts as inherited when it appears:

- `MachineHostSeamLawTests.DesktopWorldAssemblyReferencesNoExtensionProject`
  (one case per extension project) loads `Puck.World.dll` from beside the test
  output or from `src/Puck.World`'s own Release or Debug build output, so it
  fails only on a tree where `src/Puck.World` was never built. A run that
  builds the solution reports none of its cases.
