from datetime import UTC, datetime

import pytest

from dota2_mmr.matches import select_ranked_matches
from dota2_mmr.mmr import estimate_fixed_result_mmr


def _collection():
    start = datetime(2026, 1, 1, tzinfo=UTC)
    records = [
        {
            "match_id": 1,
            "start_time": int(datetime(2026, 1, 2, tzinfo=UTC).timestamp()),
            "player_slot": 0,
            "radiant_win": True,
            "lobby_type": 7,
        },
        {
            "match_id": 2,
            "start_time": int(datetime(2026, 1, 3, tzinfo=UTC).timestamp()),
            "player_slot": 0,
            "radiant_win": False,
            "lobby_type": 7,
        },
        {
            "match_id": 3,
            "start_time": int(datetime(2026, 1, 4, tzinfo=UTC).timestamp()),
            "player_slot": 128,
            "radiant_win": False,
            "lobby_type": 7,
        },
    ]
    return select_ranked_matches(
        account_id=123,
        records=records,
        start=start,
        end=datetime(2027, 1, 1, tzinfo=UTC),
    )


def test_estimate_backtracks_from_latest_anchor() -> None:
    estimate = estimate_fixed_result_mmr(
        _collection(),
        anchor_match_id=3,
        anchor_mmr_after_match=1000,
    )

    assert [point.assumed_delta for point in estimate.points] == [25, -25, 25]
    assert [point.estimated_mmr_after_match for point in estimate.points] == [1000, 975, 1000]
    assert estimate.estimated_mmr_before_period == 975
    assert estimate.estimated_period_change == 25


def test_estimate_projects_forward_from_middle_anchor() -> None:
    estimate = estimate_fixed_result_mmr(
        _collection(),
        anchor_match_id=2,
        anchor_mmr_after_match=975,
    )

    assert [point.estimated_mmr_after_match for point in estimate.points] == [1000, 975, 1000]


def test_estimate_rejects_unknown_anchor() -> None:
    with pytest.raises(ValueError, match="anchor match"):
        estimate_fixed_result_mmr(
            _collection(),
            anchor_match_id=999,
            anchor_mmr_after_match=1000,
        )
