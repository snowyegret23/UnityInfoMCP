from __future__ import annotations

import asyncio
import json
import logging
import os
from typing import Any, Literal

from mcp.server.fastmcp import FastMCP
from mcp.server.fastmcp.utilities.types import Image
from mcp.types import CallToolResult, TextContent

from .bridge_client import UnityBridgeClient
from .config import BridgeConfig, ServerConfig
from .errors import BridgeProtocolError, BridgeRemoteError, BridgeUnavailableError

CONFIG = ServerConfig.from_env()
logging.basicConfig(level=getattr(logging, CONFIG.log_level, logging.INFO))
logger = logging.getLogger("unity_info_mcp")

mcp = FastMCP(CONFIG.name)

BRIDGE_PORT_RANGE_START = 16001
BRIDGE_PORT_RANGE_END = 16100
DEFAULT_PROBE_TIMEOUT_SEC = 0.35
PROBE_CONCURRENCY = 32

_selected_bridge_port: int | None = None
_target_lock = asyncio.Lock()

MatchMode = Literal["contains", "exact", "regex", "starts_with", "ends_with"]

DEFAULT_TEXT_COMPONENT_TYPES = [
    "*",
]


def _build_ok(method: str, result: Any, *, host: str, port: int) -> dict[str, Any]:
    return {
        "ok": True,
        "method": method,
        "bridge": {
            "transport": CONFIG.bridge.transport,
            "host": host,
            "port": port,
        },
        "result": result,
    }


def _build_error(
    method: str,
    error_type: str,
    message: str,
    details: Any | None = None,
) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "ok": False,
        "method": method,
        "error": {
            "type": error_type,
            "message": message,
        },
    }
    if details is not None:
        payload["error"]["details"] = details
    return payload


def _normalize_limit(value: int, *, default: int, minimum: int = 1, maximum: int = 2000) -> int:
    if value <= 0:
        return default
    return max(minimum, min(maximum, value))


def _normalize_depth(value: int, *, default: int = 2, minimum: int = 0, maximum: int = 12) -> int:
    if value < minimum:
        return default
    return min(maximum, value)


def _normalize_match_mode(mode: MatchMode) -> MatchMode:
    allowed = {"contains", "exact", "regex", "starts_with", "ends_with"}
    if mode not in allowed:
        return "contains"
    return mode


def _split_csv(raw: str) -> list[str]:
    return [item.strip() for item in raw.split(",") if item.strip()]


def _finalize_screenshot_payload(
    response: dict[str, Any],
    *,
    requested_output_path: str | None,
) -> dict[str, Any]:
    payload = dict(response)
    result = dict(payload.get("result") or {})
    output_path = str(result.get("output_path") or "")
    delete_after_read = requested_output_path is None and bool(output_path)
    deleted = False

    if delete_after_read and output_path:
        try:
            os.remove(output_path)
            deleted = True
        except OSError:
            deleted = False

    result["image_included"] = True
    result["image_mime_type"] = "image/png"
    result["ephemeral_file_deleted"] = deleted
    result["output_path_exists"] = bool(output_path) and os.path.exists(output_path)
    payload["result"] = result
    return payload


def _build_screenshot_tool_result(
    response: dict[str, Any],
    *,
    requested_output_path: str | None,
) -> dict[str, Any] | CallToolResult:
    if not response.get("ok"):
        return response

    result = response.get("result")
    if not isinstance(result, dict):
        return response

    output_path = str(result.get("output_path") or "")
    if not output_path:
        return response

    try:
        with open(output_path, "rb") as handle:
            image_bytes = handle.read()
    except OSError:
        return response

    payload = _finalize_screenshot_payload(
        response,
        requested_output_path=requested_output_path,
    )

    return CallToolResult(
        content=[
            Image(data=image_bytes, format="png").to_image_content(),
            TextContent(type="text", text=json.dumps(payload, ensure_ascii=False, indent=2)),
        ],
        structuredContent=payload,
    )


def _make_bridge_config(port: int, timeout_sec: float | None = None) -> BridgeConfig:
    return BridgeConfig(
        transport=CONFIG.bridge.transport,
        host=CONFIG.bridge.host,
        port=port,
        timeout_sec=max(0.1, timeout_sec if timeout_sec is not None else CONFIG.bridge.timeout_sec),
    )


