from __future__ import annotations

import json
import subprocess
import sys
from datetime import UTC, datetime
from pathlib import Path

import pytest

from scripts.analyze_confidence_model import (
    ShowMmrRecord,
    TimelineMatch,
    allocate_endpoint_constrained_deltas,
    build_complete_history_curve,
    build_endpoint_constrained_curve,
    build_timeline,
    find_hidden_segments,
    fit_double_down_mixture,
    glicko_saturating_delta_prior,
    infer_double_down_posterior,
    load_gc_match_history_timeline,
    load_gc_probe_anchor,
    ranked_delta_prior,
    summarize_recommended_delta_model,
    update_uncertainty_after_match,
)
from scripts.evaluate_reconstruction_models import (
    allocate_weighted_endpoint_deltas,
    infer_double_down_multipliers,
    load_contiguous_actual_runs,
)
from scripts.export_existing_full_history import _apply_start_date


def history_field(value: object, *, present: bool = True) -> dict[str, object]:
    return {"Present": present, "Value": value}


def test_load_gc_probe_anchor_preserves_current_rank_and_uncertainty(tmp_path) -> None:
    probe_path = tmp_path / "probe.json"
    probe_path.write_text(
        json.dumps(
            {
                "AccountId": 12_345,
                "CurrentRank": {
                    "RankValue": {"Present": True, "Value": 4785},
                },
                "RankConfidence": {
                    "BaseUncertainty": 151,
                    "ProjectedUncertainty": 151,
                    "DisplayConfidencePercent": 29,
                    "TimeBaseSecondsCandidate": 1_787_234_775,
                    "ObservedAtUnix": 1_787_284_370,
                },
            }
        ),
        encoding="utf-8",
    )

    anchor = load_gc_probe_anchor(probe_path, expected_account_id=12_345)

    assert anchor.current_mmr == 4785
    assert anchor.base_uncertainty == 151
    assert anchor.confidence_percent == 29
    assert anchor.time_base_unix == 1_787_234_775


def test_load_gc_probe_anchor_derives_confidence_from_raw_collector(tmp_path) -> None:
    observed_at_unix = 1_787_284_370
    probe_path = tmp_path / "raw-collector.json"
    probe_path.write_text(
        json.dumps(
            {
                "CapturedAtUtc": datetime.fromtimestamp(
                    observed_at_unix, tz=UTC
                ).isoformat(),
                "AccountId": 12_345,
                "CurrentRank": {
                    "RankValue": {"Present": True, "Value": 4785},
                    "RankData1": {"Present": True, "Value": 151},
                    "RankData3": {"Present": True, "Value": 1_787_234_775},
                },
            }
        ),
        encoding="utf-8",
    )

    anchor = load_gc_probe_anchor(probe_path, expected_account_id=12_345)

    assert anchor.current_mmr == 4785
    assert anchor.base_uncertainty == 151
    assert anchor.projected_uncertainty >= 151
    assert 0 <= anchor.confidence_percent <= 100
    assert anchor.observed_at_unix == observed_at_unix


def test_gc_probe_anchor_rejects_a_different_account(tmp_path) -> None:
    probe_path = tmp_path / "probe.json"
    probe_path.write_text(
        json.dumps({"AccountId": 1, "CurrentRank": {}, "RankConfidence": {}}),
        encoding="utf-8",
    )

    with pytest.raises(ValueError, match="does not match"):
        load_gc_probe_anchor(probe_path, expected_account_id=2)


def test_gc_history_uses_exact_rank_change_for_an_abandon_without_winner(
    tmp_path,
) -> None:
    history_path = tmp_path / "gc-collection.json"
    history_path.write_text(
        json.dumps(
            {
                "AccountId": 67_890,
                "MatchHistory": {
                    "Finished": True,
                    "Error": None,
                    "Matches": [
                        {
                            "MatchId": history_field(8_383_226_907),
                            "StartTime": history_field(1_753_095_580),
                            "HeroId": history_field(0),
                            "Winner": history_field(False, present=False),
                            "GameMode": history_field(22),
                            "RankChange": history_field(-26),
                            "PreviousRank": history_field(7880),
                            "LobbyType": history_field(7),
                            "SoloRank": history_field(True),
                            "Abandon": history_field(True),
                            "Duration": history_field(0),
                        }
                    ],
                },
            }
        ),
        encoding="utf-8",
    )

    timeline = load_gc_match_history_timeline(
        history_path,
        expected_account_id=67_890,
    )

    assert len(timeline) == 1
    assert timeline[0].won is False
    assert timeline[0].reported is not None
    assert timeline[0].reported.start_mmr == 7880
    assert timeline[0].reported.rank_change == -26


