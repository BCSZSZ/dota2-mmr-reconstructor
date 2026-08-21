# GC data contract used by this project

The Game Coordinator interface is undocumented and can change. This project stores each optional
protobuf value together with its `Present` flag so an omitted field is never confused with an
explicit zero.

## Collector output

`gc-collection.json` contains:

- `AccountId` and capture timestamp;
- raw RankedGlicko Current Rank fields: `rank_value`, `rank_data1`, `rank_data2`, `rank_data3`;
- raw Match History rows with MatchID, time, hero, winner, lobby/game mode, previous rank,
  rank change, solo/abandon flags and duration;
- pagination/cache completion metadata.

The collector does not request other players' rank data, does not download replays, does not run
OpenDota parsing, and does not interpret Rank Confidence.

## Known client interpretation

Read-only analysis of Dota 2 `client.dll` build 6907 identified the following consumer mapping:

- `rank_value`: current MMR;
- `rank_data1`: base uncertainty;
- `rank_data3`: uncertainty time reference in wall-clock seconds;
- `rank_data2`: unknown in the studied consumption path.

The Python module `src/dota2_mmr/rank_confidence.py` contains the reproducible float32 time
projection and uncertainty-to-display mapping. Its build fingerprint is kept in code so future
client changes can be detected rather than silently treated as stable.

## Operational constraints

- The private Match History/Current Rank path is available only for the authenticated account.
- Dota 2 must be closed because the account cannot keep a second GC session for this collector.
- History is paginated in groups of 20 and deliberately paced at one page per second.
- The resumable cache is account-bound and is written atomically.
- QR refresh tokens remain in process memory and are cleared after output; they are never
  serialized.

See [`../REFERENCES.md`](../REFERENCES.md) for ShowMMR attribution, protocol schemas and licenses.