async def _set_selected_bridge_port(port: int | None) -> None:
    global _selected_bridge_port
    async with _target_lock:
        _selected_bridge_port = port


async def _get_selected_bridge_port() -> int | None:
    async with _target_lock:
        return _selected_bridge_port


def _scan_ports() -> list[int]:
    ports = list(range(BRIDGE_PORT_RANGE_START, BRIDGE_PORT_RANGE_END + 1))
    if CONFIG.bridge.port > 0 and CONFIG.bridge.port not in ports:
        ports.insert(0, CONFIG.bridge.port)
    return ports


def _build_target_id(host: str, port: int) -> str:
    return f"{host}:{port}"


async def _request_bridge(
    method: str,
    params: dict[str, Any],
    *,
    port: int,
    timeout_sec: float | None = None,
) -> Any:
    client = UnityBridgeClient(_make_bridge_config(port, timeout_sec))
    return await client.request(method, params)


async def _probe_bridge_target(
    port: int,
    *,
    include_runtime: bool,
    timeout_sec: float,
) -> dict[str, Any] | None:
    try:
        ping = await _request_bridge(
            "ping",
            {"include_runtime": include_runtime},
            port=port,
            timeout_sec=timeout_sec,
        )
    except (BridgeUnavailableError, BridgeProtocolError, BridgeRemoteError):
        return None
    except Exception:
        return None

    runtime = ping.get("runtime") if isinstance(ping, dict) else None
    item: dict[str, Any] = {
        "target_id": _build_target_id(CONFIG.bridge.host, port),
        "host": CONFIG.bridge.host,
        "port": port,
        "status": "ok",
        "ping": ping,
    }
    if isinstance(runtime, dict):
        item["process_name"] = runtime.get("process_name")
        item["process_id"] = runtime.get("process_id")
        item["product_name"] = runtime.get("product_name")
        item["active_scene"] = runtime.get("active_scene")
    return item


async def _discover_bridge_targets(
    *,
    include_runtime: bool = True,
    timeout_sec: float = DEFAULT_PROBE_TIMEOUT_SEC,
) -> list[dict[str, Any]]:
    timeout_sec = max(0.1, timeout_sec)
    sem = asyncio.Semaphore(PROBE_CONCURRENCY)

    async def _probe(port: int) -> dict[str, Any] | None:
        async with sem:
            return await _probe_bridge_target(
                port,
                include_runtime=include_runtime,
                timeout_sec=timeout_sec,
            )

    results = await asyncio.gather(*(_probe(port) for port in _scan_ports()))
    items = [item for item in results if item is not None]
    items.sort(key=lambda x: int(x["port"]))
    return items


