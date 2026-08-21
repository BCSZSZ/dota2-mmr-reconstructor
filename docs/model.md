# Low-Confidence reconstruction model

The production path is implemented in C# inside `Dota2MmrReconstructor.exe`; the Python
implementation remains as a research/reference implementation. Both use the same v2 curve logic,
and the C# model identifies itself as `endpoint-constrained-glicko-dd-v2-csharp`.

## Problem boundary

The authenticated GC Match History exposes exact `previous_rank` and `rank_change` for many
ranked matches. During uncalibrated/low Rank Confidence periods those values can be absent or
suppressed, while the surrounding calibrated matches and the current Rank still provide hard
MMR endpoints.

The server-side Dota 2 update formula is not public. A segment with many hidden matches and only
two endpoints is underdetermined: many different per-match delta sequences have the same total.
This model therefore reconstructs a plausible conditional path, not historical ground truth.

## Inputs

- Ranked Match History rows (`lobby_type = 7`) with protobuf value/presence preserved;
- result, timestamp and exact visible `previous_rank/rank_change` when present;
- raw Current Rank `rank_value`, `rank_data1` and `rank_data3`;
- the 2020-03-02 single-MMR-track boundary and the 2023-04-20 Rank Confidence boundary.

Rows before the Rank Confidence launch are never classified as low Confidence merely because a
rank field is missing.

## Uncertainty proxy

The current client mapping recovered for the documented client build treats `rank_data1` as the
base uncertainty `U0` and `rank_data3` as its time reference. Between matches, Python reproduces
the observed float32 client operation order:

```text
days = (now - rank_data3) / 86400
t = days * 0.3 / 80
U = clamp(round_half_up(sqrt(U0² + t * (250² - 90²))), 90, 3000)
```

The display mapping is piecewise quadratic. In the studied build, projected `U <= 150` is
calibrated and `U = 150` displays 30%. These are client behavior observations, not a stable Valve
API contract.

For an unobserved per-match uncertainty update, the model uses an additive-information proxy:

```text
U_after = round(1 / sqrt(1 / U_before² + information_gain))
```

`information_gain` is selected against visible/hidden transitions and the current raw
uncertainty endpoint.

## Per-match MMR prior

The v2 normal-match magnitude is monotone in uncertainty and saturates rather than growing
without bound:

```text
magnitude(U) = asymptote * U² / (U² + saturation_scale)
```

Its win/loss parameters are anchored to stable-confidence historical deltas at `U=90` and a
magnitude of 40 at `U=150`. The signs always follow the observed match result.

Visible historical deltas fit a two-component mixture: normal magnitude or approximately twice
normal magnitude for Double Down. For a hidden segment, dynamic programming conditions the
per-match Double Down probabilities on the segment's exact total MMR change.

## Endpoint constraint

For each hidden segment with an observed next endpoint, the model projects the signed priors onto
integer deltas that:

1. retain the win/loss sign;
2. stay within configured magnitude bounds;
3. minimize squared movement away from the priors;
4. sum exactly to the observed endpoint change.

The final hidden segment uses the authenticated Current Rank as its endpoint. No observed GC row
is overwritten.

## Interpretation

- Exact endpoint residual of zero is a consistency condition, not an accuracy metric.
- Individual hidden deltas remain non-identifiable without additional server observations.
- Expected win probability, party composition and the server's uncertainty update are latent.
- The most valuable future validation data is a Current Rank snapshot immediately after each
  low-Confidence match.

The executable collector intentionally does none of this modeling; it only preserves raw input.
