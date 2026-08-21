from __future__ import annotations

import argparse
import csv
import json
import math
import os
import statistics
from dataclasses import asdict, dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

from dotenv import load_dotenv

from dota2_mmr.opendota import OpenDotaClient
from dota2_mmr.rank_confidence import project_rank_uncertainty, rank_confidence_percent
from dota2_mmr.reconstruction_chart import write_reconstruction_svg

GLICKO_START = datetime(2023, 4, 20, tzinfo=UTC)
SINGLE_RANK_START = datetime(2020, 3, 2, tzinfo=UTC)
MODEL_VERSION = "endpoint-constrained-glicko-dd-v2"


@dataclass(frozen=True, slots=True)
class ShowMmrRecord:
    match_id: int
    started_at: datetime
    start_mmr: int
    rank_change: int
    hero_id: int
    solo_queue: bool

    @property
    def end_mmr(self) -> int:
        return self.start_mmr + self.rank_change


@dataclass(frozen=True, slots=True)
class TimelineMatch:
    match_id: int
    started_at: datetime
    duration_seconds: int
    won: bool
    hero_id: int
    average_rank: int | None
    party_size: int | None
    reported: ShowMmrRecord | None

    @property
    def ended_at(self) -> datetime:
        return self.started_at + timedelta(seconds=self.duration_seconds)


@dataclass(frozen=True, slots=True)
class GcProbeAnchor:
    account_id: int
    current_mmr: int
    base_uncertainty: int
    projected_uncertainty: int
    confidence_percent: int
    time_base_unix: int
    observed_at_unix: int


@dataclass(frozen=True, slots=True)
class HiddenSegment:
    number: int
    matches: tuple[TimelineMatch, ...]
    previous_visible: TimelineMatch | None
    next_visible: TimelineMatch | None

    @property
    def wins(self) -> int:
        return sum(match.won for match in self.matches)

    @property
    def losses(self) -> int:
        return len(self.matches) - self.wins

    @property
    def observed_total_change(self) -> int | None:
        if self.previous_visible is None or self.next_visible is None:
            return None
        assert self.previous_visible.reported is not None
        assert self.next_visible.reported is not None
        return self.next_visible.reported.start_mmr - self.previous_visible.reported.end_mmr


def load_showmmr_csv(path: Path) -> dict[int, ShowMmrRecord]:
    records: dict[int, ShowMmrRecord] = {}
    with path.open(encoding="utf-8-sig", newline="") as source:
        for row in csv.DictReader(source):
            record = ShowMmrRecord(
                match_id=int(row["MatchID"]),
                started_at=datetime.fromtimestamp(int(row["Unix time"]), tz=UTC),
                start_mmr=int(row["Start MMR"]),
                rank_change=int(row["Rank Change"]),
                hero_id=int(row["HeroID"]),
                solo_queue=row["Solo Queue"].strip().lower() == "true",
            )
            records[record.match_id] = record
    return records


def _required_probe_observation(container: dict[str, object], name: str) -> int:
    observation = container.get(name)
    if not isinstance(observation, dict) or observation.get("Present") is not True:
        raise ValueError(f"GC probe field {name} is not present")
    value = observation.get("Value")
    if isinstance(value, bool) or not isinstance(value, int):
        raise ValueError(f"GC probe field {name} is not an integer")
    return value


def load_gc_probe_anchor(path: Path, *, expected_account_id: int) -> GcProbeAnchor:
    with path.open(encoding="utf-8") as source:
        payload = json.load(source)
    if not isinstance(payload, dict):
        raise ValueError("GC probe output must be a JSON object")

    account_id = payload.get("AccountId")
    if account_id != expected_account_id:
        raise ValueError(
            f"GC probe account {account_id!r} does not match requested account "
            f"{expected_account_id}"
        )

    current_rank = payload.get("CurrentRank")
    confidence = payload.get("RankConfidence")
    if not isinstance(current_rank, dict):
        raise ValueError("GC probe output has no completed Current Rank observation")

    if isinstance(confidence, dict):
        time_base = confidence.get(
            "TimeBaseUnix", confidence.get("TimeBaseSecondsCandidate")
        )
        values = {
            "base_uncertainty": confidence.get("BaseUncertainty"),
            "projected_uncertainty": confidence.get("ProjectedUncertainty"),
            "confidence_percent": confidence.get("DisplayConfidencePercent"),
            "time_base_unix": time_base,
            "observed_at_unix": confidence.get("ObservedAtUnix"),
        }
    else:
        captured_at_raw = payload.get("CapturedAtUtc")
        if not isinstance(captured_at_raw, str):
            raise ValueError("raw GC collector output has no CapturedAtUtc timestamp")
        try:
            captured_at = datetime.fromisoformat(captured_at_raw.replace("Z", "+00:00"))
        except ValueError as error:
            raise ValueError("raw GC collector CapturedAtUtc is invalid") from error
        if captured_at.tzinfo is None:
            captured_at = captured_at.replace(tzinfo=UTC)
        base_uncertainty = _required_probe_observation(current_rank, "RankData1")
        time_base = _required_probe_observation(current_rank, "RankData3")
        observed_at_unix = int(captured_at.timestamp())
        projected_uncertainty = project_rank_uncertainty(
            base_uncertainty,
            time_base_unix=time_base,
            now_unix=observed_at_unix,
        )
        values = {
            "base_uncertainty": base_uncertainty,
            "projected_uncertainty": projected_uncertainty,
            "confidence_percent": rank_confidence_percent(projected_uncertainty),
            "time_base_unix": time_base,
            "observed_at_unix": observed_at_unix,
        }
    for name, value in values.items():
        if isinstance(value, bool) or not isinstance(value, int):
            raise ValueError(f"GC probe RankConfidence field {name} is not an integer")

    return GcProbeAnchor(
        account_id=account_id,
        current_mmr=_required_probe_observation(current_rank, "RankValue"),
        base_uncertainty=values["base_uncertainty"],
        projected_uncertainty=values["projected_uncertainty"],
        confidence_percent=values["confidence_percent"],
        time_base_unix=values["time_base_unix"],
        observed_at_unix=values["observed_at_unix"],
    )


def _history_field(
    game: dict[str, object],
    name: str,
    *,
    required: bool = False,
) -> object | None:
    observation = game.get(name)
    if not isinstance(observation, dict) or observation.get("Present") is not True:
        if required:
            raise ValueError(f"GC Match History field {name} is not present")
        return None
    return observation.get("Value")


def load_gc_match_history_timeline(
    path: Path,
    *,
    expected_account_id: int,
) -> list[TimelineMatch]:
    with path.open(encoding="utf-8") as source:
        payload = json.load(source)
    if not isinstance(payload, dict):
        raise ValueError("GC collector output must be a JSON object")
    if payload.get("AccountId") != expected_account_id:
        raise ValueError(
            f"GC collector account {payload.get('AccountId')!r} does not match "
            f"requested account {expected_account_id}"
        )

    history = payload.get("MatchHistory")
    if not isinstance(history, dict):
        raise ValueError("GC collector output has no MatchHistory section")
    if history.get("Finished") is not True or history.get("Error") is not None:
        raise ValueError(
            f"GC Match History collection is incomplete: {history.get('Error')!r}"
        )
    games = history.get("Matches")
    if not isinstance(games, list):
        raise ValueError("GC MatchHistory.Matches must be a list")

    by_match_id: dict[int, TimelineMatch] = {}
    result_fallbacks = 0
    skipped_unknown_results = 0
    for raw_game in games:
        if not isinstance(raw_game, dict):
            raise ValueError("GC Match History contains a non-object row")
        lobby_type = _history_field(raw_game, "LobbyType")
        if lobby_type != 7:
            continue

        match_id_value = _history_field(raw_game, "MatchId", required=True)
        start_time_value = _history_field(raw_game, "StartTime", required=True)
        winner_value = _history_field(raw_game, "Winner")
        previous_rank = _history_field(raw_game, "PreviousRank")
        rank_change = _history_field(raw_game, "RankChange")
        if isinstance(match_id_value, bool) or not isinstance(match_id_value, int):
            raise ValueError("GC Match History MatchId must be an integer")
        if isinstance(start_time_value, bool) or not isinstance(start_time_value, int):
            raise ValueError("GC Match History StartTime must be an integer")
        started_at = datetime.fromtimestamp(start_time_value, tz=UTC)
        if started_at < SINGLE_RANK_START:
            continue

        if isinstance(winner_value, bool):
            won = winner_value
        elif (
            isinstance(rank_change, int)
            and not isinstance(rank_change, bool)
            and rank_change != 0
        ):
            # GC omits Winner for some scored abandons while still returning
            # exact PreviousRank/RankChange. Preserve the actual MMR row and
            # use the signed server delta as the rating outcome.
            won = rank_change > 0
            result_fallbacks += 1
        else:
            skipped_unknown_results += 1
            continue

        reported = None
        if (
            isinstance(previous_rank, int)
            and not isinstance(previous_rank, bool)
            and previous_rank > 0
            and isinstance(rank_change, int)
            and not isinstance(rank_change, bool)
        ):
            reported = ShowMmrRecord(
                match_id=match_id_value,
                started_at=started_at,
                start_mmr=previous_rank,
                rank_change=rank_change,
                hero_id=int(_history_field(raw_game, "HeroId") or 0),
                solo_queue=bool(_history_field(raw_game, "SoloRank") or False),
            )

        # Before Rank Confidence/Glicko, absent rank fields do not identify a
        # low-Confidence modeling gap. Keep only exact single-rank-era GC rows;
        # from the Glicko launch onward, retain absent rows as hidden matches.
        if started_at < GLICKO_START and reported is None:
            continue

        by_match_id[match_id_value] = TimelineMatch(
            match_id=match_id_value,
            started_at=started_at,
            duration_seconds=int(_history_field(raw_game, "Duration") or 0),
            won=won,
            hero_id=int(_history_field(raw_game, "HeroId") or 0),
            average_rank=None,
            party_size=None,
            reported=reported,
        )

    if result_fallbacks:
        print(
            "Recovered "
            f"{result_fallbacks} ranked GC row(s) with absent Winner from exact "
            "signed RankChange."
        )
    if skipped_unknown_results:
        print(
            "Skipped "
            f"{skipped_unknown_results} ranked GC row(s) with neither Winner nor "
            "a non-zero exact RankChange."
        )

    timeline = sorted(
        by_match_id.values(),
        key=lambda match: (match.started_at, match.match_id),
    )
    if not timeline:
        raise ValueError("GC Match History contains no single-rank-era ranked matches")
    return timeline


