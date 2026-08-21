import pytest

from dota2_mmr.config import ConfigError, Settings


def test_settings_reads_opendota_api_key(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("OPENDOTA_API_KEY", "test-key")

    settings = Settings.from_env(env_file=None)

    assert settings.opendota_api_key == "test-key"


def test_settings_rejects_missing_opendota_api_key(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("OPENDOTA_API_KEY", raising=False)

    with pytest.raises(ConfigError, match="OPENDOTA_API_KEY"):
        Settings.from_env(env_file=None)
