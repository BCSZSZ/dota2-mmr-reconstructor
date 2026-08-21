# References and attribution

## ShowMMR

The collector workflow was informed by [Lypheo/ShowMMR](https://github.com/Lypheo/ShowMMR),
especially its use of the authenticated Dota 2 Game Coordinator Match History endpoint,
20-match pagination, local history reuse, and the observation that rank-change fields are
available only to the logged-in account. ShowMMR itself credits
[AveYo/ShowMMR](https://github.com/AveYo/ShowMMR/tree/main/ShowMMR_tool).

This repository does not vendor ShowMMR source code or release binaries. The collector was
implemented as a separate, data-only program: it preserves protobuf field presence, adds
account validation and resumable JSON storage, and requests the current RankedGlicko payload.
The interactive chart and the low-Confidence reconstruction model are separate components in
this repository.

ShowMMR reference revision used during development:
[`7bd10459fe6f1cd9ffc2d5ba6deb2ee5badd7980`](https://github.com/Lypheo/ShowMMR/tree/7bd10459fe6f1cd9ffc2d5ba6deb2ee5badd7980).

## Protocol and client references

- [SteamKit2](https://github.com/SteamRE/SteamKit) provides the Steam and Game Coordinator
  client library. The collector pins NuGet package `SteamKit2` 3.0.0, licensed under
  LGPL-2.1-only.
- [SteamDatabase/GameTracking-Dota2](https://github.com/SteamDatabase/GameTracking-Dota2)
  publishes tracked Dota 2 protobuf schemas used to identify Rank and Match History fields.
- [Valve: The New Frontiers Update](https://www.dota2.com/newfrontiers) describes the 2023
  switch to a Glicko-family rating system and Rank Confidence. Valve does not publish the
  server's complete MMR or confidence update formula.
- [Mark Glickman's Glicko resources](https://www.glicko.net/glicko.html) provide the public
  statistical background. The reconstruction model in this repository is an empirical,
  endpoint-constrained model; it is not claimed to be Valve's implementation.

## Other packaged dependencies

- [QRCoder](https://github.com/codebude/QRCoder), MIT, renders the Steam login QR code.
- [protobuf-net](https://github.com/protobuf-net/protobuf-net), Apache-2.0, is used transitively
  by SteamKit2.

See `collector/THIRD_PARTY_NOTICES.md` for the collector release notices.