def _optional_int(value: object) -> int | None:
    return None if value is None else int(value)


def build_timeline(
    raw_matches: list[dict[str, object]],
    showmmr: dict[int, ShowMmrRecord],
) -> list[TimelineMatch]:
    timeline: list[TimelineMatch] = []
    seen_match_ids: set[int] = set()
    for raw in raw_matches:
        started_at = datetime.fromtimestamp(int(raw["start_time"]), tz=UTC)
        if started_at < GLICKO_START:
            continue
        player_slot = int(raw["player_slot"])
        radiant_win = bool(raw["radiant_win"])
        is_radiant = player_slot < 128
        match_id = int(raw["match_id"])
        seen_match_ids.add(match_id)
        timeline.append(
            TimelineMatch(
                match_id=match_id,
                started_at=started_at,
                duration_seconds=int(raw.get("duration") or 0),
                won=radiant_win if is_radiant else not radiant_win,
                hero_id=int(raw["hero_id"]),
                average_rank=_optional_int(raw.get("average_rank")),
                party_size=_optional_int(raw.get("party_size")),
                reported=showmmr.get(match_id),
            )
        )

    # OpenDota can have public-history gaps that the authenticated GC history
    # does not. Visible ShowMMR rows are still ranked matches with an exact
    # signed result, so include them to avoid inventing unexplained MMR jumps.
    for record in showmmr.values():
        if record.started_at < SINGLE_RANK_START or record.match_id in seen_match_ids:
            continue
        if record.rank_change == 0:
            continue
        timeline.append(
            TimelineMatch(
                match_id=record.match_id,
                started_at=record.started_at,
                duration_seconds=0,
                won=record.rank_change > 0,
                hero_id=record.hero_id,
                average_rank=None,
                party_size=None,
                reported=record,
            )
        )
    timeline.sort(key=lambda match: (match.started_at, match.match_id))
    return timeline


def find_hidden_segments(timeline: list[TimelineMatch]) -> list[HiddenSegment]:
    segments: list[HiddenSegment] = []
    start: int | None = None
    for index in range(len(timeline) + 1):
        hidden = index < len(timeline) and timeline[index].reported is None
        if hidden and start is None:
            start = index
        if not hidden and start is not None:
            segment_matches = tuple(timeline[start:index])
            segments.append(
                HiddenSegment(
                    number=len(segments) + 1,
                    matches=segment_matches,
                    previous_visible=timeline[start - 1] if start > 0 else None,
                    next_visible=timeline[index] if index < len(timeline) else None,
                )
            )
            start = None
    return segments


def update_uncertainty_after_match(
    uncertainty_before: int,
    *,
    information_gain: float,
) -> int:
    """Apply a Glicko-inspired precision-gain proxy for one match.

    This is deliberately not labelled as Valve's server update. It is the
    smallest model that respects Glicko's additive-information shape while the
    actual per-match uncertainty formula remains unavailable.
    """

    if uncertainty_before <= 0:
        raise ValueError("uncertainty_before must be positive")
    if not math.isfinite(information_gain) or information_gain < 0:
        raise ValueError("information_gain must be finite and non-negative")
    if information_gain == 0:
        return uncertainty_before

    updated = math.sqrt(
        1.0 / (1.0 / (uncertainty_before * uncertainty_before) + information_gain)
    )
    return min(3000, max(90, math.floor(updated + 0.5)))


def simulate_uncertainty(
    timeline: list[TimelineMatch],
    *,
    start_index: int,
    information_gain_per_match: float,
) -> tuple[list[int | None], list[float | None], int]:
    uncertainty_estimates: list[int | None] = [None] * len(timeline)
    confidence_estimates: list[float | None] = [None] * len(timeline)
    uncertainty_before = 150
    for index in range(start_index + 1, len(timeline)):
        previous_match = timeline[index - 1]
        uncertainty_after_previous = update_uncertainty_after_match(
            uncertainty_before,
            information_gain=information_gain_per_match,
        )
        uncertainty_before = project_rank_uncertainty(
            uncertainty_after_previous,
            time_base_unix=int(previous_match.ended_at.timestamp()),
            now_unix=int(timeline[index].started_at.timestamp()),
        )
        uncertainty_estimates[index] = uncertainty_before
        confidence_estimates[index] = rank_confidence_percent(uncertainty_before) / 100

    uncertainty_estimates[start_index] = 150
    confidence_estimates[start_index] = 0.30
    ending_base_uncertainty = update_uncertainty_after_match(
        uncertainty_before,
        information_gain=information_gain_per_match,
    )
    return uncertainty_estimates, confidence_estimates, ending_base_uncertainty


def fit_confidence_proxy(
    timeline: list[TimelineMatch],
    *,
    current_base_uncertainty: int | None = None,
) -> dict[str, object]:
    first_hidden = next(
        (index for index, match in enumerate(timeline) if match.reported is None),
        None,
    )
    if first_hidden is None:
        start_index = 0
    else:
        first_recovered = next(
            (
                index
                for index in range(first_hidden + 1, len(timeline))
                if timeline[index].reported is not None
            ),
            None,
        )
        start_index = (
            first_recovered if first_recovered is not None else max(0, first_hidden - 1)
        )
    best: tuple[
        tuple[float, ...],
        float,
        int,
        float,
        int,
        list[int | None],
        list[float | None],
    ] | None = None
    for gain_step in range(1, 1001):
        information_gain = gain_step * 1e-8
        uncertainty_estimates, confidence_estimates, ending_uncertainty = simulate_uncertainty(
            timeline,
            start_index=start_index,
            information_gain_per_match=information_gain,
        )
        log_loss = 0.0
        mismatches = 0
        for match, uncertainty in zip(
            timeline[start_index:], uncertainty_estimates[start_index:], strict=True
        ):
            assert uncertainty is not None
            observed = 1.0 if match.reported is not None else 0.0
            probability = 1.0 / (1.0 + math.exp((uncertainty - 150.5) / 2.0))
            probability = min(1 - 1e-9, max(1e-9, probability))
            log_loss -= observed * math.log(probability) + (1 - observed) * math.log(
                1 - probability
            )
            mismatches += (uncertainty <= 150) != bool(observed)
        endpoint_error = (
            abs(ending_uncertainty - current_base_uncertainty)
            if current_base_uncertainty is not None
            else 0
        )
        selection_key = (
            (float(endpoint_error), float(mismatches), log_loss, information_gain)
            if current_base_uncertainty is not None
            else (float(mismatches), log_loss, information_gain)
        )
        score = mismatches * 1_000 + log_loss
        candidate = (
            selection_key,
            score,
            mismatches,
            information_gain,
            ending_uncertainty,
            uncertainty_estimates,
            confidence_estimates,
        )
        if best is None or candidate[0] < best[0]:
            best = candidate
    assert best is not None
    (
        selection_key,
        score,
        mismatches,
        information_gain,
        ending_uncertainty,
        uncertainty_estimates,
        estimates,
    ) = best
    return {
        "score": score,
        "selection_key": selection_key,
        "mismatches": mismatches,
        "information_gain_per_match": information_gain,
        "match_update_model": "U_after=round(1/sqrt(1/U_before^2+information_gain))",
        "inactivity_projection": (
            "client.dll float32 U projection using rank_data1/rank_data3 mapping"
        ),
        "display_mapping": "client.dll piecewise quadratic U-to-Confidence mapping",
        "start_index": start_index,
        "current_endpoint_target_base_uncertainty": current_base_uncertainty,
        "current_endpoint_modeled_base_uncertainty": ending_uncertainty,
        "current_endpoint_residual": (
            ending_uncertainty - current_base_uncertainty
            if current_base_uncertainty is not None
            else None
        ),
        "uncertainty_estimates": uncertainty_estimates,
        "estimates": estimates,
    }


def _weighted_linear_fit(
    xs: list[float], ys: list[float], weights: list[float]
) -> tuple[float, float]:
    total_weight = sum(weights)
    mean_x = sum(weight * x for x, weight in zip(xs, weights, strict=True)) / total_weight
    mean_y = sum(weight * y for y, weight in zip(ys, weights, strict=True)) / total_weight
    denominator = sum(weight * (x - mean_x) ** 2 for x, weight in zip(xs, weights, strict=True))
    if denominator <= 1e-12:
        return mean_y, 0.0
    slope = (
        sum(
            weight * (x - mean_x) * (y - mean_y)
            for x, y, weight in zip(xs, ys, weights, strict=True)
        )
        / denominator
    )
    return mean_y - slope * mean_x, slope


