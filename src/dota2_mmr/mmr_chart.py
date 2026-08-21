import math
from datetime import datetime
from html import escape
from pathlib import Path
from zoneinfo import ZoneInfo

from dota2_mmr.mmr import MmrEstimate


def _scale_x(
    value: datetime,
    start: datetime,
    end: datetime,
    left: float,
    width: float,
) -> float:
    span = max((end - start).total_seconds(), 1.0)
    return left + ((value - start).total_seconds() / span) * width


def write_mmr_estimate_svg(
    *,
    estimate: MmrEstimate,
    destination: Path,
    local_timezone: ZoneInfo,
    player_name: str | None,
) -> None:
    width = 1200
    height = 620
    left = 88
    right = 34
    top = 82
    bottom = 72
    plot_width = width - left - right
    plot_height = height - top - bottom

    times = [estimate.collection.start] + [point.match.started_at for point in estimate.points]
    values = [estimate.estimated_mmr_before_period] + [
        point.estimated_mmr_after_match for point in estimate.points
    ]
    chart_end = times[-1]
    value_min = min(values)
    value_max = max(values)
    value_span = max(value_max - value_min, 100)
    tick_step = max(50, math.ceil((value_span / 6) / 50) * 50)
    axis_min = math.floor((value_min - tick_step / 2) / tick_step) * tick_step
    axis_max = math.ceil((value_max + tick_step / 2) / tick_step) * tick_step

    def y_position(value: int) -> float:
        ratio = (value - axis_min) / (axis_max - axis_min)
        return top + plot_height - ratio * plot_height

    path_parts = [f"M {left:.2f} {y_position(values[0]):.2f}"]
    for point_time, value in zip(times[1:], values[1:], strict=True):
        x = _scale_x(point_time, estimate.collection.start, chart_end, left, plot_width)
        path_parts.append(f"H {x:.2f} V {y_position(value):.2f}")

    y_marks: list[str] = []
    tick_value = axis_min
    while tick_value <= axis_max:
        y = y_position(tick_value)
        y_marks.append(
            f'<line x1="{left}" y1="{y:.2f}" x2="{left + plot_width}" y2="{y:.2f}" '
            'stroke="#d8dee4"/>'
            f'<text x="{left - 12}" y="{y + 4:.2f}" text-anchor="end">'
            f'{tick_value:,}</text>'
        )
        tick_value += tick_step

    local_start = estimate.collection.start.astimezone(local_timezone)
    local_end = chart_end.astimezone(local_timezone)
    month = datetime(local_start.year, local_start.month, 1, tzinfo=local_timezone)
    x_marks: list[str] = []
    while month <= local_end:
        x = _scale_x(
            month.astimezone(estimate.collection.start.tzinfo),
            estimate.collection.start,
            chart_end,
            left,
            plot_width,
        )
        x_marks.append(
            f'<line x1="{x:.2f}" y1="{top + plot_height}" x2="{x:.2f}" '
            f'y2="{top + plot_height + 7}" stroke="#57606a"/>'
            f'<text x="{x:.2f}" y="{top + plot_height + 27}" text-anchor="middle">'
            f'{month:%Y-%m}</text>'
        )
        next_month = month.month + 1
        next_year = month.year
        if next_month == 13:
            next_month = 1
            next_year += 1
        month = datetime(next_year, next_month, 1, tzinfo=local_timezone)

    dots: list[str] = []
    for point in estimate.points:
        x = _scale_x(
            point.match.started_at,
            estimate.collection.start,
            chart_end,
            left,
            plot_width,
        )
        y = y_position(point.estimated_mmr_after_match)
        fill = "#2da44e" if point.match.won else "#cf222e"
        radius = 5 if point.is_anchor else 2.6
        stroke = ' stroke="#8250df" stroke-width="3"' if point.is_anchor else ""
        result = "win" if point.match.won else "loss"
        dots.append(
            f'<circle cx="{x:.2f}" cy="{y:.2f}" r="{radius}" fill="{fill}"{stroke}>'
            f'<title>{point.match.match_id} · {result} · '
            f'{point.estimated_mmr_after_match}</title></circle>'
        )

    name = player_name or str(estimate.collection.account_id)
    title = f"{name} · {estimate.collection.start.astimezone(local_timezone).year} MMR estimate"
    subtitle = (
        f"ROUGH ONLY · user anchor {estimate.anchor_mmr_after_match} after match "
        f"{estimate.anchor_match_id} · fixed ±{estimate.mmr_per_result} per result"
    )
    final_value = estimate.estimated_mmr_after_period
    final_change = estimate.estimated_period_change
    svg = f"""<svg xmlns="http://www.w3.org/2000/svg"
  viewBox="0 0 {width} {height}" role="img" aria-labelledby="title desc">
<title id="title">{escape(title)}</title>
<desc id="desc">{escape(subtitle)}</desc>
<rect width="{width}" height="{height}" fill="#ffffff"/>
<g font-family="system-ui, sans-serif" font-size="13" fill="#1f2328">
  <text x="{left}" y="32" font-size="22" font-weight="600">{escape(title)}</text>
  <text x="{left}" y="56" fill="#9a6700">{escape(subtitle)}</text>
  {''.join(y_marks)}
  <rect x="{left}" y="{top}" width="{plot_width}" height="{plot_height}"
    fill="none" stroke="#8c959f"/>
  <path d="{' '.join(path_parts)}" fill="none" stroke="#0969da"
    stroke-width="2.5" stroke-linejoin="round"/>
  {''.join(dots)}
  {''.join(x_marks)}
  <text x="{left + plot_width / 2}" y="{height - 15}" text-anchor="middle">
    Date ({escape(str(local_timezone))})
  </text>
  <text transform="translate(22 {top + plot_height / 2}) rotate(-90)"
    text-anchor="middle">Estimated MMR</text>
  <text x="{left + plot_width - 6}" y="{y_position(final_value) - 10:.2f}"
    text-anchor="end" font-weight="600">{final_value:,} ({final_change:+d})</text>
</g>
</svg>
"""
    destination.write_text(svg, encoding="utf-8")
