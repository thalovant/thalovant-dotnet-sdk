# Bouncy Castle managed primitive subset

`src/Thalovant.Sdk/Internal/BouncyCastle` contains the X25519, constant-time
field inversion, Argon2id and BLAKE2b source subset from **Bouncy Castle 2.6.2**,
commit [`b4f2f6ad76bcd1f11f365ee50cc7447fbce79077`](https://github.com/bcgit/bc-csharp/tree/b4f2f6ad76bcd1f11f365ee50cc7447fbce79077).
The corresponding upstream paths are recorded in `tools/noise-primitives-sources.json`.

Adaptations are mechanical: namespace isolation/internal visibility, portable
array branches selected instead of intrinsics/span optimizations, removal of
unneeded RNG/precomputation entrypoints, and extraction of used utility methods.
X25519 public-key derivation uses the equivalent Montgomery basepoint-9 path
explicitly documented in upstream `ScalarMultBase`, avoiding the unrelated
Ed25519 dependency. Field arithmetic, inversion, Argon2 and BLAKE2b algorithms
are retained. `Compatibility.cs` supplies small non-cryptographic adapters for
array operations, UTF-8 conversion and output bounds.

To reproduce the subset, check out that exact upstream commit and run:

```sh
python3 tools/vendor-noise-primitives.py /path/to/bc-csharp
```

The SDK sequences Noise revision 34 itself and uses platform AES-256 with its
existing SP800-38D GCM arithmetic (extended for AAD; secret-dependent GHASH
branches replaced with masks). No external runtime cryptography package is
required. Known-answer tests use independent Node/noble handshake/transport
transcripts, upstream Argon2/AESGCM values, and low-order-key rejection.

The checked-in `noise-node.json` fixture contains synthetic test keys only. It
was generated using the Thalovant Node SDK Noise implementation with noble
primitives and fixed ephemeral entropy; none of its key material is deployed.
The complete shared fixture retains both cipher suites so it stays comparable
with the other SDKs. The .NET transcript test explicitly checks the two AESGCM
exchanges (XXpsk2 and KKpsk0); the retained ChaChaPoly exchanges do not imply
.NET support for that suite.

## Bouncy Castle license

Copyright (c) 2000-2025 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org).
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sub license, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions: The above copyright notice and this
permission notice shall be included in all copies or substantial portions of the Software.

**THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.**
