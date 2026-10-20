# Puck.Maths

Puck.Maths provides deterministic numerics for simulation, including fixed-point
values, reproducible random streams, exact integer and algebraic operations, and
quantized signed 8-bit vector arithmetic (`SignedByteVectorFunctions`). The one
home for the float helpers presentation code shares is `VectorFunctions`: the
vector admission checks, angle wraps, easings, shortest arc and perpendicular,
−Z-forward attitude readbacks and unorm packers, all outside the determinism
contract.

## Documentation

- [Deterministic numerics](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/maths.md) — worked examples, a primitive chooser, and the determinism boundary.
- [Prime exploration](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/prime-exploration.md) — the Nth prime, prime counting, uniform random primes, unsigned 64-bit primality, interval enumeration, constellations and wheel coordinates.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.Maths.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
