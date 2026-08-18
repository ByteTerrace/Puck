# Puck.Networking.Host

A thin console demonstration of [`Puck.Networking`](../Puck.Networking/README.md)'s
peer substrate (`Puck.Networking.Peers`) over its QUIC transport. The same
binary runs on every side of a connection — there is no `--server`/`--client`
mode.

```text
dotnet run --project src/Puck.Networking.Host -- --listen <port> [--dial <host:port>]... [--key <file>]
```

- `--listen <port>` binds a QUIC (UDP) listener on `0.0.0.0:<port>` and accepts
  connections in the background. Omit it to run dial-only.
- `--dial <host:port>` connects to another peer at startup. Repeatable.
- `--key <file>` loads a persisted identity from `<file>` if it exists, or
  generates one and saves it there. Omitted entirely, the identity is
  ephemeral and generated fresh at every start.

Exit code 2 with a line on stderr means QUIC is not available on the host;
`Puck.Networking`'s README states the deployment requirement.

## stdin verbs

- `dial <host:port>` — connect to another peer while running.
- `send <peerIdPrefix> <text>` — sign and send `text` to the open link whose
  remote identity fingerprint starts with `peerIdPrefix`.
- `peers` — list every currently open link's identity and remote endpoint.
- `quit` — exit.

## stdout events

One line per event: `peer.id <fingerprint>` at startup, `listening
<endpoint>`, `link.up <peerId> <remote>`, `link.down <peerId> <reason>`,
`recv <peerId> <text>`, `refused <reason>` (a message refused on an open
link, or a dial the far side refused), `handshake.refused <remote> <reason>`
(an inbound connection this side refused before any link existed).
