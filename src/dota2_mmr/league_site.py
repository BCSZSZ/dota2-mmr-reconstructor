from __future__ import annotations

import argparse
import json
import os
import random
import time
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import httpx
from dotenv import load_dotenv

API_BASE_URL = "https://api.opendota.com/api"
DEFAULT_LEAGUE_ID = 19224
DEFAULT_OUTPUT = Path("site/data/league.json")


class LeagueCollectionError(RuntimeError):
    """Raised when a league snapshot cannot be collected safely."""


class OpenDotaLeagueClient:
    def __init__(
        self,
        *,
        api_key: str | None = None,
        max_paid_calls: int = 250,
        timeout_seconds: float = 30.0,
        transport: httpx.BaseTransport | None = None,
    ) -> None:
        self._api_key = api_key or None
        self._max_paid_calls = max_paid_calls
        self._paid_calls = 0
        self._free_exhausted = False
        self._client = httpx.Client(
            base_url=API_BASE_URL,
            headers={"User-Agent": "dota2-tools-league-dashboard/1.0"},
            timeout=timeout_seconds,
            transport=transport,
        )

    @property
    def paid_calls(self) -> int:
        return self._paid_calls

    @property
    def free_exhausted(self) -> bool:
        return self._free_exhausted

    def __enter__(self) -> OpenDotaLeagueClient:
        return self

    def __exit__(self, *args: object) -> None:
        self.close()

    def close(self) -> None:
        self._client.close()

    @staticmethod
    def _error_message(response: httpx.Response) -> str:
        try:
            payload = response.json()
        except ValueError:
            return response.text.strip().lower()
        if isinstance(payload, dict):
            return str(payload.get("error") or payload.get("message") or payload).lower()
        return str(payload).lower()

    def _sleep_until_next_minute(self) -> None:
        remaining = 61.0 - (time.time() % 60.0)
        time.sleep(max(2.0, remaining) + random.uniform(0.2, 1.2))

    def _request(self, path: str, *, paid: bool) -> Any:
        headers: dict[str, str] = {}
        if paid:
            if not self._api_key:
                raise LeagueCollectionError(
                    "OpenDota anonymous daily quota was exhausted and no API key is configured"
                )
            if self._paid_calls >= self._max_paid_calls:
                raise LeagueCollectionError(
                    f"OpenDota paid-call safety limit reached ({self._max_paid_calls})"
                )
            headers["Authorization"] = f"Bearer {self._api_key}"

        for attempt in range(4):
            if paid:
                self._paid_calls += 1
            try:
                response = self._client.get(path, headers=headers)
            except httpx.RequestError as error:
                if attempt == 3:
                    raise LeagueCollectionError(
                        f"OpenDota request failed for {path}: {error}"
                    ) from error
                time.sleep(2**attempt + random.uniform(0.1, 0.8))
                continue

            message = self._error_message(response)
            if response.status_code == 429 and "minute rate limit exceeded" in message:
                if attempt == 3:
                    raise LeagueCollectionError(f"OpenDota minute rate limit persisted for {path}")
                self._sleep_until_next_minute()
                continue
            if response.status_code == 429 and "daily api limit exceeded" in message:
                if paid:
                    raise LeagueCollectionError("OpenDota API-key daily limit was exhausted")
                self._free_exhausted = True
                return self._request(path, paid=True)
            if response.status_code >= 500:
                if attempt == 3:
                    raise LeagueCollectionError(
                        f"OpenDota server error {response.status_code} for {path}"
                    )
                time.sleep(2**attempt + random.uniform(0.1, 0.8))
                continue
            if response.status_code >= 400:
                raise LeagueCollectionError(
                    f"OpenDota returned {response.status_code} for {path}: {message}"
                )
            try:
                return response.json()
            except ValueError as error:
                raise LeagueCollectionError(f"OpenDota returned invalid JSON for {path}") from error

        raise AssertionError("unreachable")

    def get_json(self, path: str) -> Any:
        return self._request(path, paid=self._free_exhausted)


def normalise_match(raw: dict[str, Any], league_id: int) -> dict[str, Any]:
    match_id = int(raw.get("match_id") or 0)
    actual_league_id = int(raw.get("leagueid") or 0)
    start_time = int(raw.get("start_time") or 0)
    duration = int(raw.get("duration") or 0)
    radiant_win = raw.get("radiant_win")
    players = raw.get("players")

    if match_id <= 0 or start_time <= 0 or duration < 0:
        raise LeagueCollectionError(f"Match {match_id or '<unknown>'} has invalid core fields")
    if actual_league_id != league_id:
        raise LeagueCollectionError(
            f"Match {match_id} belongs to league {actual_league_id}, expected {league_id}"
        )
    if not isinstance(radiant_win, bool):
        raise LeagueCollectionError(f"Match {match_id} has no scored winner")
    if not isinstance(players, list):
        raise LeagueCollectionError(f"Match {match_id} has no player roster")

    normalised_players: list[dict[str, Any]] = []
    for player in players:
        if not isinstance(player, dict):
            continue
        player_slot = player.get("player_slot")
        if not isinstance(player_slot, int):
            continue
        active_slot = 0 <= player_slot <= 4 or 128 <= player_slot <= 132
        if not active_slot:
            continue
        account_id = player.get("account_id")
        normalised_players.append(
            {
                "account_id": (
                    int(account_id) if isinstance(account_id, int) and account_id > 0 else None
                ),
                "personaname": str(player.get("personaname") or "").strip() or None,
                "player_slot": player_slot,
                "hero_id": int(player.get("hero_id") or 0),
            }
        )

    return {
        "match_id": match_id,
        "league_id": league_id,
        "start_time": start_time,
        "duration": duration,
        "radiant_win": radiant_win,
        "players": normalised_players,
    }