def _robust_linear_fit(xs: list[float], ys: list[float]) -> tuple[float, float]:
    weights = [1.0] * len(xs)
    intercept, slope = _weighted_linear_fit(xs, ys, weights)
    for _ in range(30):
        residuals = [y - (intercept + slope * x) for x, y in zip(xs, ys, strict=True)]
        scale = 1.4826 * statistics.median(abs(value) for value in residuals)
        if scale < 1e-6:
            break
        cutoff = 1.5 * scale
        weights = [1.0 if abs(value) <= cutoff else cutoff / abs(value) for value in residuals]
        new_intercept, new_slope = _weighted_linear_fit(xs, ys, weights)
        if abs(new_intercept - intercept) + abs(new_slope - slope) < 1e-8:
            intercept, slope = new_intercept, new_slope
            break
        intercept, slope = new_intercept, new_slope
    return intercept, slope


def fit_visible_delta_model(
    timeline: list[TimelineMatch],
    confidence_estimates: list[float | None],
    *,
    start_index: int,
) -> dict[str, object]:
    outcome_models: dict[str, object] = {}
    for won, label in ((True, "win"), (False, "loss")):
        observations = [
            (
                max(0.30, min(1.0, confidence_estimates[index] or 0.30)),
                abs(match.reported.rank_change),
            )
            for index, match in enumerate(timeline)
            if index >= start_index and match.reported is not None and match.won == won
        ]
        multipliers = [1] * len(observations)
        stable_fallback = 27.0 if won else 25.0
        intercept = stable_fallback
        slope = (40.0 - stable_fallback) / 0.70
        if observations:
            for _ in range(12):
                xs = [1.0 - confidence for confidence, _ in observations]
                normalized = [
                    magnitude / multiplier
                    for (_, magnitude), multiplier in zip(
                        observations, multipliers, strict=True
                    )
                ]
                intercept, slope = _robust_linear_fit(xs, normalized)
                predictions = [max(10.0, intercept + slope * x) for x in xs]
                updated = [
                    2
                    if magnitude >= 40
                    and magnitude / prediction >= 1.60
                    and abs(magnitude / 2 - prediction) < abs(magnitude - prediction)
                    else 1
                    for (_, magnitude), prediction in zip(
                        observations, predictions, strict=True
                    )
                ]
                if updated == multipliers:
                    break
                multipliers = updated

        residuals = [
            magnitude / multiplier - max(10.0, intercept + slope * (1.0 - confidence))
            for (confidence, magnitude), multiplier in zip(observations, multipliers, strict=True)
        ]
        outcome_models[label] = {
            "intercept_at_100pct": intercept,
            "slope_per_unit_uncertainty": slope,
            "value_at_30pct": intercept + 0.70 * slope,
            "observations": len(observations),
            "likely_double_downs": sum(multiplier == 2 for multiplier in multipliers),
            "mae": (
                sum(abs(value) for value in residuals) / len(residuals)
                if residuals
                else None
            ),
            "rmse": (
                math.sqrt(sum(value * value for value in residuals) / len(residuals))
                if residuals
                else None
            ),
            "median_actual": (
                statistics.median(magnitude for _, magnitude in observations)
                if observations
                else None
            ),
            "fallback_used": not observations,
        }
    return outcome_models


def fit_low_confidence_model(
    segments: list[HiddenSegment],
    timeline: list[TimelineMatch],
    confidence_estimates: list[float | None],
    visible_model: dict[str, object],
    *,
    current_mmr: int | None = None,
) -> dict[str, object]:
    index_by_match_id = {match.match_id: index for index, match in enumerate(timeline)}
    win_intercept = float(visible_model["win"]["intercept_at_100pct"])  # type: ignore[index]
    win_slope = float(visible_model["win"]["slope_per_unit_uncertainty"])  # type: ignore[index]
    loss_intercept = float(visible_model["loss"]["intercept_at_100pct"])  # type: ignore[index]
    loss_slope = float(visible_model["loss"]["slope_per_unit_uncertainty"])  # type: ignore[index]

    def inherited_delta(match: TimelineMatch, confidence: float) -> float:
        if match.won:
            return win_intercept + win_slope * (1.0 - confidence)
        return -(loss_intercept + loss_slope * (1.0 - confidence))

    current_segment = next(
        (segment for segment in segments if segment.next_visible is None),
        None,
    )

    def observed_change(segment: HiddenSegment) -> tuple[int | None, str | None]:
        if segment.observed_total_change is not None:
            return segment.observed_total_change, "next_visible_match"
        if (
            current_mmr is not None
            and segment is current_segment
            and segment.previous_visible is not None
            and segment.previous_visible.reported is not None
        ):
            return current_mmr - segment.previous_visible.reported.end_mmr, "current_rank_gc"
        return None, None

    candidates: list[dict[str, float | int | str]] = []
    for segment in segments:
        segment_change, endpoint_source = observed_change(segment)
        if segment.number == 1 or segment_change is None:
            continue
        confidences: list[float] = []
        inherited_change = 0.0
        for match in segment.matches:
            estimate = confidence_estimates[index_by_match_id[match.match_id]]
            confidence = min(0.299, max(0.0, estimate if estimate is not None else 0.0))
            confidences.append(confidence)
            inherited_change += inherited_delta(match, confidence)
        calibration_ratio = (
            segment_change / inherited_change
            if abs(inherited_change) > 1e-9 and segment_change * inherited_change > 0
            else math.nan
        )
        net_wins = segment.wins - segment.losses
        symmetric_delta_implied = (
            segment_change / net_wins
            if net_wins != 0 and segment_change * net_wins > 0
            else math.nan
        )
        candidates.append(
            {
                "segment": segment.number,
                "matches": len(segment.matches),
                "wins": segment.wins,
                "losses": segment.losses,
                "net_wins": net_wins,
                "average_confidence": sum(confidences) / len(confidences),
                "observed_change": segment_change,
                "endpoint_source": endpoint_source or "unknown",
                "symmetric_delta_implied": symmetric_delta_implied,
                "inherited_model_change": inherited_change,
                "calibration_ratio": calibration_ratio,
            }
        )

    finite_ratios = [
        float(candidate["calibration_ratio"])
        for candidate in candidates
        if math.isfinite(float(candidate["calibration_ratio"]))
    ]
    symmetric_delta_observations = [
        float(candidate["symmetric_delta_implied"])
        for candidate in candidates
        if math.isfinite(float(candidate["symmetric_delta_implied"]))
    ]
    calibration = statistics.median(finite_ratios) if finite_ratios else 1.0
    symmetric_delta_fallback = 75.75
    symmetric_delta_basis = (
        symmetric_delta_observations
        if symmetric_delta_observations
        else [symmetric_delta_fallback]
    )
    residuals = [
        float(candidate["observed_change"])
        - calibration * float(candidate["inherited_model_change"])
        for candidate in candidates
    ]

    for candidate, residual in zip(candidates, residuals, strict=True):
        candidate["calibrated_model_change"] = calibration * float(
            candidate["inherited_model_change"]
        )
        candidate["residual"] = residual

    current_projection: dict[str, object] | None = None
    if current_segment is not None and current_segment.previous_visible is not None:
        assert current_segment.previous_visible.reported is not None
        inherited_change = 0.0
        confidences = []
        for match in current_segment.matches:
            estimate = confidence_estimates[index_by_match_id[match.match_id]]
            confidence = min(0.299, max(0.0, estimate if estimate is not None else 0.0))
            confidences.append(confidence)
            inherited_change += inherited_delta(match, confidence)
        conservative_factor = 1.0
        aggressive_factor = max(finite_ratios, default=1.0)
        start_mmr = current_segment.previous_visible.reported.end_mmr
        anchored_change, endpoint_source = observed_change(current_segment)
        endpoint_factor = (
            anchored_change / inherited_change
            if anchored_change is not None
            and abs(inherited_change) > 1e-9
            and anchored_change * inherited_change > 0
            else None
        )
        best_guess_change = (
            float(anchored_change)
            if anchored_change is not None
            else calibration * inherited_change
        )
        current_projection = {
            "matches": len(current_segment.matches),
            "wins": current_segment.wins,
            "losses": current_segment.losses,
            "start_mmr": start_mmr,
            "average_confidence": sum(confidences) / len(confidences),
            "last_confidence": confidences[-1],
            "inherited_change": inherited_change,
            "endpoint_source": endpoint_source,
            "endpoint_factor": endpoint_factor,
            "actual_current_mmr": current_mmr,
            "best_guess_change": best_guess_change,
            "best_guess_mmr": start_mmr + best_guess_change,
            "scenario_change_low": conservative_factor * inherited_change,
            "scenario_change_high": aggressive_factor * inherited_change,
            "scenario_mmr_low": start_mmr + conservative_factor * inherited_change,
            "scenario_mmr_high": start_mmr + aggressive_factor * inherited_change,
        }

    return {
        "calibration_factor": calibration,
        "calibration_factor_low_scenario": 1.0,
        "calibration_factor_high_scenario": max(finite_ratios, default=1.0),
        "symmetric_delta_median": statistics.median(symmetric_delta_basis),
        "symmetric_delta_observed_min": min(symmetric_delta_basis),
        "symmetric_delta_observed_max": max(symmetric_delta_basis),
        "recommended_low_confidence_win_delta": statistics.median(
            symmetric_delta_basis
        ),
        "recommended_low_confidence_loss_delta": -statistics.median(
            symmetric_delta_basis
        ),
        "recommended_model": (
            "constant symmetric delta below 30%; available segment endpoints do not "
            "identify a reliable within-regime Confidence slope"
        ),
        "symmetric_delta_caveat": (
            "observed segment change divided by net wins; absorbs Double Down, "
            "team expectation and win/loss asymmetry"
        ),
        "win_at_30pct": calibration * (win_intercept + 0.70 * win_slope),
        "loss_at_30pct": calibration * (loss_intercept + 0.70 * loss_slope),
        "win_at_0pct": calibration * (win_intercept + win_slope),
        "loss_at_0pct": calibration * (loss_intercept + loss_slope),
        "segment_mae": (
            sum(abs(value) for value in residuals) / len(residuals)
            if residuals
            else None
        ),
        "segments": candidates,
        "current_projection": current_projection,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--account-id", type=int, default=136_619_313)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument(
        "--gc-history-json",
        type=Path,
        help="Unified GcRankProbe schema-v3 output with raw Match History and Current Rank.",
    )
    source.add_argument(
        "--showmmr-csv",
        type=Path,
        help="Legacy ShowMMR visible-MMR CSV; OpenDota supplies the missing ranked matches.",
    )
    parser.add_argument("--limit", type=int, default=5_000)
    parser.add_argument(
        "--gc-probe-json",
        type=Path,
        help="Current Rank output from GcRankProbe; supplies exact MMR and uncertainty anchors.",
    )
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--json", action="store_true")
    return parser.parse_args()