def test_gc_history_keeps_single_rank_truth_before_glicko_but_not_missing_rows(
    tmp_path,
) -> None:
    history_path = tmp_path / "gc-collection.json"

    def game(
        match_id: int,
        started_at: datetime,
        previous_rank: int | None,
        rank_change: int | None,
    ) -> dict[str, object]:
        return {
            "MatchId": history_field(match_id),
            "StartTime": history_field(int(started_at.timestamp())),
            "HeroId": history_field(31),
            "Winner": history_field(rank_change is None or rank_change > 0),
            "GameMode": history_field(22),
            "RankChange": history_field(
                rank_change or 0,
                present=rank_change is not None,
            ),
            "PreviousRank": history_field(
                previous_rank or 0,
                present=previous_rank is not None,
            ),
            "LobbyType": history_field(7),
            "SoloRank": history_field(True),
            "Abandon": history_field(False),
            "Duration": history_field(1800),
        }

    history_path.write_text(
        json.dumps(
            {
                "AccountId": 123,
                "MatchHistory": {
                    "Finished": True,
                    "Error": None,
                    "Matches": [
                        game(1, datetime(2020, 3, 7, tzinfo=UTC), 3138, -30),
                        game(2, datetime(2020, 3, 8, tzinfo=UTC), None, None),
                        game(3, datetime(2023, 4, 21, tzinfo=UTC), None, None),
                    ],
                },
            }
        ),
        encoding="utf-8",
    )

    timeline = load_gc_match_history_timeline(
        history_path,
        expected_account_id=123,
    )

    assert [match.match_id for match in timeline] == [1, 3]
    assert timeline[0].reported is not None
    assert timeline[1].reported is None


def test_backtest_loader_skips_exact_pre_glicko_rows_without_confidence(
    tmp_path,
) -> None:
    csv_path = tmp_path / "match-estimates.csv"
    csv_path.write_text(
        "date_utc,match_id,result,mmr_fields_visible,confidence_used,"
        "actual_rank_change,actual_start_mmr,actual_end_mmr,"
        "uncertainty_proxy,likely_double_down,anchor_jump_before\n"
        "2023-04-19T00:00:00+00:00,1,Win,True,,30,1000,1030,,False,0\n"
        "2023-04-21T00:00:00+00:00,2,Loss,True,0.8,-25,1030,1005,100,False,0\n",
        encoding="utf-8",
    )

    runs = load_contiguous_actual_runs(csv_path)

    assert [[match.match_id for match in run] for run in runs] == [["2"]]


def test_curve_starts_at_first_real_anchor_when_leading_hidden_block_has_none() -> None:
    first_time = datetime(2023, 4, 21, tzinfo=UTC)
    hidden = TimelineMatch(
        match_id=1,
        started_at=first_time,
        duration_seconds=1800,
        won=False,
        hero_id=31,
        average_rank=None,
        party_size=None,
        reported=None,
    )
    visible_record = ShowMmrRecord(
        match_id=2,
        started_at=datetime(2023, 4, 22, tzinfo=UTC),
        start_mmr=4343,
        rank_change=40,
        hero_id=1,
        solo_queue=True,
    )
    visible = TimelineMatch(
        match_id=2,
        started_at=visible_record.started_at,
        duration_seconds=1800,
        won=True,
        hero_id=1,
        average_rank=None,
        party_size=None,
        reported=visible_record,
    )
    timeline = [hidden, visible]

    rows = build_endpoint_constrained_curve(
        timeline,
        find_hidden_segments(timeline),
        {
            "estimates": [None, 0.5],
            "uncertainty_estimates": [None, None],
        },
        current_mmr=None,
    )

    assert [row["match_id"] for row in rows] == ["2"]
    assert rows[0]["curve_mmr_before"] == 4343


