# Changelog

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
