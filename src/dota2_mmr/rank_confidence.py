"""Reproduce the current Dota 2 client's Rank Confidence display calculation.

This is not a Valve-published API contract. The field mapping and arithmetic were
recovered from the local Dota 2 client.dll build 6907 on 2026-08-21. Keeping the
calculation here lets GC observations retain their raw values while producing the
same derived display percentage as that client build.
"""

from __future__ import annotations

import math
import struct
from dataclasses import dataclass

CLIENT_DLL_BUILD = 6907
CLIENT_DLL_SHA256 = "DF9A8F89BB43E98FC1C0795D6A84B2B1AB49531B0811032ADAF8E980F766A5E0"
CALIBRATED_MAX_UNCERTAINTY = 150


@dataclass(frozen=True, slots=True)
class RankConfidenceState:
    """A current Rank response interpreted at a specific Unix timestamp."""

    base_uncertainty: int
    time_base_unix: int
    observed_at_unix: int
    elapsed_seconds: int
    projected_uncertainty: int
    confidence_percent: int
    calibrated: bool


def _float32(value: float) -> float:
    """Round a Python float to IEEE-754 binary32 after each client operation."""

    return struct.unpack("=f", struct.pack("=f", value))[0]


def _require_nonnegative_integer(name: str, value: int) -> None:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ValueError(f"{name} must be a non-negative integer")


def project_rank_uncertainty(
    base_uncertainty: int,
    *,
    time_base_unix: int,
    now_unix: int,
) -> int:
    """Project GC ``rank_data1`` from its ``rank_data3`` time base.

    The operation order intentionally mirrors the client's scalar float32 code.
    Algebraically, the variance growth is approximately ``204 * elapsed_days``,
    but using that collapsed expression can move a result across a rounding edge.
    """

    _require_nonnegative_integer("base_uncertainty", base_uncertainty)
    _require_nonnegative_integer("time_base_unix", time_base_unix)
    _require_nonnegative_integer("now_unix", now_unix)

    if time_base_unix == 0 or now_unix <= time_base_unix:
        return base_uncertainty

    elapsed_seconds = now_unix - time_base_unix
    elapsed_seconds_f32 = _float32(float(elapsed_seconds))
    elapsed_days = _float32(elapsed_seconds_f32 / _float32(86_400.0))
    time_factor = _float32(elapsed_days * _float32(0.3))
    time_factor = _float32(time_factor / _float32(80.0))
    if time_factor <= 0:
        return base_uncertainty

    base_f32 = _float32(float(base_uncertainty))
    base_variance = _float32(base_f32 * base_f32)
    reference_f32 = _float32(250.0)
    floor_f32 = _float32(90.0)
    reference_variance = _float32(reference_f32 * reference_f32)
    floor_variance = _float32(floor_f32 * floor_f32)
    variance_span = _float32(reference_variance - floor_variance)
    elapsed_variance = _float32(time_factor * variance_span)
    variance = _float32(base_variance + elapsed_variance)

    if variance < 0:
        return 90

    root = _float32(math.sqrt(variance))
    rounded = int(_float32(root + _float32(0.5)))
    return min(3000, max(90, rounded))


def rank_confidence_percent(uncertainty: int) -> int:
    """Convert projected raw uncertainty to the client's displayed percentage."""

    _require_nonnegative_integer("uncertainty", uncertainty)

    if uncertainty <= 165:
        score = 0.0056 * uncertainty * uncertainty - 2.55 * uncertainty + 286.56
        minimum, maximum = 18, 100
    elif uncertainty <= 240:
        score = 0.0016 * uncertainty * uncertainty - 0.8022 * uncertainty + 107.78
        minimum, maximum = 7, 18
    elif uncertainty <= 820:
        score = 0.0000165 * uncertainty * uncertainty - 0.0283 * uncertainty + 12.6
        minimum, maximum = 1, 7
    else:
        return 0

    # client.dll narrows the double quadratic result to float before V_roundf.
    score_f32 = _float32(score)
    rounded = math.floor(score_f32 + 0.5) if score_f32 >= 0 else math.ceil(score_f32 - 0.5)
    return min(maximum, max(minimum, rounded))


def project_rank_confidence(
    base_uncertainty: int,
    *,
    time_base_unix: int,
    now_unix: int,
) -> RankConfidenceState:
    """Interpret ``rank_data1``/``rank_data3`` as the current client does."""

    uncertainty = project_rank_uncertainty(
        base_uncertainty,
        time_base_unix=time_base_unix,
        now_unix=now_unix,
    )
    elapsed_seconds = (
        max(now_unix - time_base_unix, 0)
        if time_base_unix != 0
        else 0
    )
    return RankConfidenceState(
        base_uncertainty=base_uncertainty,
        time_base_unix=time_base_unix,
        observed_at_unix=now_unix,
        elapsed_seconds=elapsed_seconds,
        projected_uncertainty=uncertainty,
        confidence_percent=rank_confidence_percent(uncertainty),
        calibrated=uncertainty <= CALIBRATED_MAX_UNCERTAINTY,
    )
