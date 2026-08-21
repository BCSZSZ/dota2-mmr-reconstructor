from pathlib import Path

VIEWER = Path(__file__).resolve().parents[1] / "tools" / "mmr-table-viewer.html"


def test_table_viewer_is_a_self_contained_importer() -> None:
    html = VIEWER.read_text(encoding="utf-8")

    assert '<input id="fileInput" type="file"' in html
    assert 'accept=".csv,.json' in html
    assert "parseCsv(text)" in html
    assert "parseJson(text)" in html
    assert "indexedDB.open" in html
    assert "exportStandaloneHtml" in html
    assert '<script src="http' not in html
    assert '<link rel="stylesheet" href="http' not in html


def test_table_viewer_has_virtual_scrolling_and_resizing_controls() -> None:
    html = VIEWER.read_text(encoding="utf-8")

    assert "overflow: auto" in html
    assert "resize: both" in html
    assert 'class="rows-layer"' in html
    assert "OVERSCAN = 8" in html
    assert "beginColumnResize" in html
    assert 'id="scaleInput" type="range"' in html
    assert "ResizeObserver" in html


def test_table_viewer_has_an_interactive_mmr_chart() -> None:
    html = VIEWER.read_text(encoding="utf-8")

    assert 'id="chartSvg"' in html
    assert 'id="chartActualToggle"' in html
    assert 'id="chartModeledToggle"' in html
    assert "updateChartData" in html
    assert "zoomChart" in html
    assert 'addEventListener("wheel"' in html
    assert 'addEventListener("pointerdown"' in html
    assert 'addEventListener("dblclick"' in html
    assert "chartSection.requestFullscreen" in html
