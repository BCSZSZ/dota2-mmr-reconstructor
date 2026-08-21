from pathlib import Path

import pytest

from dota2_mmr.reconstruct_cli import (
    ReconstructionWorkflowError,
    analysis_command,
    build_paths,
    collector_command,
    run_workflow,
)


def test_collector_command_requests_cached_history_and_account_validation(tmp_path) -> None:
    paths = build_paths(tmp_path, 123)

    command = collector_command(
        collector=Path("Dota2MmrCollector.exe"),
        account_id=123,
        history_matches=5_000,
        paths=paths,
    )

    assert command[:3] == ["Dota2MmrCollector.exe", "--account-id", "123"]
    assert command[command.index("--history-matches") + 1] == "5000"
    assert command[command.index("--history-cache") + 1] == str(
        paths.history_cache_json
    )
    assert "--skip-match-details" not in command
    assert "--skip-battle-report" not in command


def test_analysis_command_uses_unified_gc_history(tmp_path) -> None:
    paths = build_paths(tmp_path, 123)

    command = analysis_command(
        analysis_script=Path("analyze.py"),
        account_id=123,
        paths=paths,
    )

    assert "--gc-history-json" in command
    assert command[command.index("--gc-history-json") + 1] == str(paths.probe_json)
    assert "--showmmr-csv" not in command


def test_no_collect_requires_existing_probe(tmp_path) -> None:
    with pytest.raises(ReconstructionWorkflowError, match="--no-collect"):
        run_workflow(
            account_id=123,
            history_matches=100,
            output_directory=tmp_path,
            collector=tmp_path / "collector.exe",
            analysis_script=tmp_path / "analysis.py",
            collect=False,
        )