def build_match_estimates(
    timeline: list[TimelineMatch],
    segments: list[HiddenSegment],
    confidence_fit: dict[str, object],
    visible_model: dict[str, object],
    low_model: dict[str, object],
) -> list[dict[str, object]]:
    estimates = confidence_fit["estimates"]
    assert isinstance(estimates, list)
    uncertainty_estimates = confidence_fit["uncertainty_estimates"]
    assert isinstance(uncertainty_estimates, list)
    start_index = int(confidence_fit["start_index"])
    segment_by_match: dict[int, HiddenSegment] = {
        match.match_id: segment for segment in segments for match in segment.matches
    }
    segment_factor = {
        int(segment["segment"]): float(segment["calibration_ratio"])
        for segment in low_model["segments"]  # type: ignore[union-attr]
    }
    default_low_magnitude = float(low_model["symmetric_delta_median"])
    win_intercept = float(visible_model["win"]["intercept_at_100pct"])  # type: ignore[index]
    win_slope = float(visible_model["win"]["slope_per_unit_uncertainty"])  # type: ignore[index]
    loss_intercept = float(visible_model["loss"]["intercept_at_100pct"])  # type: ignore[index]
    loss_slope = float(visible_model["loss"]["slope_per_unit_uncertainty"])  # type: ignore[index]

    rows: list[dict[str, object]] = []
    curve_mmr: float | None = None
    for index, match in enumerate(timeline):
        if index < start_index:
            continue
        confidence_raw = estimates[index]
        assert confidence_raw is not None
        if match.reported is not None:
            confidence_used = max(0.30, min(1.0, confidence_raw))
            normal_magnitude = (
                win_intercept + win_slope * (1.0 - confidence_used)
                if match.won
                else loss_intercept + loss_slope * (1.0 - confidence_used)
            )
            actual_magnitude = abs(match.reported.rank_change)
            likely_double_down = (
                actual_magnitude >= 40
                and actual_magnitude / normal_magnitude >= 1.60
                and abs(actual_magnitude / 2 - normal_magnitude)
                < abs(actual_magnitude - normal_magnitude)
            )
            multiplier = 2.0 if likely_double_down else 1.0
            modeled_delta = (1.0 if match.won else -1.0) * normal_magnitude * multiplier
            curve_mmr = float(match.reported.end_mmr)
            curve_source = "GC actual"
            segment_number: int | None = None
            low_factor: float | None = None
        else:
            segment = segment_by_match[match.match_id]
            segment_number = segment.number
            confidence_used = min(0.299, max(0.0, confidence_raw))
            normal_magnitude = (
                win_intercept + win_slope * (1.0 - confidence_used)
                if match.won
                else loss_intercept + loss_slope * (1.0 - confidence_used)
            )
            low_factor = segment_factor.get(segment.number)
            if low_factor is None:
                modeled_delta = (1.0 if match.won else -1.0) * default_low_magnitude
                low_factor = default_low_magnitude / normal_magnitude
            else:
                modeled_delta = (1.0 if match.won else -1.0) * normal_magnitude * low_factor
            likely_double_down = False
            if curve_mmr is None:
                assert segment.previous_visible is not None
                assert segment.previous_visible.reported is not None
                curve_mmr = float(segment.previous_visible.reported.end_mmr)
            curve_mmr += modeled_delta
            curve_source = (
                "Hidden, endpoint constrained"
                if segment.observed_total_change is not None
                else "Hidden, projected"
            )

        rows.append(
            {
                "date_utc": match.started_at.isoformat(),
                "unix_time": int(match.started_at.timestamp()),
                "match_id": str(match.match_id),
                "result": "Win" if match.won else "Loss",
                "hero_id": match.hero_id,
                "average_rank": match.average_rank,
                "party_size": match.party_size,
                "mmr_fields_visible": match.reported is not None,
                "confidence_regime": ">=30%" if match.reported is not None else "<30% inferred",
                "uncertainty_proxy": uncertainty_estimates[index],
                "confidence_proxy": confidence_raw,
                "confidence_used": confidence_used,
                "segment": segment_number,
                "actual_start_mmr": match.reported.start_mmr if match.reported else None,
                "actual_rank_change": match.reported.rank_change if match.reported else None,
                "actual_end_mmr": match.reported.end_mmr if match.reported else None,
                "likely_double_down": likely_double_down,
                "low_regime_factor": low_factor,
                "modeled_rank_change": modeled_delta,
                "curve_mmr_after": curve_mmr,
                "curve_source": curve_source,
            }
        )
    return rows


def ranked_delta_prior(confidence: float, *, won: bool) -> float:
    """Return the fitted trajectory prior before an endpoint constraint.

    The curve is continuous at 30% and 40%. Below 30%, it follows the bounded
    engineering prior ±40 at the calibration boundary to ±120 at 0%. Those
    endpoints are intentionally conservative community/model assumptions; each
    hidden segment is subsequently forced to its observed MMR endpoint.
    """

    confidence = min(1.0, max(0.0, confidence))
    stable_magnitude = 27.0 if won else 25.0
    if confidence < 0.30:
        magnitude = 40.0 + 80.0 * (0.30 - confidence) / 0.30
    elif confidence < 0.40:
        magnitude = 40.0 + (stable_magnitude - 40.0) * (confidence - 0.30) / 0.10
    else:
        magnitude = stable_magnitude
    return magnitude if won else -magnitude


def glicko_saturating_delta_prior(uncertainty: int, *, won: bool) -> float:
    """Return the v2 monotone Glicko-shaped normal-match prior.

    The curve is anchored to the backtested stable values at U=90 and to
    magnitude 40 at the calibrated boundary U=150. Beyond the boundary it
    saturates instead of linearly extrapolating to an arbitrary magnitude.
    """

    if uncertainty <= 0:
        raise ValueError("uncertainty must be positive")
    stable_uncertainty = 90.0
    boundary_uncertainty = 150.0
    stable_magnitude = 27.0 if won else 25.0
    boundary_magnitude = 40.0
    saturation_scale = (boundary_magnitude - stable_magnitude) / (
        stable_magnitude / stable_uncertainty**2
        - boundary_magnitude / boundary_uncertainty**2
    )
    asymptote = (
        stable_magnitude
        * (stable_uncertainty**2 + saturation_scale)
        / stable_uncertainty**2
    )
    magnitude = (
        asymptote * uncertainty**2 / (uncertainty**2 + saturation_scale)
    )
    return magnitude if won else -magnitude


def _double_down_probability(
    actual_magnitude: float,
    normal_magnitude: float,
    *,
    double_down_rate: float,
    residual_sigma: float,
) -> float:
    rate = min(0.49, max(1e-6, double_down_rate))
    sigma = max(1e-6, residual_sigma)
    log_odds = math.log(rate / (1.0 - rate)) - 0.5 * (
        (actual_magnitude - 2.0 * normal_magnitude) ** 2
        - (actual_magnitude - normal_magnitude) ** 2
    ) / sigma**2
    if log_odds >= 0:
        return 1.0 / (1.0 + math.exp(-min(log_odds, 700.0)))
    odds = math.exp(max(log_odds, -700.0))
    return odds / (1.0 + odds)


def fit_double_down_mixture(
    observations: list[tuple[float, float]],
) -> dict[str, object]:
    """Fit a two-component normal/DD mixture with a shared residual scale."""

    if not observations:
        return {
            "double_down_rate": 0.05,
            "residual_sigma": 5.0,
            "observations": 0,
            "effective_double_downs": 0.0,
            "probable_double_downs": 0,
            "fallback_used": True,
        }
    if any(actual <= 0 or normal <= 0 for actual, normal in observations):
        raise ValueError("Double Down observations require positive magnitudes")

    rate = 0.05
    sigma = 5.0
    probabilities: list[float] = []
    for _ in range(200):
        probabilities = [
            _double_down_probability(
                actual,
                normal,
                double_down_rate=rate,
                residual_sigma=sigma,
            )
            for actual, normal in observations
        ]
        next_rate = min(0.30, max(0.001, statistics.mean(probabilities)))
        next_sigma = math.sqrt(
            statistics.mean(
                (1.0 - probability) * (actual - normal) ** 2
                + probability * (actual - 2.0 * normal) ** 2
                for (actual, normal), probability in zip(
                    observations, probabilities, strict=True
                )
            )
        )
        next_sigma = min(20.0, max(2.0, next_sigma))
        if abs(next_rate - rate) + abs(next_sigma - sigma) < 1e-9:
            rate, sigma = next_rate, next_sigma
            break
        rate, sigma = next_rate, next_sigma

    probabilities = [
        _double_down_probability(
            actual,
            normal,
            double_down_rate=rate,
            residual_sigma=sigma,
        )
        for actual, normal in observations
    ]
    return {
        "double_down_rate": rate,
        "residual_sigma": sigma,
        "observations": len(observations),
        "effective_double_downs": sum(probabilities),
        "probable_double_downs": sum(value >= 0.5 for value in probabilities),
        "fallback_used": False,
        "model": "Normal(base,sigma) vs Normal(2*base,sigma) fitted by EM",
    }


