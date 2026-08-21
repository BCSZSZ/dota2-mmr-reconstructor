import os
from dataclasses import dataclass
from pathlib import Path

from dotenv import load_dotenv


class ConfigError(RuntimeError):
    """Raised when required local configuration is unavailable."""


@dataclass(frozen=True, slots=True)
class Settings:
    opendota_api_key: str

    @classmethod
    def from_env(cls, env_file: str | Path | None = ".env") -> "Settings":
        if env_file is not None:
            load_dotenv(dotenv_path=env_file, override=False)

        api_key = os.getenv("OPENDOTA_API_KEY", "").strip()
        if not api_key:
            raise ConfigError("OPENDOTA_API_KEY is missing; add it to the local .env file")

        return cls(opendota_api_key=api_key)
