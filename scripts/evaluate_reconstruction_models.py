from __future__ import annotations

import argparse
import csv
import json
import math
import statistics
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

if __package__:
    from scripts.analyze_confidence_model import (
        allocate_endpoint_constrained_deltas,
        ranked_delta_prior,
    )
else:
    from analyze_confidence_model import (
        allocate_endpoint_constrained_deltas,
        ranked_delta_prior,
    )


@dataclass(frozen=True, slots=True)
class ActualMatch:
    match_id: str
    started_at: datetime
    won: bool
    delta: int
    start_mmr: int
    end_mmr: int
    confidence: float
    uncertainty: int
    likely_double_down: bool


@dataclass(frozen=True, slots=True)
class BacktestBlock:
    matches: tuple[ActualMatch, ...]

    @property
    def target_change(self) -> int:
        return sum(match.delta for match in self.matches)

    @property
    def contains_double_down(self) -> bool:
        return any(match.likely_double_down for match in self.matches)


def load_contiguous_actual_runs(path: Path) -> list[list[ActualMatch]]:
    runs: list[list[ActualMatch]] = []
    current: list[ActualMatch] = []
    with path.open(encoding="utf-8-sig", newline="") as source:
        for row in csv.DictReader(source):
            if row["mmr_fields_visible"].lower() != "true":
                if current:
                    runs.append(current)
                    current = []
                continue

            # Pre-Glicko single-rank rows are exact MMR truth, but they have no
            # Confidence state and therefore are outside this model's domain.
            if not row.get("confidence_used"):
                if current:
                    runs.append(current)
                    current = []
                continue

            delta = int(row["actual_rank_change"])
            won = row["result"] == "Win"
            sign_matches = (delta > 0) == won and delta != 0
            start_mmr = int(row["actual_start_mmr"])
            anchor_jump = int(row["anchor_jump_before"] or 0)
            if (
                not sign_matches
                or anchor_jump != 0
                or (current and start_mmr != current[-1].end_mmr)
            ):
                if current:
                    runs.append(current)
                    current = []
                if not sign_matches or anchor_jump != 0:
                    continue

            current.append(
                ActualMatch(
                    match_id=row["match_id"],
                    started_at=datetime.fromisoformat(row["date_utc"]),
                    won=won,
                    delta=delta,
                    start_mmr=start_mmr,
                    end_mmr=int(row["actual_end_mmr"]),
                    confidence=float(row["confidence_used"]),
                    uncertainty=min(150, int(row["uncertainty_proxy"] or 150)),
                    likely_double_down=row["likely_double_down"].lower() == "true",
                )
            )
    if current:
        runs.append(current)
    return runs


def make_blocks(
    runs: list[list[ActualMatch]],
    *,
    lengths: tuple[int, ...],
) -> list[BacktestBlock]:
    return [
        BacktestBlock(tuple(run[start : start + length]))
        for run in runs
        for length in lengths
        for start in range(len(run) - length + 1)
    ]


def infer_double_down_multipliers(
    priors: list[float],
    *,
    target_change: int,
    penalty: float,
) -> list[int]:
    rounded = [math.floor(value + 0.5) if value > 0 else math.ceil(value - 0.5) for value in priors]
    residual = target_change - sum(rounded)
    states: dict[int, tuple[int, int]] = {0: (0, 0)}
    for index, extra in enumerate(rounded):
        updated = dict(states)
        for total_extra, (count, mask) in states.items():
            candidate_extra = total_extra + extra
            candidate = (count + 1, mask | (1 << index))
            existing = updated.get(candidate_extra)
            if existing is None or candidate[0] < existing[0]:
                updated[candidate_extra] = candidate
        states = updated

    _, (_, mask) = min(
        states.items(),
        key=lambda item: (
            (residual - item[0]) ** 2 / len(priors) + penalty * item[1][0],
            item[1][0],
            abs(residual - item[0]),
        ),
    )
    return [2 if mask & (1 << index) else 1 for index in range(len(priors))]


