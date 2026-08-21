from datetime import UTC, datetime

from dota2_mmr.matches import select_ranked_matches


def _record(
    match_id: int,
    timestamp: int,
    *,
    player_slot: int,
    radiant_win: bool,
    lobby_type: int = 7,
) -> dict[str, int | bool | str]:
    return {
        "match_id": match_id,
        "start_time": timestamp,
        "duration": 2400,
        "player_slot": player_slot,
        "radiant_win": radiant_win,
        "hero_id": 1,
        "lobby_type": lobby_type,
        "future_api_field": "preserved",
    }


def test_selection_filters_period_and_preserves_raw_fields() -> None:
    start = datetime(2026, 1, 1, tzinfo=UTC)
    end = datetime(2027, 1, 1, tzinfo=UTC)
    first = int(datetime(2026, 1, 2, tzinfo=UTC).timestamp())
    second = int(datetime(2026, 1, 3, tzinfo=UTC).timestamp())
    outside = int(datetime(2025, 12, 31, tzinfo=UTC).timestamp())

    collection = select_ranked_matches(
        account_id=123,
        records=[
            _record(2, second, player_slot=128, radiant_win=True),
            _record(1, first, player_slot=0, radiant_win=True),
            _record(3, outside, player_slot=0, radiant_win=True),
            _record(4, second, player_slot=0, radiant_win=True, lobby_type=0),
        ],
        start=start,
        end=end,
    )

    assert [match.match_id for match in collection.matches] == [1, 2]
    assert [match.won for match in collection.matches] == [True, False]
    assert collection.matches[0].raw["future_api_field"] == "preserved"
    assert collection.wins == 1
    assert collection.losses == 1


def test_selection_deduplicates_matches() -> None:
    start = datetime(2026, 1, 1, tzinfo=UTC)
    end = datetime(2027, 1, 1, tzinfo=UTC)
    timestamp = int(datetime(2026, 2, 1, tzinfo=UTC).timestamp())
    record = _record(10, timestamp, player_slot=0, radiant_win=True)

    collection = select_ranked_matches(
        account_id=123,
        records=[record, record],
        start=start,
        end=end,
    )

    assert collection.total == 1
