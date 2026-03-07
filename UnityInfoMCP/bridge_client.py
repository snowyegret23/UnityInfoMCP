from __future__ import annotations

import asyncio
import json
from typing import Any
from uuid import uuid4

from .config import BridgeConfig
from .errors import BridgeProtocolError, BridgeRemoteError, BridgeUnavailableError


class UnityBridgeClient:
    """JSON-RPC 2.0 client for Unity runtime bridge.

    The bridge is expected to expose one request/response pair per line on a TCP socket.
    """

    def __init__(self, config: BridgeConfig) -> None:
        self._config = config

    async def request(self, method: str, params: dict[str, Any] | None = None) -> Any:
        if self._config.transport != "tcp":
            raise BridgeUnavailableError(
                f"Unsupported transport '{self._config.transport}'. Currently only 'tcp' is implemented."
            )

        payload = {
            "jsonrpc": "2.0",
            "id": str(uuid4()),
            "method": method,
            "params": params or {},
        }

        try:
            reader, writer = await asyncio.wait_for(
                asyncio.open_connection(self._config.host, self._config.port),
                timeout=self._config.timeout_sec,
            )
        except Exception as exc:
            raise BridgeUnavailableError(
                f"Failed to connect to bridge at {self._config.host}:{self._config.port}"
            ) from exc

        try:
            writer.write((json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8"))
            await asyncio.wait_for(writer.drain(), timeout=self._config.timeout_sec)

            raw = await asyncio.wait_for(reader.readline(), timeout=self._config.timeout_sec)
            if not raw:
                raise BridgeProtocolError("Bridge closed connection without response.")

            try:
                response = json.loads(raw.decode("utf-8"))
            except json.JSONDecodeError as exc:
                raise BridgeProtocolError("Bridge returned invalid JSON.") from exc

            if not isinstance(response, dict):
                raise BridgeProtocolError("Bridge response must be a JSON object.")

            if response.get("id") != payload["id"]:
                raise BridgeProtocolError("Bridge response id mismatch.")

            if "error" in response:
                error = response["error"]
                if not isinstance(error, dict):
                    raise BridgeProtocolError("Bridge error payload must be an object.")
                code = int(error.get("code", -32000))
                message = str(error.get("message", "Bridge error"))
                data = error.get("data")
                raise BridgeRemoteError(code=code, message=message, data=data)

            if "result" not in response:
                raise BridgeProtocolError("Bridge response missing 'result'.")

            return response["result"]
        finally:
            writer.close()
            await writer.wait_closed()
