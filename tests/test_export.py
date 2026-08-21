import json
from datetime import UTC, datetime
from zoneinfo import ZoneInfo

from dota2_mmr.export import export_matches, export_mmr_estimate
from dota2_mmr.matches import select_ranked_matches
from dota2_mmr.mmr import estimate_fixed_result_mmr


def test_export_writes_complete_json_csv_and_summary(tmp_path) -> None:
    timestamp = int(datetime(2026, 1, 2, tzinfo=UTC).timestamp())
    collection = select_ranked_matches(
        account_id=123,
        records=[
            {
                "match_id": 456,
                "start_time": timestamp,
                "duration": 2400,
                "player_slot": 0,
                "radiant_win": True,
                "hero_id": 1,
                "lobby_type": 7,
                "new_field": 99,
            }
        ],
        start=datetime(2026, 1, 1, tzinfo=UTC),
        end=datetime(2027, 1, 1, tzinfo=UTC),
    )

    paths = export_matches(
        collection=collection,
        base_directory=tmp_path,
        local_timezone=ZoneInfo("UTC"),
        player_name="Player",
        fetched_at=datetime(2026, 2, 1, tzinfo=UTC),
    )

    payload = json.loads(paths.matches_json.read_text(encoding="utf-8"))
    summary = json.loads(paths.summary_json.read_text(encoding="utf-8"))
    csv_text = paths.matches_csv.read_text(encoding="utf-8-sig")
    assert payload["matches"][0]["new_field"] == 99
    assert payload["matches"][0]["result"] == "win"
    assert "new_field" in csv_text
    assert summary["mmr_calculated"] is False


def test_export_writes_anchored_mmr_estimate(tmp_path) -> None:
    timestamp = int(datetime(2026, 1, 2, tzinfo=UTC).timestamp())
    collection = select_ranked_matches(
        account_id=123,
        records=[
            {
                "match_id": 456,
                "start_time": timestamp,
                "player_slot": 0,
                "radiant_win": True,
                "lobby_type": 7,
            }
        ],
        start=datetime(2026, 1, 1, tzinfo=UTC),
        end=datetime(2027, 1, 1, tzinfo=UTC),
    )
    estimate = estimate_fixed_result_mmr(
        collection,
        anchor_match_id=456,
        anchor_mmr_after_match=1000,
    )

    paths = export_mmr_estimate(
        estimate=estimate,
        base_directory=tmp_path,
        local_timezone=ZoneInfo("UTC"),
        player_name="Player",
    )

    payload = json.loads(paths.estimate_json.read_text(encoding="utf-8"))
    assert payload["anchor"] == {
        "source": "user_provided",
        "match_id": 456,
        "mmr_after_match": 1000,
    }
    assert paths.estimate_csv.is_file()
    assert paths.estimate_svg.read_text(encoding="utf-8").startswith("<svg")
