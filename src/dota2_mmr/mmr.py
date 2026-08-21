from dataclasses import dataclass

from dota2_mmr.matches import RankedMatch, RankedMatchCollection


@dataclass(frozen=True, slots=True)
class MmrEstimatePoint:
    match: RankedMatch
    assumed_delta: int
    estimated_mmr_after_match: int
    is_anchor: bool


@dataclass(frozen=True, slots=True)
class MmrEstimate:
    collection: RankedMatchCollection
    anchor_match_id: int
    anchor_mmr_after_match: int
    mmr_per_result: int
    estimated_mmr_before_period: int
    points: tuple[MmrEstimatePoint, ...]

    @property
    def estimated_mmr_after_period(self) -> int:
        if not self.points:
            return self.estimated_mmr_before_period
        return self.points[-1].estimated_mmr_after_match

    @property
    def estimated_period_change(self) -> int:
        return self.estimated_mmr_after_period - self.estimated_mmr_before_period


def estimate_fixed_result_mmr(
    collection: RankedMatchCollection,
    *,
    anchor_match_id: int,
    anchor_mmr_after_match: int,
    mmr_per_result: int = 25,
) -> MmrEstimate:
    if mmr_per_result <= 0:
        raise ValueError("mmr_per_result must be greater than zero")
    if anchor_mmr_after_match < 0:
        raise ValueError("anchor_mmr_after_match cannot be negative")
    if not collection.matches:
        raise ValueError("cannot estimate MMR without matches")

    anchor_index = next(
        (
            index
            for index, match in enumerate(collection.matches)
            if match.match_id == anchor_match_id
        ),
        None,
    )
    if anchor_index is None:
        raise ValueError(f"anchor match {anchor_match_id} is not in the selected match period")

    deltas = [mmr_per_result if match.won else -mmr_per_result for match in collection.matches]
    estimates = [0] * len(collection.matches)
    estimates[anchor_index] = anchor_mmr_after_match

    for index in range(anchor_index - 1, -1, -1):
        estimates[index] = estimates[index + 1] - deltas[index + 1]
    for index in range(anchor_index + 1, len(collection.matches)):
        estimates[index] = estimates[index - 1] + deltas[index]

    estimated_mmr_before_period = estimates[0] - deltas[0]
    points = tuple(
        MmrEstimatePoint(
            match=match,
            assumed_delta=deltas[index],
            estimated_mmr_after_match=estimates[index],
            is_anchor=index == anchor_index,
        )
        for index, match in enumerate(collection.matches)
    )
    return MmrEstimate(
        collection=collection,
        anchor_match_id=anchor_match_id,
        anchor_mmr_after_match=anchor_mmr_after_match,
        mmr_per_result=mmr_per_result,
        estimated_mmr_before_period=estimated_mmr_before_period,
        points=points,
    )
