import argparse
from collections.abc import Sequence
from datetime import UTC, datetime
from pathlib import Path
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from dota2_mmr import __version__
from dota2_mmr.config import ConfigError, Settings
from dota2_mmr.export import export_matches, export_mmr_estimate
from dota2_mmr.matches import select_ranked_matches
from dota2_mmr.mmr import estimate_fixed_result_mmr
from dota2_mmr.opendota import OpenDotaClient, OpenDotaError

STEAM_ID64_OFFSET = 76_561_197_960_265_728


def normalize_account_id(value: int) -> int:
    account_id = value - STEAM_ID64_OFFSET if value >= STEAM_ID64_OFFSET else value
    if not 0 < account_id <= 0xFFFFFFFF:
        raise argparse.ArgumentTypeError("Steam ID must be a valid Steam32 account ID or SteamID64")
    return account_id


def positive_int(value: str) -> int:
    parsed = int(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("value must be greater than zero")
    return parsed


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="dota2-mmr",
        description="Collect ranked matches from OpenDota without downloading replays.",
    )
    parser.add_argument(
        "--version",
        action="version",
        version=f"%(prog)s {__version__}",
    )
    parser.add_argument(
        "account_id",
        nargs="?",
        type=int,
        help="Steam32 account ID or SteamID64",
    )
    parser.add_argument("--year", type=int, default=datetime.now(UTC).year)
    parser.add_argument("--timezone", default="Asia/Tokyo")
    parser.add_argument("--output-dir", type=Path, default=Path("artifacts"))
    parser.add_argument(
        "--anchor-match-id",
        type=positive_int,
        help="match whose post-match MMR is known",
    )
    parser.add_argument(
        "--anchor-mmr",
        type=positive_int,
        help="known MMR immediately after --anchor-match-id",
    )
    parser.add_argument(
        "--mmr-per-result",
        type=positive_int,
        default=25,
        help="rough fixed MMR change per win/loss (default: 25)",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.account_id is None:
        parser.print_help()
        return 0
    if (args.anchor_match_id is None) != (args.anchor_mmr is None):
        parser.error("--anchor-match-id and --anchor-mmr must be provided together")

    try:
        account_id = normalize_account_id(args.account_id)
        local_timezone = ZoneInfo(args.timezone)
        settings = Settings.from_env()
        now = datetime.now(UTC)
        local_start = datetime(args.year, 1, 1, tzinfo=local_timezone)
        local_end = datetime(args.year + 1, 1, 1, tzinfo=local_timezone)
        start = local_start.astimezone(UTC)
        end = min(local_end.astimezone(UTC), now)
        if end <= start:
            parser.error("--year cannot be in the future")

        with OpenDotaClient(settings.opendota_api_key) as client:
            profile = client.get_player(account_id)
            raw_matches = client.get_ranked_matches(account_id)

        fetched_at = datetime.now(UTC)
        matches = select_ranked_matches(
            account_id=account_id,
            records=raw_matches,
            start=start,
            end=end,
        )
        output_paths = export_matches(
            collection=matches,
            base_directory=args.output_dir,
            local_timezone=local_timezone,
            player_name=profile.get("profile", {}).get("personaname"),
            fetched_at=fetched_at,
        )
        estimate_paths = None
        estimate = None
        if args.anchor_match_id is not None and args.anchor_mmr is not None:
            estimate = estimate_fixed_result_mmr(
                matches,
                anchor_match_id=args.anchor_match_id,
                anchor_mmr_after_match=args.anchor_mmr,
                mmr_per_result=args.mmr_per_result,
            )
            estimate_paths = export_mmr_estimate(
                estimate=estimate,
                base_directory=args.output_dir,
                local_timezone=local_timezone,
                player_name=profile.get("profile", {}).get("personaname"),
            )
    except (
        argparse.ArgumentTypeError,
        ConfigError,
        OpenDotaError,
        ZoneInfoNotFoundError,
        ValueError,
    ) as error:
        parser.error(str(error))

    player_name = profile.get("profile", {}).get("personaname") or "unknown"
    print(f"Player: {player_name} ({account_id})")
    print(f"Period timezone: {local_timezone}")
    print(f"Ranked matches: {matches.total} ({matches.wins}W / {matches.losses}L)")
    print(f"Win rate: {matches.win_rate:.1%}")
    print(f"Match data (JSON): {output_paths.matches_json}")
    print(f"Matches: {output_paths.matches_csv}")
    print(f"Summary: {output_paths.summary_json}")
    if estimate is not None and estimate_paths is not None:
        print(
            "Rough MMR estimate: "
            f"{estimate.estimated_mmr_before_period} -> {estimate.estimated_mmr_after_period} "
            f"({estimate.estimated_period_change:+d})"
        )
        print(f"MMR estimate data: {estimate_paths.estimate_json}")
        print(f"MMR estimate CSV: {estimate_paths.estimate_csv}")
        print(f"MMR estimate chart: {estimate_paths.estimate_svg}")
        print("Warning: fixed-delta reconstruction; not Valve's actual per-match MMR history.")
    return 0
