from collections.abc import Mapping
from typing import Any

import httpx


class OpenDotaError(RuntimeError):
    """Raised when OpenDota cannot provide a valid response."""


class OpenDotaClient:
    def __init__(
        self,
        api_key: str,
        *,
        timeout_seconds: float = 30.0,
        transport: httpx.BaseTransport | None = None,
    ) -> None:
        self._client = httpx.Client(
            base_url="https://api.opendota.com/api",
            headers={"Authorization": f"Bearer {api_key}"},
            timeout=timeout_seconds,
            transport=transport,
        )

    def __enter__(self) -> "OpenDotaClient":
        return self

    def __exit__(self, *args: object) -> None:
        self.close()

    def close(self) -> None:
        self._client.close()

    def _get(self, path: str, *, params: Mapping[str, Any] | None = None) -> Any:
        try:
            response = self._client.get(path, params=params)
            response.raise_for_status()
            return response.json()
        except (httpx.HTTPError, ValueError) as error:
            raise OpenDotaError(f"OpenDota request failed: {error}") from error

    def get_player(self, account_id: int) -> dict[str, Any]:
        result = self._get(f"/players/{account_id}")
        if not isinstance(result, dict):
            raise OpenDotaError("OpenDota returned an invalid player response")
        return result

    def get_ranked_matches(self, account_id: int, *, limit: int = 10_000) -> list[dict[str, Any]]:
        result = self._get(
            f"/players/{account_id}/matches",
            params={"limit": limit, "lobby_type": 7, "significant": 0},
        )
        if not isinstance(result, list) or not all(isinstance(item, dict) for item in result):
            raise OpenDotaError("OpenDota returned an invalid matches response")
        if len(result) >= limit:
            raise OpenDotaError(
                f"OpenDota returned the {limit}-match safety limit; refusing a partial history"
            )
        return result
