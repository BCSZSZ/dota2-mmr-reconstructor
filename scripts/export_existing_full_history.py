from __future__ import annotations

import argparse
import csv
import hashlib
import json
from datetime import UTC, datetime
from pathlib import Path

if __package__:
    from scripts.analyze_confidence_model import (
        build_complete_history_curve,
        export_analysis,
        load_showmmr_csv,
    )
else:
    from analyze_confidence_model import (
        build_complete_history_curve,
        export_analysis,
        load_showmmr_csv,
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Prepend immutable pre-Glicko ShowMMR truth to an existing "
            "post-Glicko reconstruction without making any network request."
        )
    )
    parser.add_argument("--account-id", required=True, type=int)
    parser.add_argument("--showmmr-csv", required=True, type=Path)
    parser.add_argument("--analysis-dir", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument(
        "--start-date",
        help="include matches on or after this ISO date (interpreted as UTC)",
    )
    return parser.parse_args()


def _load_existing_match_rows(path: Path) -> list[dict[str, object]]:
    with path.open(encoding="utf-8-sig", newline="") as source:
        rows: list[dict[str, object]] = list(csv.DictReader(source))
    for row in rows:
        row["mmr_fields_visible"] = row["mmr_fields_visible"].lower() == "true"
        for field in (
            "unix_time",
            "hero_id",
            "modeled_rank_change",
            "curve_mmr_before",
            "curve_mmr_after",
            "anchor_jump_before",
        ):
            value = row.get(field)
            if value not in (None, ""):
                row[field] = int(float(str(value)))
    return rows


def _full_history_statistics(rows: list[dict[str, object]]) -> dict[str, object]:
    values = [int(row["curve_mmr_after"]) for row in rows]
    actual = [row for row in rows if row["mmr_fields_visible"] is True]
    modeled = [row for row in rows if row["mmr_fields_visible"] is not True]
    wins = sum(row["result"] == "Win" for row in rows)
    losses = sum(row["result"] == "Loss" for row in rows)

    def extremum(
        candidates: list[dict[str, object]], *, maximum: bool
    ) -> dict[str, object]:
        row = (max if maximum else min)(
            candidates,
            key=lambda item: int(item["curve_mmr_after"]),
        )
        return {
            "mmr": int(row["curve_mmr_after"]),
            "date_utc": row["date_utc"],
            "match_id": row["match_id"],
            "source": row["curve_source"],
        }
    anchor_jumps = [
        {
            "date_utc": row["date_utc"],
            "match_id": row["match_id"],
            "jump": int(row["anchor_jump_before"]),
        }
        for row in rows
        if int(row.get("anchor_jump_before") or 0) != 0
    ]
    return {
        "matches": len(rows),
        "actual_gc_matches": len(actual),
        "endpoint_constrained_matches": len(modeled),
        "wins": wins,
        "losses": losses,
        "win_rate": wins / (wins + losses),
        "first_match_utc": rows[0]["date_utc"],
        "last_match_utc": rows[-1]["date_utc"],
        "first_start_mmr": int(rows[0]["curve_mmr_before"]),
        "current_mmr": values[-1],
        "minimum_mmr_after": min(values),
        "maximum_mmr_after": max(values),
        "actual_minimum": extremum(actual, maximum=False),
        "actual_maximum": extremum(actual, maximum=True),
        "modeled_minimum": extremum(modeled, maximum=False) if modeled else None,
        "modeled_maximum": extremum(modeled, maximum=True) if modeled else None,
        "net_change_from_first_start": values[-1]
        - int(rows[0]["curve_mmr_before"]),
        "sum_of_match_deltas": sum(int(row["modeled_rank_change"]) for row in rows),
        "sum_of_anchor_discontinuities": sum(
            int(row.get("anchor_jump_before") or 0) for row in rows
        ),
        "anchor_discontinuities": len(anchor_jumps),
        "largest_anchor_discontinuities": sorted(
            anchor_jumps,
            key=lambda item: abs(int(item["jump"])),
            reverse=True,
        )[:20],
        "warning": (
            "Anchor discontinuities are retained rather than smoothed. Large historical "
            "jumps can reflect distinct rating pools, recalibration, settlement order, "
            "or missing observations; they are not modeled match deltas."
        ),
    }


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _apply_start_date(
    rows: list[dict[str, object]],
    start_date: str | None,
) -> tuple[list[dict[str, object]], datetime | None]:
    if start_date is None:
        return rows, None
    cutoff = datetime.fromisoformat(start_date)
    if cutoff.tzinfo is None:
        cutoff = cutoff.replace(tzinfo=UTC)
    else:
        cutoff = cutoff.astimezone(UTC)
    selected = [
        row
        for row in rows
        if datetime.fromisoformat(str(row["date_utc"])).astimezone(UTC) >= cutoff
    ]
    if not selected:
        raise ValueError("start date excludes every curve row")
    selected[0]["anchor_jump_before"] = 0
    return selected, cutoff


def main() -> int:
    args = parse_args()
    summary_path = args.analysis_dir / "model-summary.json"
    estimates_path = args.analysis_dir / "match-estimates.csv"
    with summary_path.open(encoding="utf-8") as source:
        summary: dict[str, object] = json.load(source)
    if summary.get("account_id") != args.account_id:
        raise ValueError("existing analysis belongs to a different account")

    source_records = load_showmmr_csv(args.showmmr_csv)
    post_glicko_rows = _load_existing_match_rows(estimates_path)
    complete_rows = build_complete_history_curve(source_records, post_glicko_rows)
    complete_rows, cutoff = _apply_start_date(complete_rows, args.start_date)
    pre_glicko_count = sum(
        row["curve_source"] == "GC actual (pre-Glicko)" for row in complete_rows
    )

    curve_summary = summary.get("curve_reconstruction")
    if not isinstance(curve_summary, dict):
        raise ValueError("existing analysis has no curve_reconstruction summary")
    curve_summary.update(
        {
            "matches": len(complete_rows),
            "pre_glicko_actual_matches": pre_glicko_count,
            "post_glicko_matches": len(post_glicko_rows),
            "actual_gc_matches": sum(
                row["mmr_fields_visible"] is True for row in complete_rows
            ),
        }
    )
    summary["input_source"] = "existing_showmmr_truth_plus_v1_reconstruction"
    summary["full_history"] = _full_history_statistics(complete_rows)
    summary["curve_window"] = {
        "requested_start_utc": cutoff.isoformat() if cutoff is not None else None,
        "first_observation_utc": complete_rows[0]["date_utc"],
        "excluded_earlier_matches": len(source_records) + len(
            [row for row in post_glicko_rows if row["mmr_fields_visible"] is not True]
        )
        - len(complete_rows),
        "reason": (
            "Only the single-rank era after the Core/Support rating-track merge "
            "is shown and evaluated. Earlier source rows remain untouched."
            if cutoff is not None
            else "No start-date filter."
        ),
    }
    summary["source_preservation"] = {
        "showmmr_csv": str(args.showmmr_csv.resolve()),
        "showmmr_csv_sha256": _sha256(args.showmmr_csv),
        "existing_analysis": str(args.analysis_dir.resolve()),
        "existing_match_estimates_sha256": _sha256(estimates_path),
        "source_files_modified": False,
        "output_directory": str(args.output_dir.resolve()),
    }

    paths = export_analysis(
        args.output_dir,
        summary,
        post_glicko_rows,
        curve_rows=complete_rows,
    )
    print(json.dumps({"output_paths": [str(path.resolve()) for path in paths]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