def test_load_gc_match_history_builds_ranked_visible_and_hidden_timeline(tmp_path) -> None:
    probe_path = tmp_path / "collector.json"
    probe_path.write_text(
        json.dumps(
            {
                "AccountId": 123,
                "MatchHistory": {
                    "Finished": True,
                    "Error": None,
                    "Matches": [
                        {
                            "MatchId": history_field(1001),
                            "StartTime": history_field(1_700_000_000),
                            "HeroId": history_field(31),
                            "Winner": history_field(False),
                            "GameMode": history_field(22),
                            "RankChange": history_field(-40),
                            "PreviousRank": history_field(4574),
                            "LobbyType": history_field(7),
                            "SoloRank": history_field(True),
                            "Abandon": history_field(False),
                            "Duration": history_field(2400),
                        },
                        {
                            "MatchId": history_field(1002),
                            "StartTime": history_field(1_700_003_000),
                            "HeroId": history_field(94),
                            "Winner": history_field(True),
                            "GameMode": history_field(22),
                            "RankChange": history_field(0, present=False),
                            "PreviousRank": history_field(0, present=False),
                            "LobbyType": history_field(7),
                            "SoloRank": history_field(False),
                            "Abandon": history_field(False),
                            "Duration": history_field(2100),
                        },
                        {
                            "MatchId": history_field(1003),
                            "StartTime": history_field(1_700_006_000),
                            "HeroId": history_field(1),
                            "Winner": history_field(True),
                            "LobbyType": history_field(0),
                        },
                    ],
                },
            }
        ),
        encoding="utf-8",
    )

    timeline = load_gc_match_history_timeline(probe_path, expected_account_id=123)

    assert [match.match_id for match in timeline] == [1001, 1002]
    assert timeline[0].reported is not None
    assert timeline[0].reported.start_mmr == 4574
    assert timeline[0].reported.rank_change == -40
    assert timeline[1].reported is None
    assert timeline[1].won is True


def test_gc_match_history_rejects_incomplete_collection(tmp_path) -> None:
    probe_path = tmp_path / "collector.json"
    probe_path.write_text(
        json.dumps(
            {
                "AccountId": 123,
                "MatchHistory": {
                    "Finished": False,
                    "Error": "response_timeout",
                    "Matches": [],
                },
            }
        ),
        encoding="utf-8",
    )

    with pytest.raises(ValueError, match="incomplete"):
        load_gc_match_history_timeline(probe_path, expected_account_id=123)


def test_precision_gain_proxy_reduces_uncertainty_and_respects_floor() -> None:
    assert update_uncertainty_after_match(155, information_gain=1.95e-6) == 151
    assert update_uncertainty_after_match(90, information_gain=1.95e-6) == 90
    assert update_uncertainty_after_match(151, information_gain=0) == 151


@pytest.mark.parametrize("information_gain", [-1.0, float("inf")])
def test_precision_gain_proxy_rejects_invalid_gain(information_gain: float) -> None:
    with pytest.raises(ValueError, match="finite and non-negative"):
        update_uncertainty_after_match(151, information_gain=information_gain)


def test_recommended_delta_summary_uses_visible_medians_and_low_anchor() -> None:
    rows = [
        {
            "actual_rank_change": delta,
            "mmr_fields_visible": True,
            "likely_double_down": False,
            "result": result,
            "confidence_used": confidence,
        }
        for delta, result, confidence in (
            (40, "Win", 0.35),
            (38, "Win", 0.35),
            (-40, "Loss", 0.35),
            (27, "Win", 0.70),
            (-25, "Loss", 0.70),
        )
    ]
    low_model = {
        "recommended_low_confidence_win_delta": 75.75,
        "recommended_low_confidence_loss_delta": -75.75,
        "symmetric_delta_observed_min": 45.0,
        "symmetric_delta_observed_max": 105.5,
        "recommended_model": "test basis",
    }

    summary = summarize_recommended_delta_model(rows, low_model)

    diagnostics = summary["visible_bin_diagnostics"]
    assert isinstance(diagnostics, list)
    assert diagnostics[0]["median_magnitude"] == 39
    assert summary["below_30pct"]["win"] == 75.75


def test_ranked_delta_prior_is_continuous_and_sign_aware() -> None:
    assert ranked_delta_prior(0.0, won=True) == 120
    assert ranked_delta_prior(0.30, won=True) == 40
    assert ranked_delta_prior(0.40, won=True) == 27
    assert ranked_delta_prior(1.0, won=False) == -25
    assert ranked_delta_prior(0.35, won=False) == pytest.approx(-32.5)


def test_glicko_saturating_prior_hits_stable_and_calibration_anchors() -> None:
    assert glicko_saturating_delta_prior(90, won=True) == pytest.approx(27)
    assert glicko_saturating_delta_prior(90, won=False) == pytest.approx(-25)
    assert glicko_saturating_delta_prior(150, won=True) == pytest.approx(40)
    assert glicko_saturating_delta_prior(150, won=False) == pytest.approx(-40)
    assert 40 < glicko_saturating_delta_prior(300, won=True) < 55


