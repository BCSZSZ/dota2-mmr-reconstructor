# Third-party notices

The collector dynamically loads the following NuGet dependencies. Their source is not copied
into this repository.

- SteamKit2 3.0.0 — LGPL-2.1-only — <https://github.com/SteamRE/SteamKit>
- QRCoder 1.4.3 — MIT — <https://github.com/codebude/QRCoder>
- protobuf-net — Apache-2.0 — <https://github.com/protobuf-net/protobuf-net>
- Microsoft .NET runtime libraries — MIT — <https://github.com/dotnet/runtime>

The packaged SteamKit2 notice is included at `licenses/SteamKit2-LGPL-2.1.txt`. The release is
framework-dependent and keeps SteamKit2 as a separate DLL rather than merging it into the
collector executable.

The collection approach was informed by ShowMMR; see the repository-level `REFERENCES.md`.
