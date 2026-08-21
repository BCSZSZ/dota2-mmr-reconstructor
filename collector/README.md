# Dota 2 MMR Collector

This executable only downloads raw data for the Steam account that approves the QR login:

- Dota 2 Game Coordinator Match History, preserving protobuf field presence;
- the current RankedGlicko Rank payload (`rank_value`, `rank_data1/2/3`);
- a resumable local Match History cache.

It does not run the low-Confidence model and does not contain the HTML chart viewer.

Close Dota 2 before starting the collector because one account can have only one active Dota
Game Coordinator session. Double-clicking the executable fetches 5,000 history rows and writes
`gc-collection.json` plus `gc-match-history-cache.json` beside the executable.

Command-line usage:

```powershell
.\Dota2MmrCollector.exe `
  --account-id 123456789 `
  --history-matches 8000 `
  --output artifacts\123456789\gc-collection.json `
  --history-cache artifacts\123456789\gc-match-history-cache.json
```

`--account-id` is the Steam account ID32 and is optional, but specifying it prevents accidentally
saving data from the wrong scanned account. The QR login token remains in memory and is not
written to the output.
