import csv
import json
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any
from zoneinfo import ZoneInfo

from dota2_mmr.matches import RankedMatch, RankedMatchCollection
from dota2_mmr.mmr import MmrEstimate
from dota2_mmr.mmr_chart import write_mmr_estimate_svg

PREFERRED_CSV_FIELDS = [
    "match_id",
    "start_time",
    "started_at_utc",
    "started_at_local",
    "result",
    "side",
    "radiant_win",
    "player_slot",
    "duration",
    "game_mode",
    "lobby_type",
    "hero_id",
    "hero_variant",
    "kills",
    "deaths",
    "assists",
    "average_rank",
    "leaver_status",
    "party_size",
    "version",
]


@dataclass(frozen=True, slots=True)
class OutputPaths:
    matches_json: Path
    matches_csv: Path
    summary_json: Path


@dataclass(frozen=True, slots=True)
class MmrEstimateOutputPaths:
    estimate_json: Path
    estimate_csv: Path
    estimate_svg: Path


def _export_record(match: RankedMatch, local_timezone: ZoneInfo) -> dict[str, Any]:
    record = dict(match.raw)
    record.update(
        {
            "started_at_utc": match.started_at.isoformat(),
            "started_at_local": match.started_at.astimezone(local_timezone).isoformat(),
            "result": "win" if match.won else "loss",
            "side": match.side,
        }
    )
    return record


def _csv_value(value: Any) -> Any:
    if isinstance(value, (dict, list)):
        return json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    return value


def export_matches(
    *,
    collection: RankedMatchCollection,
    base_directory: Path,
    local_timezone: ZoneInfo,
    player_name: str | None,
    fetched_at: datetime,
) -> OutputPaths:
    year = collection.start.astimezone(local_timezone).year
    output_directory = base_directory / str(collection.account_id) / str(year)
    output_directory.mkdir(parents=True, exist_ok=True)
    matches_json = output_directory / f"ranked-matches-{year}.json"
    matches_csv = output_directory / f"ranked-matches-{year}.csv"
    summary_json = output_directory / f"summary-{year}.json"

    records = [_export_record(match, local_timezone) for match in collection.matches]
    payload = {
        "source": {
            "provider": "OpenDota",
            "endpoint": f"/players/{collection.account_id}/matches",
            "parameters": {"limit": 10_000, "lobby_type": 7, "significant": 0},
            "fetched_at_utc": fetched_at.isoformat(),
        },
        "account_id": collection.account_id,
        "player_name": player_name,
        "period_timezone": str(local_timezone),
        "period_start_inclusive": collection.start.astimezone(local_timezone).isoformat(),
        "period_end_exclusive": collection.end.astimezone(local_timezone).isoformat(),
        "match_count": collection.total,
        "matches": records,
    }
    with matches_json.open("w", encoding="utf-8") as file:
        json.dump(payload, file, ensure_ascii=False, indent=2)
        file.write("\n")

    extra_fields = sorted({key for record in records for key in record} - set(PREFERRED_CSV_FIELDS))
    fieldnames = [field for field in PREFERRED_CSV_FIELDS if any(field in row for row in records)]
    fieldnames.extend(extra_fields)
    with matches_csv.open("w", encoding="utf-8-sig", newline="") as file:
        writer = csv.DictWriter(file, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()
        for record in records:
            writer.writerow({key: _csv_value(value) for key, value in record.items()})

    summary = {
        "account_id": collection.account_id,
        "player_name": player_name,
        "period_timezone": str(local_timezone),
        "period_start_inclusive": collection.start.astimezone(local_timezone).isoformat(),
        "period_end_exclusive": collection.end.astimezone(local_timezone).isoformat(),
        "ranked_lobby_type": 7,
        "matches": collection.total,
        "wins": collection.wins,
        "losses": collection.losses,
        "win_rate": round(collection.win_rate, 6),
        "fetched_at_utc": fetched_at.isoformat(),
        "mmr_calculated": False,
    }
    with summary_json.open("w", encoding="utf-8") as file:
        json.dump(summary, file, ensure_ascii=False, indent=2)
        file.write("\n")

    return OutputPaths(
        matches_json=matches_json,
        matches_csv=matches_csv,
        summary_json=summary_json,
    )


def export_mmr_estimate(
    *,
    estimate: MmrEstimate,
    base_directory: Path,
    local_timezone: ZoneInfo,
    player_name: str | None,
) -> MmrEstimateOutputPaths:
    year = estimate.collection.start.astimezone(local_timezone).year
    output_directory = base_directory / str(estimate.collection.account_id) / str(year)
    output_directory.mkdir(parents=True, exist_ok=True)
    estimate_json = output_directory / f"mmr-estimate-{year}.json"
    estimate_csv = output_directory / f"mmr-estimate-{year}.csv"
    estimate_svg = output_directory / f"mmr-estimate-{year}.svg"

    points = [
        {
            "match_id": point.match.match_id,
            "started_at_utc": point.match.started_at.isoformat(),
            "started_at_local": point.match.started_at.astimezone(local_timezone).isoformat(),
            "result": "win" if point.match.won else "loss",
            "assumed_delta": point.assumed_delta,
            "estimated_mmr_after_match": point.estimated_mmr_after_match,
            "is_anchor": point.is_anchor,
        }
        for point in estimate.points
    ]
    payload = {
        "account_id": estimate.collection.account_id,
        "player_name": player_name,
        "period_timezone": str(local_timezone),
        "method": "fixed_delta_per_result",
        "assumed_mmr_per_win": estimate.mmr_per_result,
        "assumed_mmr_per_loss": -estimate.mmr_per_result,
        "anchor": {
            "source": "user_provided",
            "match_id": estimate.anchor_match_id,
            "mmr_after_match": estimate.anchor_mmr_after_match,
        },
        "estimated_mmr_before_period": estimate.estimated_mmr_before_period,
        "estimated_mmr_after_period": estimate.estimated_mmr_after_period,
        "estimated_period_change": estimate.estimated_period_change,
        "warning": (
            "This is a rough reconstruction, not Valve's actual per-match MMR history. "
            "Every win/loss is assumed to change MMR by a fixed amount."
        ),
        "points": points,
    }
    with estimate_json.open("w", encoding="utf-8") as file:
        json.dump(payload, file, ensure_ascii=False, indent=2)
        file.write("\n")

    with estimate_csv.open("w", encoding="utf-8-sig", newline="") as file:
        writer = csv.DictWriter(file, fieldnames=list(points[0]))
        writer.writeheader()
        writer.writerows(points)

    write_mmr_estimate_svg(
        estimate=estimate,
        destination=estimate_svg,
        local_timezone=local_timezone,
        player_name=player_name,
    )
    return MmrEstimateOutputPaths(
        estimate_json=estimate_json,
        estimate_csv=estimate_csv,
        estimate_svg=estimate_svg,
    )