def test_double_down_mixture_learns_a_nonzero_doubled_component() -> None:
    observations = [(25.0, 25.0)] * 18 + [(50.0, 25.0)] * 2

    model = fit_double_down_mixture(observations)

    assert model["double_down_rate"] == pytest.approx(0.10, abs=0.02)
    assert model["residual_sigma"] == pytest.approx(2.0)


def test_double_down_posterior_uses_endpoint_direction() -> None:
    posterior = infer_double_down_posterior(
        [25.0, -25.0, -25.0],
        target_change=-50,
        double_down_rate=0.10,
        residual_sigma=3.0,
    )

    probabilities = posterior["probabilities"]
    assert isinstance(probabilities, list)
    assert probabilities[0] < probabilities[1]
    assert probabilities[0] < probabilities[2]
    assert sum(probabilities) == pytest.approx(1.0, abs=0.15)


def test_endpoint_constraint_preserves_results_and_hits_exact_total() -> None:
    priors = [75.0] * 9 + [-75.0] * 7

    deltas = allocate_endpoint_constrained_deltas(priors, target_change=211)

    assert sum(deltas) == 211
    assert all(delta > 0 for delta in deltas[:9])
    assert all(delta < 0 for delta in deltas[9:])


def test_endpoint_constraint_handles_negative_segment_and_rounding() -> None:
    deltas = allocate_endpoint_constrained_deltas(
        [55.2, -54.7, -52.1],
        target_change=-54,
    )

    assert sum(deltas) == -54
    assert deltas[0] > 0
    assert deltas[1] < 0
    assert deltas[2] < 0


def test_endpoint_constraint_rejects_impossible_sign_preserving_total() -> None:
    with pytest.raises(ValueError, match="outside feasible"):
        allocate_endpoint_constrained_deltas(
            [25.0, -25.0],
            target_change=500,
            maximum_magnitude=100,
        )


def test_weighted_endpoint_constraint_is_exact_and_preserves_signs() -> None:
    deltas = allocate_weighted_endpoint_deltas(
        [80.0, -70.0, 60.0],
        target_change=95,
        variances=[4.0, 1.0, 2.0],
    )

    assert sum(deltas) == 95
    assert deltas[0] > 0
    assert deltas[1] < 0
    assert deltas[2] > 0


def test_latent_double_down_inference_can_use_endpoint_residual() -> None:
    multipliers = infer_double_down_multipliers(
        [25.0, -25.0, 25.0],
        target_change=50,
        penalty=0.0,
    )

    assert sum(multiplier == 2 for multiplier in multipliers) == 1


def test_timeline_includes_authenticated_gc_matches_missing_from_opendota() -> None:
    started_at = datetime(2025, 1, 1, tzinfo=UTC)
    gc_only = ShowMmrRecord(
        match_id=222,
        started_at=started_at,
        start_mmr=4000,
        rank_change=-25,
        hero_id=31,
        solo_queue=True,
    )
    raw_matches = [
        {
            "match_id": 111,
            "start_time": int(started_at.timestamp()) - 60,
            "duration": 1800,
            "player_slot": 0,
            "radiant_win": True,
            "hero_id": 1,
            "average_rank": 50,
            "party_size": 1,
        }
    ]

    timeline = build_timeline(raw_matches, {gc_only.match_id: gc_only})

    assert [match.match_id for match in timeline] == [111, 222]
    assert timeline[1].won is False
    assert timeline[1].reported == gc_only


def test_complete_curve_prepends_pre_glicko_truth_without_mutating_model_rows() -> None:
    historical = ShowMmrRecord(
        match_id=100,
        started_at=datetime(2017, 1, 21, tzinfo=UTC),
        start_mmr=2576,
        rank_change=25,
        hero_id=10,
        solo_queue=True,
    )
    modeled = {
        "date_utc": "2023-04-21T00:00:00+00:00",
        "unix_time": 1_682_035_200,
        "match_id": "200",
        "result": "Loss",
        "hero_id": 31,
        "mmr_fields_visible": False,
        "curve_mmr_before": 4400,
        "curve_mmr_after": 4350,
        "anchor_jump_before": None,
    }

    rows = build_complete_history_curve({historical.match_id: historical}, [modeled])

    assert len(rows) == 2
    assert rows[0]["curve_source"] == "GC actual (pre-Glicko)"
    assert rows[0]["curve_mmr_after"] == 2601
    assert rows[1]["anchor_jump_before"] == 1799
    assert modeled["anchor_jump_before"] is None


