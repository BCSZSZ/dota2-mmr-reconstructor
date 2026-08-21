from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

from dota2_mmr.cli import normalize_account_id, positive_int


class ReconstructionWorkflowError(RuntimeError):
    """Raised when collection or reconstruction cannot complete safely."""


@dataclass(frozen=True, slots=True)
class ReconstructionPaths:
    account_directory: Path
    probe_json: Path
    history_cache_json: Path
    model_directory: Path
    summary_json: Path
    complete_curve_csv: Path
    complete_curve_svg: Path
    dataset_json: Path
    manifest_json: Path


def project_root() -> Path:
    return Path(__file__).resolve().parents[2]


def build_paths(base_directory: Path, account_id: int) -> ReconstructionPaths:
    account_directory = base_directory.resolve() / str(account_id)
    model_directory = account_directory / "mmr-reconstruction"
    return ReconstructionPaths(
        account_directory=account_directory,
        probe_json=account_directory / "gc-collection.json",
        history_cache_json=account_directory / "gc-match-history-cache.json",
        model_directory=model_directory,
        summary_json=model_directory / "model-summary.json",
        complete_curve_csv=model_directory / "complete-mmr-curve.csv",
        complete_curve_svg=model_directory / "complete-mmr-curve.svg",
        dataset_json=model_directory / "mmr-dataset.json",
        manifest_json=account_directory / "reconstruction-manifest.json",
    )


def collector_command(
    *,
    collector: Path,
    account_id: int,
    history_matches: int,
    paths: ReconstructionPaths,
) -> list[str]:
    return [
        str(collector),
        "--account-id",
        str(account_id),
        "--history-matches",
        str(history_matches),
        "--history-cache",
        str(paths.history_cache_json),
        "--output",
        str(paths.probe_json),
    ]


def analysis_command(
    *,
    analysis_script: Path,
    account_id: int,
    paths: ReconstructionPaths,
) -> list[str]:
    return [
        sys.executable,
        str(analysis_script),
        "--account-id",
        str(account_id),
        "--gc-history-json",
        str(paths.probe_json),
        "--output-dir",
        str(paths.model_directory),
    ]


def _run(command: list[str], *, cwd: Path, stage: str) -> None:
    result = subprocess.run(command, cwd=cwd, check=False)
    if result.returncode != 0:
        raise ReconstructionWorkflowError(
            f"{stage} failed with exit code {result.returncode}"
        )


def run_workflow(
    *,
    account_id: int,
    history_matches: int,
    output_directory: Path,
    collector: Path,
    analysis_script: Path,
    collect: bool,
) -> ReconstructionPaths:
    root = project_root()
    paths = build_paths(output_directory, account_id)
    paths.account_directory.mkdir(parents=True, exist_ok=True)

    if collect:
        if not collector.is_file():
            raise ReconstructionWorkflowError(
                f"GC collector executable does not exist: {collector}"
            )
        _run(
            collector_command(
                collector=collector,
                account_id=account_id,
                history_matches=history_matches,
                paths=paths,
            ),
            cwd=collector.parent,
            stage="GC collection",
        )
    elif not paths.probe_json.is_file():
        raise ReconstructionWorkflowError(
            f"--no-collect requires an existing collector output: {paths.probe_json}"
        )

    if not analysis_script.is_file():
        raise ReconstructionWorkflowError(f"analysis script does not exist: {analysis_script}")
    _run(
        analysis_command(
            analysis_script=analysis_script,
            account_id=account_id,
            paths=paths,
        ),
        cwd=root,
        stage="MMR reconstruction",
    )

    if (
        not paths.summary_json.is_file()
        or not paths.complete_curve_csv.is_file()
        or not paths.complete_curve_svg.is_file()
        or not paths.dataset_json.is_file()
    ):
        raise ReconstructionWorkflowError("analysis completed without the required output files")
    summary = json.loads(paths.summary_json.read_text(encoding="utf-8"))
    curve = summary.get("curve_reconstruction", {})
    manifest = {
        "schema_version": 1,
        "generated_at_utc": datetime.now(UTC).isoformat(),
        "account_id": account_id,
        "history_target": history_matches,
        "model_version": summary.get("model_version"),
        "input_source": summary.get("input_source"),
        "matches": curve.get("matches"),
        "actual_gc_matches": curve.get("actual_gc_matches"),
        "endpoint_constrained_matches": curve.get("endpoint_constrained_matches"),
        "all_hidden_endpoints_exact": curve.get("all_hidden_endpoints_exact"),
        "files": {
            "collector_output": str(paths.probe_json),
            "match_history_cache": str(paths.history_cache_json),
            "model_summary": str(paths.summary_json),
            "complete_curve": str(paths.complete_curve_csv),
            "complete_curve_chart": str(paths.complete_curve_svg),
            "table_dataset": str(paths.dataset_json),
        },
    }
    paths.manifest_json.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return paths


def build_parser() -> argparse.ArgumentParser:
    root = project_root()
    default_collector = Path(
        os.environ.get(
            "DOTA2_GC_COLLECTOR",
            root
            / "dist"
            / "Dota2MmrCollector-win-x64"
            / "Dota2MmrCollector.exe",
        )
    )
    parser = argparse.ArgumentParser(
        prog="dota2-mmr-reconstruct",
        description=(
            "Collect the authenticated account's raw GC Match History and Current Rank, "
            "then reconstruct low-Confidence MMR gaps with endpoint constraints."
        ),
    )
    parser.add_argument("account_id", type=int, help="Steam32 account ID or SteamID64")
    parser.add_argument(
        "--history-matches",
        type=positive_int,
        default=5_000,
        help="target total Match History rows retained in the resumable cache (default: 5000)",
    )
    parser.add_argument("--output-dir", type=Path, default=Path("artifacts"))
    parser.add_argument("--collector", type=Path, default=default_collector)
    parser.add_argument(
        "--analysis-script",
        type=Path,
        default=root / "scripts" / "analyze_confidence_model.py",
    )
    parser.add_argument(
        "--no-collect",
        action="store_true",
        help="reuse gc-collection.json and rerun only the reconstruction model",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        account_id = normalize_account_id(args.account_id)
        paths = run_workflow(
            account_id=account_id,
            history_matches=args.history_matches,
            output_directory=args.output_dir,
            collector=args.collector.resolve(),
            analysis_script=args.analysis_script.resolve(),
            collect=not args.no_collect,
        )
    except (argparse.ArgumentTypeError, OSError, ValueError, ReconstructionWorkflowError) as error:
        parser.error(str(error))

    summary = json.loads(paths.summary_json.read_text(encoding="utf-8"))
    curve = summary["curve_reconstruction"]
    print(f"Authenticated account: {account_id}")
    print(
        "Curve matches: "
        f"{curve['matches']} "
        f"({curve['actual_gc_matches']} GC actual / "
        f"{curve['endpoint_constrained_matches']} endpoint-constrained)"
    )
    print(f"All hidden endpoints exact: {curve['all_hidden_endpoints_exact']}")
    print(f"Complete curve: {paths.complete_curve_csv}")
    print(f"Curve chart: {paths.complete_curve_svg}")
    print(f"Table dataset: {paths.dataset_json}")
    print(f"Model summary: {paths.summary_json}")
    print(f"Workflow manifest: {paths.manifest_json}")
    return 0