def _uncertainty_for_confidence(confidence: float) -> int:
    confidence = min(1.0, max(0.0, confidence))
    target_percent = int(math.floor(confidence * 100.0 + 0.5))
    candidate = min(
        range(90, 822),
        key=lambda value: (
            abs(rank_confidence_percent(value) - target_percent),
            value,
        ),
    )
    return max(151, candidate) if confidence < 0.30 else candidate


def fit_double_down_model(
    timeline: list[TimelineMatch],
    confidence_fit: dict[str, object],
) -> dict[str, object]:
    uncertainty_estimates = confidence_fit["uncertainty_estimates"]
    assert isinstance(uncertainty_estimates, list)
    start_index = int(confidence_fit["start_index"])
    observations: list[tuple[float, float]] = []
    for index, match in enumerate(timeline):
        if index < start_index or match.reported is None:
            continue
        actual_delta = match.reported.rank_change
        if actual_delta == 0 or (actual_delta > 0) != match.won:
            continue
        uncertainty = uncertainty_estimates[index]
        if uncertainty is None:
            continue
        normal_magnitude = abs(
            glicko_saturating_delta_prior(int(uncertainty), won=match.won)
        )
        observations.append((abs(actual_delta), normal_magnitude))
    model = fit_double_down_mixture(observations)
    return {
        **model,
        "normal_prior": (
            "monotone saturating Glicko-shaped magnitude anchored at U90 and U150"
        ),
    }


def infer_double_down_posterior(
    priors: list[float],
    *,
    target_change: int,
    double_down_rate: float,
    residual_sigma: float,
) -> dict[str, object]:
    """Condition per-match Double Down probabilities on an exact segment endpoint."""

    if not priors:
        if target_change != 0:
            raise ValueError("a non-zero endpoint requires at least one prior")
        return {
            "probabilities": [],
            "expected_double_downs": 0.0,
            "normal_prior_change": 0.0,
            "mixture_prior_change": 0.0,
        }
    if any(value == 0 or not math.isfinite(value) for value in priors):
        raise ValueError("Double Down priors must be finite and non-zero")

    rate = min(0.49, max(1e-6, double_down_rate))
    sigma = max(1e-6, residual_sigma)
    extras = [
        math.floor(value + 0.5) if value > 0 else math.ceil(value - 0.5)
        for value in priors
    ]
    mass: dict[int, float] = {0: 1.0}
    dd_mass: list[dict[int, float]] = []
    for index, extra in enumerate(extras):
        next_mass: dict[int, float] = {}
        next_dd_mass: list[dict[int, float]] = [
            {} for _ in range(index + 1)
        ]

        def add(target: dict[int, float], key: int, value: float) -> None:
            target[key] = target.get(key, 0.0) + value

        for total, probability in mass.items():
            add(next_mass, total, probability * (1.0 - rate))
            add(next_mass, total + extra, probability * rate)
            add(next_dd_mass[index], total + extra, probability * rate)
        for previous_index in range(index):
            for total, probability in dd_mass[previous_index].items():
                add(
                    next_dd_mass[previous_index],
                    total,
                    probability * (1.0 - rate),
                )
                add(
                    next_dd_mass[previous_index],
                    total + extra,
                    probability * rate,
                )
        mass = next_mass
        dd_mass = next_dd_mass

    normal_change = sum(priors)
    endpoint_variance = max(1.0, len(priors) * sigma**2)
    log_likelihoods = {
        total: -0.5 * (target_change - normal_change - total) ** 2 / endpoint_variance
        for total in mass
    }
    peak = max(log_likelihoods.values())
    likelihoods = {
        total: math.exp(value - peak) for total, value in log_likelihoods.items()
    }
    evidence = sum(
        probability * likelihoods[total] for total, probability in mass.items()
    )
    probabilities = [
        sum(
            probability * likelihoods[total]
            for total, probability in per_match.items()
        )
        / evidence
        for per_match in dd_mass
    ]
    mixture_change = sum(
        prior * (1.0 + probability)
        for prior, probability in zip(priors, probabilities, strict=True)
    )
    return {
        "probabilities": probabilities,
        "expected_double_downs": sum(probabilities),
        "normal_prior_change": normal_change,
        "mixture_prior_change": mixture_change,
        "endpoint_residual_after_mixture": target_change - mixture_change,
    }


def allocate_endpoint_constrained_deltas(
    priors: list[float],
    *,
    target_change: int,
    minimum_magnitude: int = 10,
    maximum_magnitude: int = 240,
) -> list[int]:
    """Find sign-preserving integer deltas that exactly sum to an MMR endpoint.

    This is an active-set least-squares projection. It changes win and loss
    magnitudes as little as possible from their priors while treating the segment
    endpoint as a hard observation.
    """

    if not priors:
        if target_change != 0:
            raise ValueError("a non-zero endpoint change requires at least one match")
        return []
    if minimum_magnitude <= 0 or maximum_magnitude < minimum_magnitude:
        raise ValueError("invalid magnitude bounds")
    if any(value == 0 or not math.isfinite(value) for value in priors):
        raise ValueError("every prior must be finite and non-zero")

    signs = [1 if value > 0 else -1 for value in priors]
    magnitudes = [abs(value) for value in priors]
    minimum_change = sum(
        minimum_magnitude if sign > 0 else -maximum_magnitude for sign in signs
    )
    maximum_change = sum(
        maximum_magnitude if sign > 0 else -minimum_magnitude for sign in signs
    )
    if not minimum_change <= target_change <= maximum_change:
        raise ValueError(
            f"endpoint change {target_change} is outside feasible sign-preserving "
            f"range {minimum_change}..{maximum_change}"
        )

    free = set(range(len(priors)))
    fixed: dict[int, float] = {}
    projected = [0.0] * len(priors)
    while free:
        fixed_change = sum(signs[index] * value for index, value in fixed.items())
        prior_change = sum(signs[index] * magnitudes[index] for index in free)
        adjustment = (target_change - fixed_change - prior_change) / len(free)

        violations: list[tuple[int, float]] = []
        for index in free:
            candidate = magnitudes[index] + adjustment * signs[index]
            if candidate < minimum_magnitude:
                violations.append((index, float(minimum_magnitude)))
            elif candidate > maximum_magnitude:
                violations.append((index, float(maximum_magnitude)))

        if not violations:
            for index in free:
                projected[index] = magnitudes[index] + adjustment * signs[index]
            break

        for index, boundary in violations:
            fixed[index] = boundary
            projected[index] = boundary
            free.remove(index)

    for index, value in fixed.items():
        projected[index] = value

    integer_magnitudes = [math.floor(value + 0.5) for value in projected]
    deltas = [sign * magnitude for sign, magnitude in zip(signs, integer_magnitudes, strict=True)]
    residual = target_change - sum(deltas)
    while residual != 0:
        direction = 1 if residual > 0 else -1
        choices: list[tuple[float, int, int]] = []
        for index, (sign, magnitude, prior) in enumerate(
            zip(signs, integer_magnitudes, magnitudes, strict=True)
        ):
            next_delta = deltas[index] + direction
            if next_delta == 0 or (1 if next_delta > 0 else -1) != sign:
                continue
            next_magnitude = abs(next_delta)
            if not minimum_magnitude <= next_magnitude <= maximum_magnitude:
                continue
            cost = (next_magnitude - prior) ** 2 - (magnitude - prior) ** 2
            choices.append((cost, index, next_magnitude))
        if not choices:
            raise ValueError("could not round constrained deltas to the exact endpoint")
        _, chosen, next_magnitude = min(choices)
        integer_magnitudes[chosen] = next_magnitude
        deltas[chosen] += direction
        residual -= direction

    return deltas


