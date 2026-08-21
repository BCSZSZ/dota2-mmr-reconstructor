from collections.abc import Iterable, Mapping
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import Any

RANKED_LOBBY_TYPE = 7


@dataclass(frozen=True, slots=True)
class RankedMatch:
    raw: dict[str, Any]
    match_id: int
    started_at: datetime
    player_slot: int
    radiant_win: bool
    won: bool

    @property
    def side(self) -> str:
        return "radiant" if self.player_slot < 128 else "dire"


@dataclass(frozen=True, slots=True)
class RankedMatchCollection:
    account_id: int
    start: datetime
    end: datetime
    matches: tuple[RankedMatch, ...]

    @property
    def total(self) -> int:
        return len(self.matches)

    @property
    def wins(self) -> int:
        return sum(match.won for match in self.matches)

    @property
    def losses(self) -> int:
        return self.total - self.wins

    @property
    def win_rate(self) -> float:
        return self.wins / self.total if self.total else 0.0


def _parse_match(record: Mapping[str, Any]) -> RankedMatch:
    radiant_win = record.get("radiant_win")
    if not isinstance(radiant_win, bool):
        raise ValueError(f"match {record.get('match_id')} has no final result")

    player_slot = int(record["player_slot"])
    is_radiant = player_slot < 128
    return RankedMatch(
        raw=dict(record),
        match_id=int(record["match_id"]),
        started_at=datetime.fromtimestamp(int(record["start_time"]), tz=UTC),
        player_slot=player_slot,
        radiant_win=radiant_win,
        won=radiant_win if is_radiant else not radiant_win,
    )


def select_ranked_matches(
    *,
    account_id: int,
    records: Iterable[Mapping[str, Any]],
    start: datetime,
    end: datetime,
) -> RankedMatchCollection:
    if start.tzinfo is None or end.tzinfo is None:
        raise ValueError("match boundaries must be timezone-aware")
    if end <= start:
        raise ValueError("match period end must be later than start")

    matches_by_id: dict[int, RankedMatch] = {}
    for record in records:
        if int(record.get("lobby_type", -1)) != RANKED_LOBBY_TYPE:
            continue
        match = _parse_match(record)
        if start <= match.started_at < end:
            matches_by_id[match.match_id] = match

    matches = sorted(matches_by_id.values(), key=lambda match: (match.started_at, match.match_id))
    return RankedMatchCollection(
        account_id=account_id,
        start=start,
        end=end,
        matches=tuple(matches),
    )
