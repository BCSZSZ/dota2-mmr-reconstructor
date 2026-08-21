import pytest

from dota2_mmr.rank_confidence import (
    project_rank_confidence,
    project_rank_uncertainty,
    rank_confidence_percent,
)


@pytest.mark.parametrize(
    ("uncertainty", "confidence"),
    [
        (90, 100),
        (150, 30),
        (151, 29),
        (165, 18),
        (166, 18),
        (240, 7),
        (241, 7),
        (820, 1),
        (821, 0),
    ],
)
def test_rank_confidence_piecewise_boundaries(uncertainty: int, confidence: int) -> None:
    assert rank_confidence_percent(uncertainty) == confidence


def test_projection_uses_fractional_days_and_crosses_calibration_threshold() -> None:
    time_base = 1_000_000

    assert project_rank_uncertainty(
        150,
        time_base_unix=time_base,
        now_unix=time_base + 43_200,
    ) == 150
    assert project_rank_uncertainty(
        150,
        time_base_unix=time_base,
        now_unix=time_base + 86_400,
    ) == 151

    state = project_rank_confidence(
        150,
        time_base_unix=time_base,
        now_unix=time_base + 86_400,
    )
    assert state.confidence_percent == 29
    assert state.calibrated is False


def test_zero_or_future_time_base_returns_unprojected_value() -> None:
    assert project_rank_uncertainty(149, time_base_unix=0, now_unix=2_000_000) == 149
    assert project_rank_uncertainty(149, time_base_unix=2_000_001, now_unix=2_000_000) == 149


@pytest.mark.parametrize("value", [-1, 1.5, True])
def test_invalid_uncertainty_is_rejected(value) -> None:
    with pytest.raises(ValueError, match="non-negative integer"):
        rank_confidence_percent(value)
