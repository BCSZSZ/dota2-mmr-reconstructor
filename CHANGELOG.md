# Changelog

## 0.5.6 - 2026-09-11

- Continue C# reconstruction when a hidden segment's observed endpoint change cannot satisfy the model's win/loss bounds.
- Preserve affected matches in CSV/JSON with unknown deltas and curve values; show gaps and real endpoint markers in charts without inventing calibration changes or per-match contributions.
- Disclose unresolved coverage in summaries, completion notices and hero/teammate reports; exclude unresolved matches from contribution statistics and teammate collection scope.
- Add regression coverage for the 4-win/6-loss +915 case, incompatible all-win/all-loss segments and a trailing Current Rank anchor.
- Keep GC request pacing, retries and caching unchanged.

## 0.5.5 - 2026-09-11

- Align corresponding teammate chart cards in shared rows on desktop, including after filtering or text wrapping; preserve favorable-first ordering on mobile.

## 0.5.4 - 2026-09-11

- Embed the ranked MMR curve in teammate reports with drag selection, adjustable boundaries and inclusive UTC date inputs.
- Recompute all teammate statistics from per-match samples using the selected dates, normal-match toggle and minimum valid-match count (default eleven, minimum two).
- Retain candidates with at least two shared matches and collect their missing STRATZ scores so short ranges can be analyzed.
- Rename visible IMP labels to average overall performance; group favorable charts on the left and unfavorable charts on the right, favorable first on mobile.

## 0.5.3 - 2026-09-11

- Add an opt-in normal-match checkbox for teammate GC details, STRATZ IMP and reports; MMR reconstruction remains ranked-only.
- Allow offline switching between ranked-only and ranked plus standard normal matchmaking, with all eight charts and the minimum-match slider updating together.
- Show ranked/normal sample counts, preserve ranked-only MMR samples and require eleven ranked matches for MMR leaderboards.
- Support `--include-normal-matches` online and in cached offline reconstruction; exclude Turbo, Ability Draft, events, custom games and bots.

## 0.5.2 - 2026-09-11

- Replace static teammate winner cards with eight offline Top 10 bar charts, updated by a minimum shared-match slider and number input.
- Update teammate counts, unique shared matches, net MMR and the complete table together with the slider.
- Explain STRATZ setup using the website's “我的 Tokens / My Tokens” labels and explicit copy/paste steps.
- Add direct buttons to open the teammate report, MMR curve and output folder when collection finishes.
- Print the full teammate report path in the completion summary and console.

## 0.5.1 - 2026-09-11

- Preserve an open or write-protected hero workbook and save the new Excel report with a timestamp instead of aborting reconstruction and teammate collection.
- Show the alternate workbook filename in the completion dialog, console and output manifest.
- Add a regression test that holds the old workbook open while regenerating all reports.

## 0.5.0 - 2026-09-10

- Add an optional ranked teammate report to the existing Windows app, with eight leaderboards and per-metric coverage.
- Collect player IDs and KDA through GC MatchDetails in the existing login session, with resumable per-match caching.
- Identify teammates by at least eleven same-side matches within the MMR chart scope; aggregate by account ID.
- Fetch STRATZ IMP only for matches containing selected teammates, with a Steam OpenID attempt and masked personal-token fallback.
- Store STRATZ tokens with Windows DPAPI; retain missing IMP as unavailable and preserve the original MMR reports on service failures.
- Support cached, offline teammate reports with `--reconstruct-existing --teammates`.

## 0.3.0 - 2026-08-21

- Add a 2300x1250 shareable PNG rendering of the complete reconstructed curve.
- Add a hero MMR contribution report, sorted from highest to lowest net contribution.
- Separate actual GC contribution from endpoint-constrained fitted contribution in the report.

## 0.2.1 - 2026-08-21

- Explain the fixed output-root rule and automatic account subdirectories in the GUI.
- Document automatic cache discovery, recent-match catch-up and oldest-cursor continuation.

## 0.2.0 - 2026-08-21

- Port the production low-Confidence reconstruction path from Python to C#.
- Add a double-click GUI for Steam ID, history target and output directory.
- Generate CSV, JSON, SVG and a self-contained interactive HTML after collection.
- Keep `--raw-only` and add offline `--reconstruct-existing` mode.
- Retain the Python implementation as a reference and research workflow.

## 0.1.0 - 2026-08-21

- Add the QR-authenticated, resumable raw GC Match History and Current Rank collector.
- Add endpoint-constrained Glicko-shaped low-Confidence MMR reconstruction with Double Down
  mixture inference.
- Preserve exact GC values separately from modeled rows and generate machine-readable summaries.
- Add the local interactive MMR chart/table viewer with zoom, pan, filters and standalone export.
- Add Windows collector packaging, CI, release automation, attribution and third-party notices.