def test_apply_start_date_excludes_earlier_rating_tracks_and_resets_first_jump() -> None:
    rows = [
        {"date_utc": "2020-03-01T00:00:00+00:00", "anchor_jump_before": 500},
        {"date_utc": "2020-03-07T00:00:00+00:00", "anchor_jump_before": -500},
    ]

    selected, cutoff = _apply_start_date(rows, "2020-03-02")

    assert cutoff == datetime(2020, 3, 2, tzinfo=UTC)
    assert selected == [
        {"date_utc": "2020-03-07T00:00:00+00:00", "anchor_jump_before": 0}
    ]


def test_unified_gc_history_runs_end_to_end_without_opendota(tmp_path) -> None:
    start_time = 1_700_000_000

    def game(
        offset: int,
        match_id: int,
        won: bool,
        previous_rank: int | None,
        rank_change: int | None,
    ) -> dict[str, object]:
        return {
            "MatchId": history_field(match_id),
            "StartTime": history_field(start_time + offset * 86_400),
            "HeroId": history_field(31),
            "Winner": history_field(won),
            "GameMode": history_field(22),
            "RankChange": history_field(
                rank_change or 0,
                present=rank_change is not None,
            ),
            "PreviousRank": history_field(
                previous_rank or 0,
                present=previous_rank is not None,
            ),
            "LobbyType": history_field(7),
            "SoloRank": history_field(True),
            "Abandon": history_field(False),
            "Duration": history_field(2400),
        }

    games = [
        game(0, 1000, True, 4000, 25),
        game(1, 1001, True, None, None),
        game(2, 1002, False, None, None),
        game(3, 1003, True, 4030, 25),
        game(4, 1004, True, None, None),
        game(5, 1005, True, None, None),
        game(6, 1006, False, None, None),
        game(7, 1007, False, 4130, -25),
        game(8, 1008, True, None, None),
        game(9, 1009, True, None, None),
        game(10, 1010, False, None, None),
    ]
    probe_path = tmp_path / "gc-collection.json"
    probe_path.write_text(
        json.dumps(
            {
                "SchemaVersion": 3,
                "AccountId": 123,
                "CurrentRank": {
                    "RankValue": history_field(4150),
                },
                "RankConfidence": {
                    "BaseUncertainty": 151,
                    "ProjectedUncertainty": 151,
                    "DisplayConfidencePercent": 29,
                    "TimeBaseUnix": start_time + 10 * 86_400 + 2400,
                    "ObservedAtUnix": start_time + 11 * 86_400,
                },
                "MatchHistory": {
                    "Finished": True,
                    "Error": None,
                    "Matches": games,
                },
            }
        ),
        encoding="utf-8",
    )
    output_dir = tmp_path / "model"
    root = Path(__file__).resolve().parents[1]

    result = subprocess.run(
        [
            sys.executable,
            str(root / "scripts" / "analyze_confidence_model.py"),
            "--account-id",
            "123",
            "--gc-history-json",
            str(probe_path),
            "--output-dir",
            str(output_dir),
        ],
        cwd=root,
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 0, result.stderr
    summary = json.loads((output_dir / "model-summary.json").read_text(encoding="utf-8"))
    assert summary["input_source"] == "authenticated_gc_match_history"
    assert summary["model_version"] == "endpoint-constrained-glicko-dd-v2"
    assert summary["double_down_mixture"]["observations"] == 2
    assert summary["curve_reconstruction"]["matches"] == 11
    assert summary["curve_reconstruction"]["endpoint_constrained_matches"] == 8
    assert summary["curve_reconstruction"]["all_hidden_endpoints_exact"] is True
    assert (output_dir / "complete-mmr-curve.csv").is_file()
    dataset = json.loads((output_dir / "mmr-dataset.json").read_text(encoding="utf-8"))
    assert dataset["account_id"] == 123
    assert dataset["model_version"] == "endpoint-constrained-glicko-dd-v2"
    assert len(dataset["rows"]) == 11
    assert "double_down_probability" in (
        output_dir / "complete-mmr-curve.csv"
    ).read_text(encoding="utf-8-sig").splitlines()[0]
    assert (output_dir / "complete-mmr-curve.svg").read_text(encoding="utf-8").startswith(
        "<svg"
    )