def build_endpoint_constrained_curve(
    timeline: list[TimelineMatch],
    segments: list[HiddenSegment],
    confidence_fit: dict[str, object],
    *,
    current_mmr: int | None,
    double_down_model: dict[str, object] | None = None,
) -> list[dict[str, object]]:
    estimates = confidence_fit["estimates"]
    uncertainty_estimates = confidence_fit["uncertainty_estimates"]
    assert isinstance(estimates, list)
    assert isinstance(uncertainty_estimates, list)
    double_down_rate = float(
        double_down_model["double_down_rate"] if double_down_model else 0.05
    )
    residual_sigma = float(
        double_down_model["residual_sigma"] if double_down_model else 5.0
    )
    index_by_match_id = {match.match_id: index for index, match in enumerate(timeline)}
    segment_by_first_match = {segment.matches[0].match_id: segment for segment in segments}

    rows: list[dict[str, object]] = []
    curve_mmr: int | None = None
    index = 0
    while index < len(timeline):
        match = timeline[index]
        segment = segment_by_first_match.get(match.match_id)
        if segment is not None:
            if segment.previous_visible is None or segment.previous_visible.reported is None:
                # An account can enter the Glicko era below the visibility
                # threshold, leaving the leading block with an endpoint but
                # no absolute starting MMR. Do not invent that missing anchor;
                # start the plotted curve at the first real GC rank instead.
                index += len(segment.matches)
                continue
            start_mmr = segment.previous_visible.reported.end_mmr
            if segment.next_visible is not None and segment.next_visible.reported is not None:
                endpoint_mmr = segment.next_visible.reported.start_mmr
                endpoint_source = "next_visible_match"
            elif current_mmr is not None:
                endpoint_mmr = current_mmr
                endpoint_source = "current_rank_gc"
            else:
                endpoint_mmr = None
                endpoint_source = None

            confidences: list[float] = []
            uncertainties: list[int] = []
            normal_priors: list[float] = []
            for position, hidden_match in enumerate(segment.matches):
                match_index = index_by_match_id[hidden_match.match_id]
                estimate = estimates[match_index]
                if estimate is None:
                    # The first post-Glicko hidden block predates the fitted U
                    # anchor. Use a transparent 0% -> <30% calibration ramp.
                    estimate = 0.30 * position / len(segment.matches)
                confidence = min(0.299, max(0.0, float(estimate)))
                confidences.append(confidence)
                uncertainty_estimate = uncertainty_estimates[match_index]
                uncertainty = (
                    int(uncertainty_estimate)
                    if uncertainty_estimate is not None
                    else _uncertainty_for_confidence(confidence)
                )
                uncertainties.append(uncertainty)
                normal_priors.append(
                    glicko_saturating_delta_prior(
                        uncertainty,
                        won=hidden_match.won,
                    )
                )

            target_change = endpoint_mmr - start_mmr if endpoint_mmr is not None else None
            crosses_glicko_transition = (
                segment.previous_visible.started_at < GLICKO_START
                and segment.matches[0].started_at >= GLICKO_START
            )
            if crosses_glicko_transition:
                double_down_fit = {
                    "probabilities": [0.0] * len(normal_priors),
                    "expected_double_downs": 0.0,
                }
            elif target_change is not None:
                double_down_fit = infer_double_down_posterior(
                    normal_priors,
                    target_change=target_change,
                    double_down_rate=double_down_rate,
                    residual_sigma=residual_sigma,
                )
            else:
                double_down_fit = {
                    "probabilities": [double_down_rate] * len(normal_priors),
                    "expected_double_downs": double_down_rate * len(normal_priors),
                }
            probabilities = double_down_fit["probabilities"]
            assert isinstance(probabilities, list)
            priors = [
                normal_prior * (1.0 + float(probability))
                for normal_prior, probability in zip(
                    normal_priors, probabilities, strict=True
                )
            ]
            constrained = (
                allocate_endpoint_constrained_deltas(priors, target_change=target_change)
                if target_change is not None
                else [
                    math.floor(value + 0.5) if value > 0 else math.ceil(value - 0.5)
                    for value in priors
                ]
            )
            curve_mmr = start_mmr
            for (
                hidden_match,
                confidence,
                uncertainty,
                normal_prior,
                double_down_probability,
                prior,
                delta,
            ) in zip(
                segment.matches,
                confidences,
                uncertainties,
                normal_priors,
                probabilities,
                priors,
                constrained,
                strict=True,
            ):
                match_index = index_by_match_id[hidden_match.match_id]
                before = curve_mmr
                curve_mmr += delta
                rows.append(
                    {
                        "date_utc": hidden_match.started_at.isoformat(),
                        "unix_time": int(hidden_match.started_at.timestamp()),
                        "match_id": str(hidden_match.match_id),
                        "result": "Win" if hidden_match.won else "Loss",
                        "hero_id": hidden_match.hero_id,
                        "average_rank": hidden_match.average_rank,
                        "party_size": hidden_match.party_size,
                        "mmr_fields_visible": False,
                        "confidence_regime": "<30% inferred",
                        "uncertainty_proxy": uncertainty,
                        "confidence_proxy": confidence,
                        "confidence_used": confidence,
                        "segment": segment.number,
                        "actual_start_mmr": None,
                        "actual_rank_change": None,
                        "actual_end_mmr": None,
                        "likely_double_down": double_down_probability >= 0.5,
                        "double_down_probability": double_down_probability,
                        "expected_double_down_multiplier": (
                            1.0 + double_down_probability
                        ),
                        "double_down_status": (
                            "disabled_glicko_launch"
                            if crosses_glicko_transition
                            else "probable"
                            if double_down_probability >= 0.5
                            else "possible"
                            if double_down_probability >= 0.2
                            else "unlikely"
                        ),
                        "normal_rank_change_prior": normal_prior,
                        "unconstrained_rank_change": prior,
                        "endpoint_correction": delta - prior,
                        "modeled_rank_change": delta,
                        "curve_mmr_before": before,
                        "curve_mmr_after": curve_mmr,
                        "segment_endpoint_mmr": endpoint_mmr,
                        "segment_endpoint_source": endpoint_source,
                        "curve_source": "Hidden, endpoint constrained"
                        if endpoint_mmr is not None
                        else "Hidden, unconstrained",
                    }
                )
            if endpoint_mmr is not None and curve_mmr != endpoint_mmr:
                raise AssertionError(
                    f"segment {segment.number} ended at {curve_mmr}, expected {endpoint_mmr}"
                )
            index += len(segment.matches)
            continue

        if match.reported is None:
            raise AssertionError(f"hidden match {match.match_id} was not assigned to a segment")

        if match.started_at < GLICKO_START:
            actual_delta = match.reported.rank_change
            anchor_jump = (
                match.reported.start_mmr - curve_mmr if curve_mmr is not None else 0
            )
            curve_mmr = match.reported.end_mmr
            rows.append(
                {
                    "date_utc": match.started_at.isoformat(),
                    "unix_time": int(match.started_at.timestamp()),
                    "match_id": str(match.match_id),
                    "result": "Win" if match.won else "Loss",
                    "hero_id": match.hero_id,
                    "average_rank": match.average_rank,
                    "party_size": match.party_size,
                    "mmr_fields_visible": True,
                    "confidence_regime": "single-rank pre-Glicko exact",
                    "uncertainty_proxy": None,
                    "confidence_proxy": None,
                    "confidence_used": None,
                    "segment": None,
                    "actual_start_mmr": match.reported.start_mmr,
                    "actual_rank_change": actual_delta,
                    "actual_end_mmr": match.reported.end_mmr,
                    "likely_double_down": False,
                    "double_down_probability": None,
                    "expected_double_down_multiplier": None,
                    "double_down_status": "not_applicable_pre_glicko",
                    "normal_rank_change_prior": None,
                    "unconstrained_rank_change": None,
                    "endpoint_correction": None,
                    "modeled_rank_change": actual_delta,
                    "curve_mmr_before": match.reported.start_mmr,
                    "curve_mmr_after": curve_mmr,
                    "segment_endpoint_mmr": None,
                    "segment_endpoint_source": None,
                    "anchor_jump_before": anchor_jump,
                    "curve_source": "GC actual (single-rank pre-Glicko)",
                }
            )
            index += 1
            continue

        match_index = index_by_match_id[match.match_id]
        confidence_raw = estimates[match_index]
        confidence = max(0.30, min(1.0, float(confidence_raw or 0.30)))
        uncertainty_estimate = uncertainty_estimates[match_index]
        uncertainty = (
            int(uncertainty_estimate)
            if uncertainty_estimate is not None
            else _uncertainty_for_confidence(confidence)
        )
        normal_prior = glicko_saturating_delta_prior(uncertainty, won=match.won)
        actual_delta = match.reported.rank_change
        double_down_probability = _double_down_probability(
            abs(actual_delta),
            abs(normal_prior),
            double_down_rate=double_down_rate,
            residual_sigma=residual_sigma,
        )
        prior = normal_prior * (1.0 + double_down_probability)
        likely_double_down = double_down_probability >= 0.5
        anchor_jump = (
            match.reported.start_mmr - curve_mmr if curve_mmr is not None else 0
        )
        curve_mmr = match.reported.end_mmr
        rows.append(
            {
                "date_utc": match.started_at.isoformat(),
                "unix_time": int(match.started_at.timestamp()),
                "match_id": str(match.match_id),
                "result": "Win" if match.won else "Loss",
                "hero_id": match.hero_id,
                "average_rank": match.average_rank,
                "party_size": match.party_size,
                "mmr_fields_visible": True,
                "confidence_regime": ">=30%",
                "uncertainty_proxy": uncertainty,
                "confidence_proxy": confidence_raw,
                "confidence_used": confidence,
                "segment": None,
                "actual_start_mmr": match.reported.start_mmr,
                "actual_rank_change": actual_delta,
                "actual_end_mmr": match.reported.end_mmr,
                "likely_double_down": likely_double_down,
                "double_down_probability": double_down_probability,
                "expected_double_down_multiplier": 1.0 + double_down_probability,
                "double_down_status": "probable" if likely_double_down else "unlikely",
                "normal_rank_change_prior": normal_prior,
                "unconstrained_rank_change": prior,
                "endpoint_correction": actual_delta - prior,
                "modeled_rank_change": actual_delta,
                "curve_mmr_before": match.reported.start_mmr,
                "curve_mmr_after": curve_mmr,
                "segment_endpoint_mmr": None,
                "segment_endpoint_source": None,
                "anchor_jump_before": anchor_jump,
                "curve_source": "GC actual",
            }
        )
        index += 1

    return rows


