from __future__ import annotations

from dataclasses import dataclass
import os


@dataclass(frozen=True)
class BridgeConfig:
    transport: str
    host: str
    port: int
    timeout_sec: float

    @classmethod
    def from_env(cls) -> "BridgeConfig":
        transport = os.getenv("UNITY_INFO_BRIDGE_TRANSPORT", "tcp").strip().lower()
        host = os.getenv("UNITY_INFO_BRIDGE_HOST", "127.0.0.1").strip()
        port = _safe_int(os.getenv("UNITY_INFO_BRIDGE_PORT"), 16000)
        timeout_sec = _safe_float(os.getenv("UNITY_INFO_BRIDGE_TIMEOUT_SEC"), 8.0)

        return cls(
            transport=transport,
            host=host,
            port=port,
            timeout_sec=max(0.5, timeout_sec),
        )


@dataclass(frozen=True)
class ServerConfig:
    name: str
    log_level: str
    bridge: BridgeConfig

    @classmethod
    def from_env(cls) -> "ServerConfig":
        return cls(
            name=os.getenv("UNITY_INFO_MCP_NAME", "UnityInfoMCP").strip() or "UnityInfoMCP",
            log_level=os.getenv("UNITY_INFO_MCP_LOG_LEVEL", "INFO").strip().upper(),
            bridge=BridgeConfig.from_env(),
        )


def _safe_int(raw: str | None, default: int) -> int:
    if raw is None:
        return default
    try:
        return int(raw)
    except ValueError:
        return default


def _safe_float(raw: str | None, default: float) -> float:
    if raw is None:
        return default
    try:
        return float(raw)
    except ValueError:
        return default

