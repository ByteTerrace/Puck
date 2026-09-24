# Puck.Input

Puck.Input normalizes controller reports and window input for command capture.
It provides device protocols, player slots, motion fusion, and controller output.

## Keyboard focus

`SourceFocus` routes keys and text to a passthrough source the local user
focused by clicking it, or to the game. Control, Alt and Escape together always
return focus to the game. The contract is in
[Device input](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/input.md#keyboard-focus-and-passthrough-sources).

## Documentation

- [Device input](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/input.md) — usage and host contracts.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — architecture and related topics.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.Input.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