def allocate_weighted_endpoint_deltas(
    priors: list[float],
    *,
    target_change: int,
    variances: list[float],
    minimum_magnitude: int = 10,
    maximum_magnitude: int = 240,
) -> list[int]:
    if len(priors) != len(variances) or any(value <= 0 for value in variances):
        raise ValueError("every prior needs a positive endpoint variance")
    signs = [1 if value > 0 else -1 for value in priors]
    lower = [minimum_magnitude if sign > 0 else -maximum_magnitude for sign in signs]
    upper = [maximum_magnitude if sign > 0 else -minimum_magnitude for sign in signs]
    minimum_change = sum(lower)
    maximum_change = sum(upper)
    if not minimum_change <= target_change <= maximum_change:
        raise ValueError("endpoint is outside the weighted sign-preserving range")

    free = set(range(len(priors)))
    projected = [0.0] * len(priors)
    while free:
        fixed_change = sum(projected[index] for index in range(len(priors)) if index not in free)
        prior_change = sum(priors[index] for index in free)
        total_variance = sum(variances[index] for index in free)
        multiplier = (target_change - fixed_change - prior_change) / total_variance
        violations: list[tuple[int, float]] = []
        for index in free:
            candidate = priors[index] + multiplier * variances[index]
            if candidate < lower[index]:
                violations.append((index, float(lower[index])))
            elif candidate > upper[index]:
                violations.append((index, float(upper[index])))
            else:
                projected[index] = candidate
        if not violations:
            break
        for index, boundary in violations:
            projected[index] = boundary
            free.remove(index)

    deltas = [math.floor(value + 0.5) for value in projected]
    residual = target_change - sum(deltas)
    while residual != 0:
        direction = 1 if residual > 0 else -1
        choices: list[tuple[float, int]] = []
        for index, value in enumerate(deltas):
            candidate = value + direction
            if not lower[index] <= candidate <= upper[index]:
                continue
            cost = (
                (candidate - priors[index]) ** 2 - (value - priors[index]) ** 2
            ) / variances[index]
            choices.append((cost, index))
        if not choices:
            raise ValueError("could not round the weighted endpoint allocation")
        _, chosen = min(choices)
        deltas[chosen] += direction
        residual -= direction
    return deltas


def candidate_priors(
    block: BacktestBlock,
    *,
    model: str,
    double_down_penalty: float | None = None,
) -> list[float]:
    if model == "fixed_symmetric_25":
        return [25.0 if match.won else -25.0 for match in block.matches]
    if model == "stable_asymmetric_27_25":
        return [27.0 if match.won else -25.0 for match in block.matches]
    if model == "glicko_saturating":
        values: list[float] = []
        boundary_magnitude = 40.0
        boundary_uncertainty = 150.0
        stable_uncertainty = 90.0
        for match in block.matches:
            stable_magnitude = 27.0 if match.won else 25.0
            saturation_scale = (boundary_magnitude - stable_magnitude) / (
                stable_magnitude / stable_uncertainty**2
                - boundary_magnitude / boundary_uncertainty**2
            )
            asymptote = stable_magnitude * (
                stable_uncertainty**2 + saturation_scale
            ) / stable_uncertainty**2
            magnitude = (
                asymptote
                * match.uncertainty**2
                / (match.uncertainty**2 + saturation_scale)
            )
            values.append(magnitude if match.won else -magnitude)
        return values

    priors = [
        ranked_delta_prior(match.confidence, won=match.won) for match in block.matches
    ]
    if model in {"confidence_v1", "confidence_weighted"}:
        return priors
    if model == "oracle_double_down":
        return [
            prior * (2 if match.likely_double_down else 1)
            for prior, match in zip(priors, block.matches, strict=True)
        ]
    if model == "latent_double_down":
        if double_down_penalty is None:
            raise ValueError("latent_double_down requires a penalty")
        multipliers = infer_double_down_multipliers(
            priors,
            target_change=block.target_change,
            penalty=double_down_penalty,
        )
        return [
            prior * multiplier
            for prior, multiplier in zip(priors, multipliers, strict=True)
        ]
    raise ValueError(f"unknown candidate model: {model}")


