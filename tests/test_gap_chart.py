import shutil
import subprocess
from pathlib import Path

import pytest


def test_unknown_chart_values_and_real_anchor_survive_json_and_csv() -> None:
    node = shutil.which("node")
    if node is None:
        pytest.skip("Node is required to execute the standalone chart code")
    html = (Path(__file__).resolve().parents[1] / "tools/mmr-table-viewer.html").read_text(
        encoding="utf-8"
    )
    helpers = html[html.index("    function toBoolean("):html.index("    function compareValues(")]
    chart = html[html.index("    function firstValue("):html.index("    function clampDomain(")]
    program = helpers + chart + """
const assert = require('node:assert/strict');
const state = {chart: {}};
const els = {chartEmpty: {classList: {add() {}}}};
function fitChart() {}
const original = [
  {date_utc: '2024-01-01T00:00:00Z', curve_mmr_after: 2256, mmr_fields_visible: true},
  {date_utc: '2024-01-02T00:00:00Z', curve_mmr_after: null,
    modeled_rank_change: null, curve_mmr_before: null, curve_source: '无法重建：分差未知'},
  {date_utc: '2024-01-03T00:00:00Z', curve_mmr_after: null,
    modeled_rank_change: null, curve_mmr_before: null, curve_source: '无法重建：分差未知',
    gap_endpoint_time: 1704326400, segment_endpoint_mmr: 3171}
];
for (const csv of [false, true]) {
  const rows = csv ? original.map(row => Object.fromEntries(Object.entries(row)
    .map(([k, v]) => [k, v === null ? '' : String(v)]))) : original;
  state.rows = state.filteredRows = rows;
  updateChartData({fit: true});
  assert.equal(state.chart.points.length, 2);
  assert.equal(state.chart.points[0].mmr, 2256);
  assert.equal(state.chart.points[1].mmr, 3171);
  assert.equal(state.chart.points[1].time, 1704326400000);
  assert.equal(state.chart.points[1].anchor, true);
  assert.ok(Number.isNaN(state.chart.points[1].change));
  assert.equal(state.chart.gaps.length, 2);
}
"""
    subprocess.run([node, "-e", program], check=True, capture_output=True, text=True)