def build_complete_history_curve(
    showmmr: dict[int, ShowMmrRecord],
    post_glicko_rows: list[dict[str, object]],
) -> list[dict[str, object]]:
    """Prepend immutable pre-Glicko GC truth to the modeled Glicko-era curve."""
    historical_rows: list[dict[str, object]] = []
    for record in sorted(
        (item for item in showmmr.values() if item.started_at < GLICKO_START),
        key=lambda item: (item.started_at, item.match_id),
    ):
        result = "Win" if record.rank_change > 0 else "Loss"
        if record.rank_change == 0:
            result = "No change"
        historical_rows.append(
            {
                "date_utc": record.started_at.isoformat(),
                "unix_time": int(record.started_at.timestamp()),
                "match_id": str(record.match_id),
                "result": result,
                "hero_id": record.hero_id,
                "mmr_fields_visible": True,
                "confidence_proxy": None,
                "segment": None,
                "likely_double_down": False,
                "double_down_probability": None,
                "expected_double_down_multiplier": None,
                "double_down_status": "not_applicable",
                "normal_rank_change_prior": None,
                "unconstrained_rank_change": None,
                "endpoint_correction": None,
                "modeled_rank_change": record.rank_change,
                "curve_mmr_before": record.start_mmr,
                "curve_mmr_after": record.end_mmr,
                "segment_endpoint_mmr": None,
                "segment_endpoint_source": None,
                "anchor_jump_before": 0,
                "curve_source": "GC actual (pre-Glicko)",
            }
        )

    rows = historical_rows + [dict(row) for row in post_glicko_rows]
    previous_end: int | None = None
    for row in rows:
        before = int(row["curve_mmr_before"])
        row["anchor_jump_before"] = (
            before - previous_end if previous_end is not None else 0
        )
        previous_end = int(row["curve_mmr_after"])
    return rows


def export_analysis(
    output_dir: Path,
    summary: dict[str, object],
    match_rows: list[dict[str, object]],
    *,
    curve_rows: list[dict[str, object]] | None = None,
) -> tuple[Path, Path, Path, Path, Path, Path]:
    curve_rows = match_rows if curve_rows is None else curve_rows
    output_dir.mkdir(parents=True, exist_ok=True)
    summary_path = output_dir / "model-summary.json"
    matches_path = output_dir / "match-estimates.csv"
    curve_path = output_dir / "complete-mmr-curve.csv"
    chart_path = output_dir / "complete-mmr-curve.svg"
    segments_path = output_dir / "hidden-segments.csv"
    dataset_path = output_dir / "mmr-dataset.json"
    with summary_path.open("w", encoding="utf-8") as target:
        json.dump(summary, target, ensure_ascii=False, indent=2)
        target.write("\n")
    with matches_path.open("w", encoding="utf-8-sig", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=list(match_rows[0]))
        writer.writeheader()
        writer.writerows(match_rows)
    with dataset_path.open("w", encoding="utf-8") as target:
        json.dump(
            {
                "schema_version": 1,
                "account_id": summary["account_id"],
                "model_version": summary["model_version"],
                "input_source": summary["input_source"],
                "curve_start_utc": summary.get("curve_start_utc"),
                "glicko_start_utc": summary.get("glicko_start_utc"),
                "rows": match_rows,
            },
            target,
            ensure_ascii=False,
            separators=(",", ":"),
        )
        target.write("\n")
    curve_fields = [
        "date_utc",
        "unix_time",
        "match_id",
        "result",
        "hero_id",
        "mmr_fields_visible",
        "confidence_proxy",
        "segment",
        "double_down_probability",
        "expected_double_down_multiplier",
        "double_down_status",
        "normal_rank_change_prior",
        "unconstrained_rank_change",
        "endpoint_correction",
        "modeled_rank_change",
        "curve_mmr_before",
        "curve_mmr_after",
        "segment_endpoint_mmr",
        "segment_endpoint_source",
        "anchor_jump_before",
        "curve_source",
    ]
    with curve_path.open("w", encoding="utf-8-sig", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=curve_fields)
        writer.writeheader()
        writer.writerows(
            {field: row.get(field) for field in curve_fields} for row in curve_rows
        )
    write_reconstruction_svg(
        rows=curve_rows,
        destination=chart_path,
        account_id=int(summary["account_id"]),
    )
    hidden_segments = summary["hidden_segments"]
    assert isinstance(hidden_segments, list)
    with segments_path.open("w", encoding="utf-8-sig", newline="") as target:
        segment_fields = (
            list(hidden_segments[0])
            if hidden_segments
            else [
                "number",
                "start",
                "end",
                "matches",
                "wins",
                "losses",
                "observed_total_change",
                "endpoint_source",
                "previous_visible_end_mmr",
                "next_visible_start_mmr",
            ]
        )
        writer = csv.DictWriter(target, fieldnames=segment_fields)
        writer.writeheader()
        writer.writerows(hidden_segments)
    return (
        summary_path,
        matches_path,
        curve_path,
        chart_path,
        segments_path,
        dataset_path,
    )


def summarize_recommended_delta_model(
    match_rows: list[dict[str, object]],
    low_model: dict[str, object],
) -> dict[str, object]:
    confidence_bins = ((0.30, 0.40), (0.40, 0.60), (0.60, 0.80), (0.80, 1.01))
    bin_rows: list[dict[str, object]] = []
    for result in ("Win", "Loss"):
        for lower, upper in confidence_bins:
            magnitudes = sorted(
                abs(int(row["actual_rank_change"]))
                for row in match_rows
                if row["mmr_fields_visible"] is True
                and row["confidence_used"] is not None
                and row["likely_double_down"] is False
                and row["result"] == result
                and lower <= float(row["confidence_used"]) < upper
            )
            if not magnitudes:
                continue
            bin_rows.append(
                {
                    "result": result,
                    "confidence_lower": lower,
                    "confidence_upper_exclusive": upper,
                    "observations": len(magnitudes),
                    "median_magnitude": statistics.median(magnitudes),
                    "minimum": min(magnitudes),
                    "maximum": max(magnitudes),
                }
            )

    return {
        "at_or_above_30pct": {
            "30_to_39pct": {"win": 40, "loss": -40},
            "40_to_100pct": {"win": 27, "loss": -25},
            "basis": "rounded medians from the user's visible, non-Double-Down CSV bins",
        },
        "below_30pct": {
            "win": low_model["recommended_low_confidence_win_delta"],
            "loss": low_model["recommended_low_confidence_loss_delta"],
            "observed_endpoint_equivalent_range": [
                low_model["symmetric_delta_observed_min"],
                low_model["symmetric_delta_observed_max"],
            ],
            "basis": low_model["recommended_model"],
        },
        "visible_bin_diagnostics": bin_rows,
        "caveat": (
            "Per-match team expectation and hidden Double Down choices are unavailable; "
            "these are trajectory defaults, not Valve's server formula."
        ),
    }