def evaluate_blocks(
    blocks: list[BacktestBlock],
    *,
    model: str,
    double_down_penalty: float | None = None,
    endpoint_variance_alpha: float | None = None,
) -> dict[str, float | int]:
    delta_errors: list[float] = []
    path_errors: list[float] = []
    maximum_path_errors: list[float] = []
    inferred_double_downs = 0
    for block in blocks:
        priors = candidate_priors(
            block,
            model=model,
            double_down_penalty=double_down_penalty,
        )
        if model == "latent_double_down":
            inferred_double_downs += sum(
                multiplier == 2
                for multiplier in infer_double_down_multipliers(
                    [
                        ranked_delta_prior(match.confidence, won=match.won)
                        for match in block.matches
                    ],
                    target_change=block.target_change,
                    penalty=double_down_penalty or 0.0,
                )
            )
        if model == "confidence_weighted":
            if endpoint_variance_alpha is None:
                raise ValueError("confidence_weighted requires endpoint_variance_alpha")
            predicted = allocate_weighted_endpoint_deltas(
                priors,
                target_change=block.target_change,
                variances=[
                    1.0 + endpoint_variance_alpha * (1.0 - match.confidence)
                    for match in block.matches
                ],
            )
        else:
            predicted = allocate_endpoint_constrained_deltas(
                priors,
                target_change=block.target_change,
            )
        actual_path = 0
        predicted_path = 0
        block_path_errors: list[float] = []
        for match, estimate in zip(block.matches, predicted, strict=True):
            delta_errors.append(abs(estimate - match.delta))
            actual_path += match.delta
            predicted_path += estimate
            block_path_errors.append(abs(predicted_path - actual_path))
        path_errors.extend(block_path_errors[:-1])
        maximum_path_errors.append(max(block_path_errors, default=0.0))

    return {
        "blocks": len(blocks),
        "matches_scored": sum(len(block.matches) for block in blocks),
        "delta_mae": statistics.mean(delta_errors),
        "delta_median_absolute_error": statistics.median(delta_errors),
        "intermediate_path_mae": statistics.mean(path_errors) if path_errors else 0.0,
        "block_max_path_error_mean": statistics.mean(maximum_path_errors),
        "inferred_double_down_assignments": inferred_double_downs,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--match-estimates-csv", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    lengths = (3, 9, 16, 18, 23)
    runs = load_contiguous_actual_runs(args.match_estimates_csv)
    blocks = make_blocks(runs, lengths=lengths)
    if not blocks:
        raise ValueError("no contiguous visible blocks were available for backtesting")

    split_time = sorted(block.matches[-1].started_at for block in blocks)[
        math.floor(len(blocks) * 0.70)
    ]
    training = [block for block in blocks if block.matches[-1].started_at <= split_time]
    testing = [block for block in blocks if block.matches[-1].started_at > split_time]
    penalties = (0.0, 25.0, 50.0, 100.0, 200.0, 400.0, 800.0)
    training_penalties = {
        penalty: evaluate_blocks(
            training,
            model="latent_double_down",
            double_down_penalty=penalty,
        )
        for penalty in penalties
    }
    best_penalty = min(
        penalties,
        key=lambda penalty: (
            float(training_penalties[penalty]["intermediate_path_mae"]),
            penalty,
        ),
    )
    variance_alphas = (0.5, 1.0, 2.0, 4.0, 8.0, 16.0)
    training_variance_grid = {
        alpha: evaluate_blocks(
            training,
            model="confidence_weighted",
            endpoint_variance_alpha=alpha,
        )
        for alpha in variance_alphas
    }
    best_variance_alpha = min(
        variance_alphas,
        key=lambda alpha: (
            float(training_variance_grid[alpha]["intermediate_path_mae"]),
            alpha,
        ),
    )

    models = {
        model: evaluate_blocks(testing, model=model)
        for model in (
            "fixed_symmetric_25",
            "stable_asymmetric_27_25",
            "glicko_saturating",
            "confidence_v1",
            "oracle_double_down",
        )
    }
    models["latent_double_down"] = evaluate_blocks(
        testing,
        model="latent_double_down",
        double_down_penalty=best_penalty,
    )
    models["confidence_weighted"] = evaluate_blocks(
        testing,
        model="confidence_weighted",
        endpoint_variance_alpha=best_variance_alpha,
    )
    no_double_down_testing = [block for block in testing if not block.contains_double_down]
    double_down_testing = [block for block in testing if block.contains_double_down]
    payload = {
        "source": str(args.match_estimates_csv.resolve()),
        "window_lengths": lengths,
        "contiguous_runs": len(runs),
        "blocks": len(blocks),
        "time_split": split_time.isoformat(),
        "training_blocks": len(training),
        "testing_blocks": len(testing),
        "testing_blocks_with_known_double_down": len(double_down_testing),
        "selected_latent_double_down_penalty": best_penalty,
        "selected_endpoint_variance_alpha": best_variance_alpha,
        "training_penalty_grid": training_penalties,
        "training_endpoint_variance_grid": training_variance_grid,
        "test_models": models,
        "test_by_window_length": {
            str(length): {
                "confidence_v1": evaluate_blocks(
                    [block for block in testing if len(block.matches) == length],
                    model="confidence_v1",
                ),
                "confidence_weighted": evaluate_blocks(
                    [block for block in testing if len(block.matches) == length],
                    model="confidence_weighted",
                    endpoint_variance_alpha=best_variance_alpha,
                ),
                "oracle_double_down": evaluate_blocks(
                    [block for block in testing if len(block.matches) == length],
                    model="oracle_double_down",
                ),
            }
            for length in lengths
            if any(len(block.matches) == length for block in testing)
        },
        "test_without_known_double_down": {
            model: evaluate_blocks(no_double_down_testing, model=model)
            for model in (
                "fixed_symmetric_25",
                "stable_asymmetric_27_25",
                "glicko_saturating",
                "confidence_v1",
            )
        },
        "test_with_known_double_down": {
            model: evaluate_blocks(
                double_down_testing,
                model=model,
                double_down_penalty=(
                    best_penalty if model == "latent_double_down" else None
                ),
            )
            for model in (
                "confidence_v1",
                "latent_double_down",
                "oracle_double_down",
            )
        },
        "caveats": [
            "Sliding windows overlap, so block observations are not independent.",
            "Visible >=30% blocks are a proxy task, not direct low-Confidence ground truth.",
            "Oracle Double Down uses hidden labels and is only an upper bound.",
            "Every candidate receives the exact endpoint; metrics score the interior path.",
        ],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(args.output.resolve())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