def _load_existing(path: Path, league_id: int) -> dict[int, dict[str, Any]]:
    if not path.exists():
        return {}
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    if int(payload.get("league_id") or 0) != league_id:
        return {}
    result: dict[int, dict[str, Any]] = {}
    for match in payload.get("matches", []):
        if isinstance(match, dict) and int(match.get("match_id") or 0) > 0:
            result[int(match["match_id"])] = match
    return result


def collect_league_data(
    *,
    league_id: int,
    output_path: Path,
    refresh_recent: int = 5,
    client: OpenDotaLeagueClient,
) -> dict[str, Any]:
    league = client.get_json(f"/leagues/{league_id}")
    match_ids_raw = client.get_json(f"/leagues/{league_id}/matchIds")
    if not isinstance(league, dict):
        raise LeagueCollectionError("OpenDota returned an invalid league response")
    if not isinstance(match_ids_raw, list):
        raise LeagueCollectionError("OpenDota returned an invalid match-ID list")

    match_ids = sorted({int(match_id) for match_id in match_ids_raw if int(match_id) > 0})
    existing = _load_existing(output_path, league_id)
    refresh_ids = set(match_ids[-max(0, refresh_recent) :])
    unavailable: list[dict[str, Any]] = []
    matches: dict[int, dict[str, Any]] = {}

    for match_id in match_ids:
        if match_id in existing and match_id not in refresh_ids:
            matches[match_id] = existing[match_id]
            continue
        try:
            raw = client.get_json(f"/matches/{match_id}")
            if not isinstance(raw, dict):
                raise LeagueCollectionError("invalid match payload")
            matches[match_id] = normalise_match(raw, league_id)
        except LeagueCollectionError as error:
            if match_id in existing:
                matches[match_id] = existing[match_id]
            else:
                unavailable.append({"match_id": match_id, "reason": str(error)})

    ordered_matches = [matches[match_id] for match_id in match_ids if match_id in matches]
    anonymous_slots = sum(
        1
        for match in ordered_matches
        for player in match.get("players", [])
        if player.get("account_id") is None
    )
    incomplete_rosters = [
        int(match["match_id"])
        for match in ordered_matches
        if len(match.get("players", [])) != 10
    ]

    return {
        "schema_version": 1,
        "league_id": league_id,
        "league": {
            "name": str(league.get("name") or f"League {league_id}"),
            "tier": str(league.get("tier") or ""),
            "banner": str(league.get("banner") or ""),
        },
        "generated_at_utc": datetime.now(UTC).isoformat(),
        "time_zone": "Asia/Tokyo",
        "source": "OpenDota public API",
        "metadata": {
            "match_ids_returned": len(match_ids),
            "matches_available": len(ordered_matches),
            "unavailable_matches": unavailable,
            "anonymous_player_slots": anonymous_slots,
            "incomplete_roster_match_ids": incomplete_rosters,
            "paid_calls": client.paid_calls,
            "anonymous_daily_quota_exhausted": client.free_exhausted,
        },
        "matches": ordered_matches,
    }


def write_snapshot(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(f"{path.suffix}.tmp")
    temporary.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    temporary.replace(path)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Collect a league snapshot for the GitHub Pages dashboard"
    )
    parser.add_argument("--league-id", type=int, default=DEFAULT_LEAGUE_ID)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--refresh-recent", type=int, default=5)
    parser.add_argument(
        "--max-paid-calls",
        type=int,
        default=int(os.getenv("OPENDOTA_MAX_PAID_CALLS", "250")),
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    load_dotenv()
    args = build_parser().parse_args(argv)
    with OpenDotaLeagueClient(
        api_key=os.getenv("OPENDOTA_API_KEY"),
        max_paid_calls=args.max_paid_calls,
    ) as client:
        payload = collect_league_data(
            league_id=args.league_id,
            output_path=args.output,
            refresh_recent=max(0, args.refresh_recent),
            client=client,
        )
    write_snapshot(args.output, payload)
    metadata = payload["metadata"]
    print(
        f"League {args.league_id}: wrote {metadata['matches_available']} matches "
        f"to {args.output} ({len(metadata['unavailable_matches'])} unavailable)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
