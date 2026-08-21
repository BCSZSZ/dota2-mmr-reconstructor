import pytest

from dota2_mmr.cli import STEAM_ID64_OFFSET, normalize_account_id, positive_int


def test_normalize_account_id_accepts_steam32_and_steam64() -> None:
    assert normalize_account_id(123_456_789) == 123_456_789
    assert normalize_account_id(STEAM_ID64_OFFSET + 123_456_789) == 123_456_789


def test_positive_int_rejects_zero() -> None:
    with pytest.raises(Exception, match="greater than zero"):
        positive_int("0")