def main() -> int:
    args = parse_args()
    showmmr: dict[int, ShowMmrRecord] = {}
    gc_probe_path = args.gc_probe_json or args.gc_history_json
    gc_anchor = (
        load_gc_probe_anchor(gc_probe_path, expected_account_id=args.account_id)
        if gc_probe_path is not None
        else None
    )
    if args.gc_history_json is not None:
        timeline = load_gc_match_history_timeline(
            args.gc_history_json,
            expected_account_id=args.account_id,
        )
        input_source = "authenticated_gc_match_history"
    else:
        load_dotenv()
        api_key = os.environ.get("OPENDOTA_API_KEY", "")
        with OpenDotaClient(api_key) as client:
            raw_matches = client.get_ranked_matches(args.account_id, limit=args.limit)

        showmmr = load_showmmr_csv(args.showmmr_csv)
        timeline = build_timeline(raw_matches, showmmr)
        input_source = "showmmr_plus_opendota"
    segments = find_hidden_segments(timeline)
    confidence_fit = fit_confidence_proxy(
        timeline,
        current_base_uncertainty=(
            gc_anchor.base_uncertainty if gc_anchor is not None else None
        ),
    )
    confidence_estimates = confidence_fit["estimates"]
    assert isinstance(confidence_estimates, list)
    visible_delta_model = fit_visible_delta_model(
        timeline,
        confidence_estimates,
        start_index=int(confidence_fit["start_index"]),
    )
    double_down_model = fit_double_down_model(timeline, confidence_fit)
    low_delta_model = fit_low_confidence_model(
        segments,
        timeline,
        confidence_estimates,
        visible_delta_model,
        current_mmr=gc_anchor.current_mmr if gc_anchor is not None else None,
    )
    visible = [match for match in timeline if match.reported is not None]
    single_rank_pre_glicko = [
        match for match in timeline if match.started_at < GLICKO_START
    ]
    glicko_matches = [
        match for match in timeline if match.started_at >= GLICKO_START
    ]
    mismatched_results = [
        match
        for match in visible
        if (match.reported.rank_change > 0) != match.won  # type: ignore[union-attr]
    ]
    delta_counts: dict[int, int] = {}
    for match in visible:
        if match.started_at < GLICKO_START:
            continue
        delta = match.reported.rank_change  # type: ignore[union-attr]
        delta_counts[delta] = delta_counts.get(delta, 0) + 1

    result: dict[str, object] = {
        "account_id": args.account_id,
        "model_version": MODEL_VERSION,
        "input_source": input_source,
        "curve_start_utc": SINGLE_RANK_START.isoformat(),
        "glicko_start_utc": GLICKO_START.isoformat(),
        "timeline_matches": len(timeline),
        "single_rank_pre_glicko_matches": len(single_rank_pre_glicko),
        "glicko_matches": len(glicko_matches),
        "visible_matches": len(visible),
        "hidden_matches": len(timeline) - len(visible),
        "result_sign_mismatches": len(mismatched_results),
        "gc_probe_anchor": asdict(gc_anchor) if gc_anchor is not None else None,
        "confidence_proxy": {
            key: value
            for key, value in confidence_fit.items()
            if key not in {"estimates", "uncertainty_estimates"}
        },
        "visible_delta_model": visible_delta_model,
        "double_down_mixture": double_down_model,
        "low_delta_model": low_delta_model,
        "delta_counts": dict(sorted(delta_counts.items())),
        "hidden_segments": [
            {
                "number": segment.number,
                "start": segment.matches[0].started_at.isoformat(),
                "end": segment.matches[-1].started_at.isoformat(),
                "matches": len(segment.matches),
                "wins": segment.wins,
                "losses": segment.losses,
                "observed_total_change": (
                    segment.observed_total_change
                    if segment.observed_total_change is not None
                    else (
                        gc_anchor.current_mmr
                        - segment.previous_visible.reported.end_mmr
                        if gc_anchor is not None
                        and segment.next_visible is None
                        and segment.previous_visible is not None
                        and segment.previous_visible.reported is not None
                        else None
                    )
                ),
                "endpoint_source": (
                    "next_visible_match"
                    if segment.observed_total_change is not None
                    else (
                        "current_rank_gc"
                        if gc_anchor is not None and segment.next_visible is None
                        else None
                    )
                ),
                "previous_visible_end_mmr": (
                    segment.previous_visible.reported.end_mmr
                    if segment.previous_visible and segment.previous_visible.reported
                    else None
                ),
                "next_visible_start_mmr": (
                    segment.next_visible.reported.start_mmr
                    if segment.next_visible and segment.next_visible.reported
                    else None
                ),
                "crosses_glicko_transition": (
                    segment.previous_visible is not None
                    and segment.previous_visible.started_at < GLICKO_START
                    and segment.matches[0].started_at >= GLICKO_START
                ),
            }
            for segment in segments
        ],
    }
    match_estimates = build_endpoint_constrained_curve(
        timeline,
        segments,
        confidence_fit,
        current_mmr=gc_anchor.current_mmr if gc_anchor is not None else None,
        double_down_model=double_down_model,
    )
    complete_curve = [dict(row) for row in match_estimates]
    pre_glicko_actual_count = sum(
        row["mmr_fields_visible"] is True
        and datetime.fromisoformat(str(row["date_utc"])) < GLICKO_START
        for row in complete_curve
    )
    actual_rows = [row for row in match_estimates if row["mmr_fields_visible"]]
    modeled_rows = [row for row in match_estimates if not row["mmr_fields_visible"]]
    hidden_segment_summaries = result["hidden_segments"]
    assert isinstance(hidden_segment_summaries, list)
    for segment_summary in hidden_segment_summaries:
        assert isinstance(segment_summary, dict)
        segment_rows = [
            row
            for row in modeled_rows
            if row["segment"] == segment_summary["number"]
        ]
        segment_summary["expected_double_downs"] = sum(
            float(row["double_down_probability"] or 0.0) for row in segment_rows
        )
        segment_summary["probable_double_downs"] = sum(
            bool(row["likely_double_down"]) for row in segment_rows
        )
        segment_summary["normal_prior_change"] = sum(
            float(row["normal_rank_change_prior"] or 0.0) for row in segment_rows
        )
        segment_summary["mixture_prior_change"] = sum(
            float(row["unconstrained_rank_change"] or 0.0) for row in segment_rows
        )
        segment_summary["endpoint_correction_after_mixture"] = sum(
            float(row["endpoint_correction"] or 0.0) for row in segment_rows
        )
    endpoint_residuals: list[dict[str, object]] = []
    for segment_number in sorted(
        {
            int(row["segment"])
            for row in modeled_rows
            if row["segment"] is not None
        }
    ):
        segment_rows = [
            row for row in modeled_rows if row["segment"] == segment_number
        ]
        last_row = segment_rows[-1]
        endpoint = int(last_row["segment_endpoint_mmr"])
        endpoint_residuals.append(
            {
                "segment": segment_number,
                "endpoint_mmr": endpoint,
                "endpoint_source": last_row["segment_endpoint_source"],
                "reconstructed_end_mmr": int(last_row["curve_mmr_after"]),
                "residual": int(last_row["curve_mmr_after"]) - endpoint,
            }
        )
    anchor_anomalies = [
        {
            "match_id": row["match_id"],
            "date_utc": row["date_utc"],
            "anchor_jump_before": row["anchor_jump_before"],
        }
        for row in actual_rows
        if row["anchor_jump_before"] != 0
    ]
    unanchored_segments = [
        segment
        for segment in segments
        if segment.previous_visible is None or segment.previous_visible.reported is None
    ]
    result["curve_reconstruction"] = {
        "matches": len(complete_curve),
        "pre_glicko_actual_matches": pre_glicko_actual_count,
        "post_glicko_matches": len(match_estimates) - pre_glicko_actual_count,
        "actual_gc_matches": len(actual_rows),
        "endpoint_constrained_matches": len(modeled_rows),
        "unanchored_hidden_matches_excluded": sum(
            len(segment.matches) for segment in unanchored_segments
        ),
        "unanchored_hidden_segments_excluded": [
            {
                "segment": segment.number,
                "matches": len(segment.matches),
                "start": segment.matches[0].started_at.isoformat(),
                "end": segment.matches[-1].started_at.isoformat(),
                "next_visible_start_mmr": (
                    segment.next_visible.reported.start_mmr
                    if segment.next_visible is not None
                    and segment.next_visible.reported is not None
                    else None
                ),
                "reason": "no_previous_visible_mmr_anchor",
            }
            for segment in unanchored_segments
        ],
        "hidden_segments": len(endpoint_residuals),
        "endpoint_residuals": endpoint_residuals,
        "all_hidden_endpoints_exact": all(
            item["residual"] == 0 for item in endpoint_residuals
        ),
        "visible_settlement_order_anomalies": anchor_anomalies,
        "method": (
            "Per hidden segment, use a monotone saturating Glicko-shaped normal "
            "prior, condition latent Double Down probabilities on the exact endpoint, "
            "then apply bounded least squares; preserve every result sign and force "
            "integer deltas to sum exactly to the observed endpoint."
        ),
    }
    result["recommended_delta_model"] = summarize_recommended_delta_model(
        match_estimates,
        low_delta_model,
    )
    result["production_delta_model"] = {
        "normal_prior": (
            "monotone saturating Glicko-shaped curve over raw uncertainty U"
        ),
        "double_down_rate": double_down_model["double_down_rate"],
        "double_down_residual_sigma": double_down_model["residual_sigma"],
        "hidden_conditioning": (
            "posterior Double Down probabilities plus exact endpoint projection"
        ),
        "caveat": (
            "team expected win probability remains latent; this is a reconstruction "
            "model, not Valve's server formula"
        ),
    }
    result["model_formulas"] = {
        "uncertainty_before": (
            "Dota client float32 projection of previous uncertainty_after using "
            "elapsed seconds, coefficient=0.3, reference=250, divisor=80, floor=90"
        ),
        "uncertainty_after": (
            "round(1/sqrt(1/uncertainty_before^2 + "
            f"{confidence_fit['information_gain_per_match']:.8g})) "
            "clamped to >=90 [Glicko-inspired fitted proxy]"
        ),
        "display_confidence": "client.dll build 6907 piecewise quadratic mapping from U",
        "normal_rank_change": (
            "A*U^2/(U^2+B), separately anchored to win +27/loss -25 at U=90 "
            "and magnitude 40 at U=150; monotone and saturating beyond calibration"
        ),
        "double_down_mixture": (
            "EM-fitted Normal(base,sigma) vs Normal(2*base,sigma); hidden per-match "
            "probabilities are conditioned jointly on the exact segment endpoint"
        ),
        "hidden_segment_constraint": (
            "bounded least-squares correction of signed per-match priors, followed "
            "by integer apportionment so every hidden segment ends on its exact MMR anchor"
        ),
        "double_down": (
            "use posterior expected multiplier 1+P(Double Down), never a >40 hard label"
        ),
    }
    if args.output_dir is not None:
        output_paths = export_analysis(
            args.output_dir,
            result,
            match_estimates,
            curve_rows=complete_curve,
        )
        result["output_paths"] = [str(path.resolve()) for path in output_paths]
    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        projection = low_delta_model["current_projection"]
        print(
            f"Single-rank-era matches: {len(timeline)} "
            f"({len(visible)} visible / {len(timeline) - len(visible)} hidden)"
        )
        print(
            "Uncertainty proxy: exact client inactivity projection; fitted "
            "per-match precision gain "
            f"{confidence_fit['information_gain_per_match']:.8g}"
        )
        if gc_anchor is not None:
            print(
                "Current GC anchor: "
                f"MMR {gc_anchor.current_mmr}, U {gc_anchor.base_uncertainty}, "
                f"Confidence {gc_anchor.confidence_percent}%"
            )
        if gc_anchor is None and isinstance(projection, dict):
            print(
                "Current projection: "
                f"{projection['start_mmr']:.0f} -> {projection['best_guess_mmr']:.0f} "
                f"(scenario {projection['scenario_mmr_low']:.0f}.."
                f"{projection['scenario_mmr_high']:.0f})"
            )
        current_segment = next(
            (
                item
                for item in endpoint_residuals
                if item["endpoint_source"] == "current_rank_gc"
            ),
            None,
        )
        if gc_anchor is not None and current_segment is not None:
            print(
                "Current hidden segment: exact GC endpoint "
                f"{current_segment['endpoint_mmr']} "
                f"(reconstruction residual {current_segment['residual']:+d})"
            )
        elif gc_anchor is not None:
            print("No hidden current segment; the latest curve point is GC-reported.")
        if args.output_dir is not None:
            for path in output_paths:
                print(path.resolve())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
