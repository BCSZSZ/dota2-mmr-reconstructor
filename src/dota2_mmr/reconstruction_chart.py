from __future__ import annotations

import math
from datetime import datetime, timedelta
from html import escape
from pathlib import Path


def write_reconstruction_svg(
    *,
    rows: list[dict[str, object]],
    destination: Path,
    account_id: int,
) -> None:
    if not rows:
        raise ValueError("cannot render an empty MMR reconstruction")

    width = 1200
    height = 650
    left = 92
    right = 38
    top = 82
    bottom = 76
    plot_width = width - left - right
    plot_height = height - top - bottom
    times = [datetime.fromisoformat(str(row["date_utc"])) for row in rows]
    values = [int(row["curve_mmr_after"]) for row in rows]
    start_time = times[0]
    end_time = times[-1]
    time_span = max((end_time - start_time).total_seconds(), 1.0)

    value_min = min(values + [int(rows[0]["curve_mmr_before"])])
    value_max = max(values + [int(rows[0]["curve_mmr_before"])])
    value_span = max(value_max - value_min, 100)
    tick_step = max(50, math.ceil((value_span / 7) / 50) * 50)
    axis_min = math.floor((value_min - tick_step / 2) / tick_step) * tick_step
    axis_max = math.ceil((value_max + tick_step / 2) / tick_step) * tick_step

    def x_position(value: datetime) -> float:
        return left + ((value - start_time).total_seconds() / time_span) * plot_width

    def y_position(value: int) -> float:
        ratio = (value - axis_min) / (axis_max - axis_min)
        return top + plot_height - ratio * plot_height

    y_marks: list[str] = []
    tick_value = axis_min
    while tick_value <= axis_max:
        y = y_position(tick_value)
        y_marks.append(
            f'<line x1="{left}" y1="{y:.2f}" x2="{left + plot_width}" y2="{y:.2f}" '
            'stroke="#d8dee4"/>'
            f'<text x="{left - 12}" y="{y + 4:.2f}" text-anchor="end">'
            f"{tick_value:,}</text>"
        )
        tick_value += tick_step

    x_marks: list[str] = []
    tick_count = min(7, len(rows))
    for index in range(tick_count):
        timestamp = start_time + timedelta(
            seconds=time_span * index / max(tick_count - 1, 1)
        )
        x = x_position(timestamp)
        anchor = "start" if index == 0 else "end" if index == tick_count - 1 else "middle"
        x_marks.append(
            f'<line x1="{x:.2f}" y1="{top + plot_height}" x2="{x:.2f}" '
            f'y2="{top + plot_height + 7}" stroke="#57606a"/>'
            f'<text x="{x:.2f}" y="{top + plot_height + 27}" text-anchor="{anchor}">'
            f"{timestamp:%Y-%m}</text>"
        )

    segments: list[str] = []
    double_down_marks: list[str] = []
    previous_x = left
    previous_y = y_position(int(rows[0]["curve_mmr_before"]))
    modeled_count = 0
    for row, timestamp, value in zip(rows, times, values, strict=True):
        x = x_position(timestamp)
        y = y_position(value)
        modeled = row["mmr_fields_visible"] is not True
        modeled_count += modeled
        color = "#2da44e" if modeled else "#0969da"
        dash = ' stroke-dasharray="5 3"' if modeled else ""
        source = "endpoint constrained" if modeled else "GC actual"
        double_down_probability = float(row.get("double_down_probability") or 0.0)
        probability_label = (
            f" · DD {double_down_probability:.0%}" if modeled else ""
        )
        segments.append(
            f'<path d="M {previous_x:.2f} {previous_y:.2f} H {x:.2f} V {y:.2f}" '
            f'fill="none" stroke="{color}" stroke-width="2"{dash}>'
            f'<title>{escape(str(row["match_id"]))} · {escape(str(row["result"]))} · '
            f'{value:,} · {source}{probability_label}</title></path>'
        )
        if modeled and double_down_probability >= 0.20:
            double_down_marks.append(
                f'<circle cx="{x:.2f}" cy="{y:.2f}" r="4" fill="#bf8700" '
                'stroke="#ffffff" stroke-width="1.5">'
                f'<title>{escape(str(row["match_id"]))} · Double Down probability '
                f'{double_down_probability:.0%}</title></circle>'
            )
        previous_x = x
        previous_y = y

    final_value = values[-1]
    title = f"Account {account_id} · MMR reconstruction"
    subtitle = (
        f"{start_time:%Y-%m} to {end_time:%Y-%m} · {len(rows)} ranked matches · "
        f"{len(rows) - modeled_count} GC actual · "
        f"{modeled_count} endpoint-constrained"
    )
    svg = f"""<svg xmlns="http://www.w3.org/2000/svg"
  viewBox="0 0 {width} {height}" role="img" aria-labelledby="title desc">
<title id="title">{escape(title)}</title>
<desc id="desc">{escape(subtitle)}</desc>
<rect width="{width}" height="{height}" fill="#ffffff"/>
<g font-family="system-ui, sans-serif" font-size="13" fill="#1f2328">
  <text x="{left}" y="32" font-size="22" font-weight="600">{escape(title)}</text>
  <text x="{left}" y="56" fill="#57606a">{escape(subtitle)}</text>
  {''.join(y_marks)}
  <rect x="{left}" y="{top}" width="{plot_width}" height="{plot_height}"
    fill="none" stroke="#8c959f"/>
  {''.join(segments)}
  {''.join(double_down_marks)}
  {''.join(x_marks)}
  <line x1="{left}" y1="{height - 28}" x2="{left + 24}" y2="{height - 28}"
    stroke="#0969da" stroke-width="2"/>
  <text x="{left + 31}" y="{height - 24}">GC actual</text>
  <line x1="{left + 125}" y1="{height - 28}" x2="{left + 149}" y2="{height - 28}"
    stroke="#2da44e" stroke-width="2" stroke-dasharray="5 3"/>
  <text x="{left + 156}" y="{height - 24}">Glicko + DD endpoint</text>
  <circle cx="{left + 348}" cy="{height - 28}" r="4" fill="#bf8700"/>
  <text x="{left + 359}" y="{height - 24}">DD probability ≥20%</text>
  <text x="{left + plot_width - 6}" y="{y_position(final_value) - 10:.2f}"
    text-anchor="end" font-weight="600">{final_value:,}</text>
  <text transform="translate(24 {top + plot_height / 2}) rotate(-90)"
    text-anchor="middle">MMR</text>
</g>
</svg>
"""
    destination.write_text(svg, encoding="utf-8")