async def _invoke(method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
    request_params = params or {}
    host = CONFIG.bridge.host
    attempted_ports: list[int] = []
    selected_port = await _get_selected_bridge_port()

    def _is_new_port(port: int) -> bool:
        return port > 0 and port not in attempted_ports

    try:
        if selected_port is not None and _is_new_port(selected_port):
            attempted_ports.append(selected_port)
            try:
                result = await _request_bridge(method, request_params, port=selected_port)
                return _build_ok(method, result, host=host, port=selected_port)
            except BridgeUnavailableError:
                await _set_selected_bridge_port(None)

        discovered = await _discover_bridge_targets(include_runtime=False, timeout_sec=DEFAULT_PROBE_TIMEOUT_SEC)
        for item in discovered:
            port = int(item["port"])
            if not _is_new_port(port):
                continue
            attempted_ports.append(port)
            try:
                result = await _request_bridge(method, request_params, port=port)
                await _set_selected_bridge_port(port)
                return _build_ok(method, result, host=host, port=port)
            except BridgeUnavailableError:
                continue

        if _is_new_port(CONFIG.bridge.port):
            attempted_ports.append(CONFIG.bridge.port)
            result = await _request_bridge(method, request_params, port=CONFIG.bridge.port)
            await _set_selected_bridge_port(CONFIG.bridge.port)
            return _build_ok(method, result, host=host, port=CONFIG.bridge.port)

        raise BridgeUnavailableError(
            f"No reachable bridge on {host}. Tried ports: {attempted_ports or _scan_ports()}"
        )
    except BridgeUnavailableError as exc:
        return _build_error(
            method,
            "bridge_unavailable",
            str(exc),
            {
                "transport": CONFIG.bridge.transport,
                "host": host,
                "selected_port": selected_port,
                "configured_port": CONFIG.bridge.port,
                "attempted_ports": attempted_ports,
                "auto_range": {
                    "start": BRIDGE_PORT_RANGE_START,
                    "end": BRIDGE_PORT_RANGE_END,
                },
            },
        )
    except BridgeProtocolError as exc:
        return _build_error(
            method,
            "bridge_protocol_error",
            str(exc),
            {
                "host": host,
                "attempted_ports": attempted_ports,
            },
        )
    except BridgeRemoteError as exc:
        return _build_error(
            method,
            "bridge_error",
            exc.message,
            {
                "code": exc.code,
                "data": exc.data,
                "host": host,
                "attempted_ports": attempted_ports,
            },
        )


@mcp.tool()
async def list_bridge_targets(
    include_runtime: bool = True,
    probe_timeout_sec: float = DEFAULT_PROBE_TIMEOUT_SEC,
) -> dict[str, Any]:
    """Discover running UnityInfoBridge targets on localhost (auto-port range 16001~16100)."""
    items = await _discover_bridge_targets(
        include_runtime=include_runtime,
        timeout_sec=max(0.1, min(2.0, probe_timeout_sec)),
    )
    selected_port = await _get_selected_bridge_port()
    return {
        "ok": True,
        "targets": items,
        "count": len(items),
        "selected_port": selected_port,
        "configured_port": CONFIG.bridge.port,
        "auto_range": {
            "start": BRIDGE_PORT_RANGE_START,
            "end": BRIDGE_PORT_RANGE_END,
        },
    }


@mcp.tool()
async def select_bridge_target(
    port: int | None = None,
    auto_select: bool = False,
) -> dict[str, Any]:
    """Select one running bridge target. Use `auto_select=true` to pick the first discovered target."""
    chosen_port = port
    if auto_select or chosen_port is None:
        discovered = await _discover_bridge_targets(include_runtime=True, timeout_sec=DEFAULT_PROBE_TIMEOUT_SEC)
        if not discovered:
            return _build_error(
                "select_bridge_target",
                "bridge_unavailable",
                "No running bridge targets found in auto port range.",
                {
                    "host": CONFIG.bridge.host,
                    "auto_range": {
                        "start": BRIDGE_PORT_RANGE_START,
                        "end": BRIDGE_PORT_RANGE_END,
                    },
                },
            )
        chosen_port = int(discovered[0]["port"])

    if chosen_port is None or chosen_port <= 0:
        return _build_error("select_bridge_target", "invalid_params", "A valid port is required.")

    try:
        ping = await _request_bridge(
            "ping",
            {"include_runtime": True},
            port=chosen_port,
            timeout_sec=CONFIG.bridge.timeout_sec,
        )
    except (BridgeUnavailableError, BridgeProtocolError, BridgeRemoteError) as exc:
        return _build_error(
            "select_bridge_target",
            "bridge_unavailable",
            str(exc),
            {
                "host": CONFIG.bridge.host,
                "port": chosen_port,
            },
        )

    await _set_selected_bridge_port(chosen_port)
    return {
        "ok": True,
        "selected_target": {
            "target_id": _build_target_id(CONFIG.bridge.host, chosen_port),
            "host": CONFIG.bridge.host,
            "port": chosen_port,
        },
        "ping": ping,
    }


@mcp.tool()
async def bridge_status(include_capabilities: bool = True) -> dict[str, Any]:
    """Check bridge connectivity and capabilities.

    Use this first to verify whether Unity game bridge is connected.
    """
    ping = await _invoke("ping", {"include_runtime": True})
    selected_port = await _get_selected_bridge_port()
    if not ping["ok"]:
        return {
            "connected": False,
            "ping": ping,
            "bridge": {
                "transport": CONFIG.bridge.transport,
                "host": CONFIG.bridge.host,
                "configured_port": CONFIG.bridge.port,
                "selected_port": selected_port,
                "auto_range": {
                    "start": BRIDGE_PORT_RANGE_START,
                    "end": BRIDGE_PORT_RANGE_END,
                },
            },
        }

    response: dict[str, Any] = {
        "connected": True,
        "bridge": ping.get("bridge", {}),
        "configured_port": CONFIG.bridge.port,
        "selected_port": selected_port,
        "auto_range": {
            "start": BRIDGE_PORT_RANGE_START,
            "end": BRIDGE_PORT_RANGE_END,
        },
        "ping": ping["result"],
    }

    if include_capabilities:
        caps = await _invoke("get_capabilities", {})
        response["capabilities"] = caps

    return response


@mcp.tool()
async def get_runtime_summary() -> dict[str, Any]:
    """Return game/runtime metadata (process, Unity version, loaded scenes count, player state)."""
    return await _invoke("get_runtime_summary")


@mcp.tool()
async def list_scenes(include_unloaded: bool = False) -> dict[str, Any]:
    """List scenes currently known to runtime with load/active flags and root counts."""
    return await _invoke("list_scenes", {"include_unloaded": include_unloaded})


@mcp.tool()
async def get_scene_hierarchy(
    scene_name: str | None = None,
    scene_handle: int | None = None,
    depth_limit: int = 2,
    include_components: bool = False,
    include_inactive: bool = True,
) -> dict[str, Any]:
    """Fetch hierarchy tree for a target scene.

    Provide either scene_name or scene_handle. When both are omitted, bridge may use active scene.
    """
    depth_limit = _normalize_depth(depth_limit, default=2, minimum=0, maximum=10)
    return await _invoke(
        "get_scene_hierarchy",
        {
            "scene_name": scene_name,
            "scene_handle": scene_handle,
            "depth_limit": depth_limit,
            "include_components": include_components,
            "include_inactive": include_inactive,
        },
    )


@mcp.tool()
async def find_gameobjects_by_name(
    name_query: str,
    scene_name: str | None = None,
    match_mode: MatchMode = "contains",
    include_inactive: bool = True,
    limit: int = 200,
) -> dict[str, Any]:
    """Search GameObjects by name.

    Helpful for UI object hunting and logic tracing by familiar names.
    """
    limit = _normalize_limit(limit, default=200, minimum=1, maximum=5000)
    return await _invoke(
        "find_gameobjects_by_name",
        {
            "name_query": name_query,
            "scene_name": scene_name,
            "match_mode": _normalize_match_mode(match_mode),
            "include_inactive": include_inactive,
            "limit": limit,
        },
    )


@mcp.tool()
async def resolve_instance_id(
    instance_id: int,
    include_relations: bool = True,
) -> dict[str, Any]:
    """Resolve any UnityEngine.Object instance id into typed runtime information."""
    return await _invoke(
        "resolve_instance_id",
        {
            "instance_id": instance_id,
            "include_relations": include_relations,
        },
    )


@mcp.tool()
async def get_gameobject(instance_id: int, include_path: bool = True) -> dict[str, Any]:
    """Get one GameObject by instance id, with scene/path and activation metadata."""
    return await _invoke(
        "get_gameobject",
        {
            "instance_id": instance_id,
            "include_path": include_path,
        },
    )


@mcp.tool()
async def get_gameobject_by_path(
    path: str,
    scene_name: str | None = None,
    include_inactive: bool = True,
) -> dict[str, Any]:
    """Get one GameObject by hierarchy path (for example Canvas/MainMenu/StartButton)."""
    return await _invoke(
        "get_gameobject_by_path",
        {
            "path": path,
            "scene_name": scene_name,
            "include_inactive": include_inactive,
        },
    )


@mcp.tool()
async def get_gameobject_children(
    instance_id: int,
    recursive: bool = False,
    depth_limit: int = 2,
    include_components: bool = False,
    include_inactive: bool = True,
) -> dict[str, Any]:
    """Enumerate child hierarchy from a parent GameObject instance id."""
    depth_limit = _normalize_depth(depth_limit, default=2, minimum=0, maximum=12)
    return await _invoke(
        "get_gameobject_children",
        {
            "instance_id": instance_id,
            "recursive": recursive,
            "depth_limit": depth_limit,
            "include_components": include_components,
            "include_inactive": include_inactive,
        },
    )


@mcp.tool()
async def get_components(
    gameobject_instance_id: int,
    include_fields: bool = False,
    field_depth: int = 1,
    include_non_public: bool = False,
) -> dict[str, Any]:
    """List components attached to a GameObject.

    Optionally include selected field values for quick component triage.
    """
    field_depth = _normalize_depth(field_depth, default=1, minimum=0, maximum=6)
    return await _invoke(
        "get_components",
        {
            "gameobject_instance_id": gameobject_instance_id,
            "include_fields": include_fields,
            "field_depth": field_depth,
            "include_non_public": include_non_public,
        },
    )


@mcp.tool()
async def get_component(
    component_instance_id: int,
    include_fields: bool = True,
    field_depth: int = 2,
    include_non_public: bool = False,
) -> dict[str, Any]:
    """Inspect one component instance in detail, including type/member metadata."""
    field_depth = _normalize_depth(field_depth, default=2, minimum=0, maximum=8)
    return await _invoke(
        "get_component",
        {
            "component_instance_id": component_instance_id,
            "include_fields": include_fields,
            "field_depth": field_depth,
            "include_non_public": include_non_public,
        },
    )


@mcp.tool()
async def get_component_fields(
    component_instance_id: int,
    include_non_public: bool = False,
    max_depth: int = 2,
    include_properties: bool = False,
) -> dict[str, Any]:
    """Read component fields/properties for reverse engineering and localization flow tracing."""
    max_depth = _normalize_depth(max_depth, default=2, minimum=0, maximum=8)
    return await _invoke(
        "get_component_fields",
        {
            "component_instance_id": component_instance_id,
            "include_non_public": include_non_public,
            "max_depth": max_depth,
            "include_properties": include_properties,
        },
    )


@mcp.tool()
async def search_component_fields(
    value_query: str,
    scene_name: str | None = None,
    component_type: str | None = None,
    field_name: str | None = None,
    match_mode: MatchMode = "contains",
    include_inactive: bool = True,
    limit: int = 200,
) -> dict[str, Any]:
    """Search component field values by string pattern across scene objects.

    Useful for finding where text, keys, flags, or state values are stored.
    """
    limit = _normalize_limit(limit, default=200, minimum=1, maximum=5000)
    return await _invoke(
        "search_component_fields",
        {
            "value_query": value_query,
            "scene_name": scene_name,
            "component_type": component_type,
            "field_name": field_name,
            "match_mode": _normalize_match_mode(match_mode),
            "include_inactive": include_inactive,
            "limit": limit,
        },
    )


@mcp.tool()
async def list_text_elements(
    scene_name: str | None = None,
    include_inactive: bool = True,
    component_types_csv: str = "*",
    limit: int = 500,
) -> dict[str, Any]:
    """List visible and hidden text-bearing components used for localization work."""
    component_types = _split_csv(component_types_csv) or DEFAULT_TEXT_COMPONENT_TYPES
    limit = _normalize_limit(limit, default=500, minimum=1, maximum=10000)
    return await _invoke(
        "list_text_elements",
        {
            "scene_name": scene_name,
            "include_inactive": include_inactive,
            "component_types": component_types,
            "limit": limit,
        },
    )


@mcp.tool()
async def search_text(
    query: str,
    scene_name: str | None = None,
    include_inactive: bool = True,
    match_mode: MatchMode = "contains",
    component_types_csv: str = "*",
    limit: int = 500,
) -> dict[str, Any]:
    """Search text values in UI/text components by keyword.

    Primary tool for translation entry discovery and in-game string tracking.
    """
    component_types = _split_csv(component_types_csv) or DEFAULT_TEXT_COMPONENT_TYPES
    limit = _normalize_limit(limit, default=500, minimum=1, maximum=10000)
    return await _invoke(
        "search_text",
        {
            "query": query,
            "scene_name": scene_name,
            "include_inactive": include_inactive,
            "match_mode": _normalize_match_mode(match_mode),
            "component_types": component_types,
            "limit": limit,
        },
    )


@mcp.tool()
async def get_text_context(
    component_instance_id: int,
    include_neighbors: bool = True,
    neighbor_depth: int = 1,
    include_sibling_texts: bool = True,
) -> dict[str, Any]:
    """Collect contextual data around one text component.

    Includes object path, parent chain, sibling texts, and common UI state flags.
    """
    neighbor_depth = _normalize_depth(neighbor_depth, default=1, minimum=0, maximum=4)
    return await _invoke(
        "get_text_context",
        {
            "component_instance_id": component_instance_id,
            "include_neighbors": include_neighbors,
            "neighbor_depth": neighbor_depth,
            "include_sibling_texts": include_sibling_texts,
        },
    )


@mcp.tool()
async def snapshot_gameobject(
    instance_id: int,
    include_children_depth: int = 1,
    include_components: bool = True,
    include_fields: bool = False,
    field_depth: int = 1,
) -> dict[str, Any]:
    """Capture structured snapshot of a GameObject subtree for offline analysis."""
    include_children_depth = _normalize_depth(include_children_depth, default=1, minimum=0, maximum=8)
    field_depth = _normalize_depth(field_depth, default=1, minimum=0, maximum=5)
    return await _invoke(
        "snapshot_gameobject",
        {
            "instance_id": instance_id,
            "include_children_depth": include_children_depth,
            "include_components": include_components,
            "include_fields": include_fields,
            "field_depth": field_depth,
        },
    )


@mcp.tool()
async def snapshot_scene(
    scene_name: str | None = None,
    scene_handle: int | None = None,
    hierarchy_depth: int = 2,
    include_components: bool = False,
    include_fields: bool = False,
    field_depth: int = 1,
) -> dict[str, Any]:
    """Capture scene-level snapshot for AI-assisted exploration and diffing."""
    hierarchy_depth = _normalize_depth(hierarchy_depth, default=2, minimum=0, maximum=8)
    field_depth = _normalize_depth(field_depth, default=1, minimum=0, maximum=5)
    return await _invoke(
        "snapshot_scene",
        {
            "scene_name": scene_name,
            "scene_handle": scene_handle,
            "hierarchy_depth": hierarchy_depth,
            "include_components": include_components,
            "include_fields": include_fields,
            "field_depth": field_depth,
        },
    )


@mcp.tool()
async def set_gameobject_active(instance_id: int, active: bool) -> dict[str, Any]:
    """Enable/disable a GameObject by instance id."""
    return await _invoke(
        "set_gameobject_active",
        {
            "instance_id": instance_id,
            "active": active,
        },
    )


@mcp.tool()
async def set_component_member(
    component_instance_id: int,
    member_name: str,
    value: Any,
    include_non_public: bool = False,
) -> dict[str, Any]:
    """Set one component field/property value.

    Use with care. Supports primitive values and common Unity structs (Vector/Color/Quaternion).
    """
    return await _invoke(
        "set_component_member",
        {
            "component_instance_id": component_instance_id,
            "member_name": member_name,
            "value": value,
            "include_non_public": include_non_public,
        },
    )


@mcp.tool()
async def set_text(
    component_instance_id: int,
    text: str,
    include_non_public: bool = True,
) -> dict[str, Any]:
    """Set text on a text-bearing component (UI.Text/TMP/custom text wrappers)."""
    return await _invoke(
        "set_text",
        {
            "component_instance_id": component_instance_id,
            "text": text,
            "include_non_public": include_non_public,
        },
    )


@mcp.tool()
async def capture_screenshot(
    output_path: str | None = None,
    super_size: int = 1,
    overwrite: bool = True,
) -> dict[str, Any]:
    """Capture current game screen and return PNG image content for the MCP client."""
    super_size = _normalize_limit(super_size, default=1, minimum=1, maximum=8)
    response = await _invoke(
        "capture_screenshot",
        {
            "output_path": output_path,
            "super_size": super_size,
            "overwrite": overwrite,
        },
    )
    return _build_screenshot_tool_result(response, requested_output_path=output_path)
