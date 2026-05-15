from __future__ import annotations

import asyncio
from inspect import cleandoc
import json
import logging
import os
from typing import Annotated, Any, Literal

from mcp.server.fastmcp import FastMCP
from mcp.server.fastmcp.utilities.types import Image
from mcp.types import CallToolResult, TextContent, ToolAnnotations
from pydantic import Field

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

BridgePortParam = Annotated[
    int | None,
    Field(
        ge=1,
        le=65535,
        description="UnityInfoBridge TCP port to target. Leave null with auto_select=true to choose the first discovered bridge.",
    ),
]
SceneNameParam = Annotated[
    str | None,
    Field(description="Unity scene name. Leave null to let the bridge use the active/default scene."),
]
SceneHandleParam = Annotated[
    int | None,
    Field(description="Unity scene handle. Use this instead of scene_name when scene names are duplicated."),
]
GameObjectInstanceIdParam = Annotated[
    int,
    Field(description="GameObject instance id returned by hierarchy, search, snapshot, or resolve tools."),
]
ComponentInstanceIdParam = Annotated[
    int,
    Field(description="Component instance id returned by get_components, text search, or component inspection tools."),
]
ObjectInstanceIdParam = Annotated[
    int,
    Field(description="UnityEngine.Object instance id from logs, hooks, hierarchy results, or other MCP tool output."),
]
IncludeInactiveParam = Annotated[
    bool,
    Field(description="Include inactive GameObjects/components. Keep true for localization and hidden UI investigation."),
]
IncludeNonPublicParam = Annotated[
    bool,
    Field(description="Include non-public fields/properties where bridge reflection support allows it."),
]
MatchModeParam = Annotated[
    MatchMode,
    Field(description="String matching mode: contains, exact, regex, starts_with, or ends_with."),
]
ComponentTypesCsvParam = Annotated[
    str,
    Field(description="Comma-separated text component filters. Use '*' for all supported UI.Text, TMP, and wrapper text types."),
]
LimitParam = Annotated[
    int,
    Field(ge=1, description="Maximum results to return. The server clamps this to a tool-specific safety limit."),
]
FieldDepthParam = Annotated[
    int,
    Field(ge=0, description="Recursive field/object serialization depth. Higher values are slower and noisier."),
]


def unity_tool(
    *,
    title: str,
    description: str,
    category: str,
    bridge_methods: str | list[str],
    read_only: bool,
    destructive: bool = False,
    idempotent: bool = False,
) -> Any:
    """Register a UnityInfoMCP tool with MCP-standard discovery metadata."""
    methods = [bridge_methods] if isinstance(bridge_methods, str) else bridge_methods
    return mcp.tool(
        title=title,
        description=cleandoc(description),
        annotations=ToolAnnotations(
            title=title,
            readOnlyHint=read_only,
            destructiveHint=None if read_only else destructive,
            idempotentHint=None if read_only else idempotent,
            openWorldHint=False,
        ),
        meta={
            "unityInfoMcp": {
                "category": category,
                "bridgeMethods": methods,
            },
        },
    )


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


@unity_tool(
    title="Discover Bridge Targets",
    category="bridge",
    bridge_methods="ping",
    read_only=True,
    description="""
    Scan localhost bridge ports and list every running UnityInfoBridge target.
    Use this when more than one Unity game may be running, or before selecting a target.
    """,
)
async def list_bridge_targets(
    include_runtime: Annotated[
        bool,
        Field(description="Ask each bridge for process, product, active scene, and runtime metadata during discovery."),
    ] = True,
    probe_timeout_sec: Annotated[
        float,
        Field(
            ge=0.1,
            le=2.0,
            description="Per-port probe timeout in seconds. Lower is faster; higher helps slow game startups.",
        ),
    ] = DEFAULT_PROBE_TIMEOUT_SEC,
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


@unity_tool(
    title="Select Bridge Target",
    category="bridge",
    bridge_methods="ping",
    read_only=False,
    destructive=False,
    idempotent=True,
    description="""
    Set the preferred UnityInfoBridge port for later tool calls in this MCP server process.
    Use this after list_bridge_targets when multiple games or bridge instances are visible.
    """,
)
async def select_bridge_target(
    port: BridgePortParam = None,
    auto_select: Annotated[
        bool,
        Field(description="When true, choose the first currently discovered bridge target instead of requiring a port."),
    ] = False,
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


@unity_tool(
    title="Check Bridge Status",
    category="bridge",
    bridge_methods=["ping", "get_capabilities"],
    read_only=True,
    description="""
    Verify that UnityInfoMCP can reach the in-game bridge and report the selected/configured target.
    Use this first when a workflow depends on live Unity runtime data.
    """,
)
async def bridge_status(
    include_capabilities: Annotated[
        bool,
        Field(description="Also request the bridge capability/method list when connected."),
    ] = True,
) -> dict[str, Any]:
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


@unity_tool(
    title="Get Runtime Summary",
    category="runtime",
    bridge_methods="get_runtime_summary",
    read_only=True,
    description="""
    Return process, Unity version, loaded scene count, active scene, and player/runtime state.
    Use this for a quick orientation before inspecting scenes or UI objects.
    """,
)
async def get_runtime_summary() -> dict[str, Any]:
    """Return game/runtime metadata (process, Unity version, loaded scenes count, player state)."""
    return await _invoke("get_runtime_summary")


@unity_tool(
    title="List Scenes",
    category="scene",
    bridge_methods="list_scenes",
    read_only=True,
    description="""
    List Unity scenes known to the runtime, including load/active flags and root object counts.
    Use this before scene-specific hierarchy or text searches when the target scene is unclear.
    """,
)
async def list_scenes(
    include_unloaded: Annotated[
        bool,
        Field(description="Include scenes known by build/settings even when they are not currently loaded."),
    ] = False,
) -> dict[str, Any]:
    """List scenes currently known to runtime with load/active flags and root counts."""
    return await _invoke("list_scenes", {"include_unloaded": include_unloaded})


@unity_tool(
    title="Get Scene Hierarchy",
    category="scene",
    bridge_methods="get_scene_hierarchy",
    read_only=True,
    description="""
    Fetch a bounded GameObject hierarchy tree for a scene.
    Use this to understand UI/layout structure, managers, and object paths before deep component inspection.
    """,
)
async def get_scene_hierarchy(
    scene_name: SceneNameParam = None,
    scene_handle: SceneHandleParam = None,
    depth_limit: Annotated[
        int,
        Field(ge=0, le=10, description="Hierarchy traversal depth from each root object. Use 0 for roots only."),
    ] = 2,
    include_components: Annotated[
        bool,
        Field(description="Include attached component names/ids in each GameObject node."),
    ] = False,
    include_inactive: IncludeInactiveParam = True,
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


@unity_tool(
    title="Find GameObjects By Name",
    category="object-search",
    bridge_methods="find_gameobjects_by_name",
    read_only=True,
    description="""
    Search GameObjects by name with exact, substring, prefix/suffix, or regex matching.
    Use this when you know a UI label, manager, prefab, button, menu, or controller name.
    """,
)
async def find_gameobjects_by_name(
    name_query: Annotated[
        str,
        Field(min_length=1, description="GameObject name fragment, full name, prefix/suffix, or regex pattern to find."),
    ],
    scene_name: SceneNameParam = None,
    match_mode: MatchModeParam = "contains",
    include_inactive: IncludeInactiveParam = True,
    limit: LimitParam = 200,
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


@unity_tool(
    title="Resolve Instance ID",
    category="object-inspection",
    bridge_methods="resolve_instance_id",
    read_only=True,
    description="""
    Resolve any UnityEngine.Object instance id into its runtime type, object kind, name, and optional relations.
    Use this when an id came from logs, hooks, another tool result, or decompiled/runtime evidence.
    """,
)
async def resolve_instance_id(
    instance_id: ObjectInstanceIdParam,
    include_relations: Annotated[
        bool,
        Field(description="Include parent, owner, scene, or related object links when available."),
    ] = True,
) -> dict[str, Any]:
    """Resolve any UnityEngine.Object instance id into typed runtime information."""
    return await _invoke(
        "resolve_instance_id",
        {
            "instance_id": instance_id,
            "include_relations": include_relations,
        },
    )


@unity_tool(
    title="Get GameObject",
    category="object-inspection",
    bridge_methods="get_gameobject",
    read_only=True,
    description="""
    Return one GameObject's scene, active state, path, transform summary, and identifying metadata.
    Use this after hierarchy/search tools return a GameObject instance id.
    """,
)
async def get_gameobject(
    instance_id: GameObjectInstanceIdParam,
    include_path: Annotated[
        bool,
        Field(description="Include the hierarchy path from the scene/root object to this GameObject."),
    ] = True,
) -> dict[str, Any]:
    """Get one GameObject by instance id, with scene/path and activation metadata."""
    return await _invoke(
        "get_gameobject",
        {
            "instance_id": instance_id,
            "include_path": include_path,
        },
    )


@unity_tool(
    title="Get GameObject By Path",
    category="object-inspection",
    bridge_methods="get_gameobject_by_path",
    read_only=True,
    description="""
    Find one GameObject by a slash-separated hierarchy path such as Canvas/MainMenu/StartButton.
    Use this when a stable path is known from a previous hierarchy snapshot or documentation.
    """,
)
async def get_gameobject_by_path(
    path: Annotated[
        str,
        Field(min_length=1, description="Slash-separated GameObject hierarchy path relative to the scene/root search context."),
    ],
    scene_name: SceneNameParam = None,
    include_inactive: IncludeInactiveParam = True,
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


@unity_tool(
    title="Get GameObject Children",
    category="object-inspection",
    bridge_methods="get_gameobject_children",
    read_only=True,
    description="""
    Enumerate child GameObjects below a known parent id.
    Use this to expand part of a hierarchy without taking a full scene snapshot.
    """,
)
async def get_gameobject_children(
    instance_id: GameObjectInstanceIdParam,
    recursive: Annotated[
        bool,
        Field(description="Traverse descendants recursively instead of only direct children."),
    ] = False,
    depth_limit: Annotated[
        int,
        Field(ge=0, le=12, description="Maximum descendant depth when recursive traversal is enabled."),
    ] = 2,
    include_components: Annotated[
        bool,
        Field(description="Include attached component names/ids for each child GameObject."),
    ] = False,
    include_inactive: IncludeInactiveParam = True,
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


@unity_tool(
    title="List GameObject Components",
    category="component-inspection",
    bridge_methods="get_components",
    read_only=True,
    description="""
    List components attached to a known GameObject, optionally including shallow field values.
    Use this before get_component or get_component_fields to choose the right component id.
    """,
)
async def get_components(
    gameobject_instance_id: Annotated[
        int,
        Field(description="GameObject instance id whose attached components should be listed."),
    ],
    include_fields: Annotated[
        bool,
        Field(description="Include selected field/property values in the component list response."),
    ] = False,
    field_depth: FieldDepthParam = 1,
    include_non_public: IncludeNonPublicParam = False,
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


@unity_tool(
    title="Inspect Component",
    category="component-inspection",
    bridge_methods="get_component",
    read_only=True,
    description="""
    Inspect one component instance with type/member metadata and optional field values.
    Use this for focused analysis after get_components identifies a relevant component id.
    """,
)
async def get_component(
    component_instance_id: ComponentInstanceIdParam,
    include_fields: Annotated[
        bool,
        Field(description="Include serialized field/property values along with component metadata."),
    ] = True,
    field_depth: FieldDepthParam = 2,
    include_non_public: IncludeNonPublicParam = False,
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


@unity_tool(
    title="Read Component Fields",
    category="component-inspection",
    bridge_methods="get_component_fields",
    read_only=True,
    description="""
    Read fields and optionally properties from one component with bounded recursive expansion.
    Use this for reverse engineering state, localization keys, backing fields, and runtime values.
    """,
)
async def get_component_fields(
    component_instance_id: ComponentInstanceIdParam,
    include_non_public: IncludeNonPublicParam = False,
    max_depth: FieldDepthParam = 2,
    include_properties: Annotated[
        bool,
        Field(description="Also call readable properties. This can be slower and may trigger property getter logic."),
    ] = False,
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


@unity_tool(
    title="Search Component Fields",
    category="component-search",
    bridge_methods="search_component_fields",
    read_only=True,
    description="""
    Search serialized component field/property values across scene objects.
    Use this to find localization keys, text backing fields, state flags, paths, asset names, or repeated tokens.
    """,
)
async def search_component_fields(
    value_query: Annotated[
        str,
        Field(min_length=1, description="String value, token, localization key, regex, or fragment to search for in component data."),
    ],
    scene_name: SceneNameParam = None,
    component_type: Annotated[
        str | None,
        Field(description="Optional component type/name filter, for example Text, TMP_Text, Button, or a game-specific class."),
    ] = None,
    field_name: Annotated[
        str | None,
        Field(description="Optional field/property name filter when the relevant member is already known."),
    ] = None,
    match_mode: MatchModeParam = "contains",
    include_inactive: IncludeInactiveParam = True,
    limit: LimitParam = 200,
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


@unity_tool(
    title="List Text Elements",
    category="localization",
    bridge_methods="list_text_elements",
    read_only=True,
    description="""
    Enumerate UI/text-bearing components, including visible and hidden localization candidates.
    Use this for broad translation audits when you do not yet know the exact source string.
    """,
)
async def list_text_elements(
    scene_name: SceneNameParam = None,
    include_inactive: IncludeInactiveParam = True,
    component_types_csv: ComponentTypesCsvParam = "*",
    limit: LimitParam = 500,
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


@unity_tool(
    title="Search Text Elements",
    category="localization",
    bridge_methods="search_text",
    read_only=True,
    description="""
    Search UI/text component values by keyword, exact value, prefix/suffix, or regex.
    Use this as the primary localization lookup when you have an on-screen string or known translation fragment.
    """,
)
async def search_text(
    query: Annotated[
        str,
        Field(min_length=1, description="Text fragment, full string, regex, or translation token to find in UI/text components."),
    ],
    scene_name: SceneNameParam = None,
    include_inactive: IncludeInactiveParam = True,
    match_mode: MatchModeParam = "contains",
    component_types_csv: ComponentTypesCsvParam = "*",
    limit: LimitParam = 500,
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


@unity_tool(
    title="Get Text Context",
    category="localization",
    bridge_methods="get_text_context",
    read_only=True,
    description="""
    Collect parent, sibling, neighbor, owner, and UI state context around one text component.
    Use this after search_text/list_text_elements to understand where a string appears and how it is used.
    """,
)
async def get_text_context(
    component_instance_id: ComponentInstanceIdParam,
    include_neighbors: Annotated[
        bool,
        Field(description="Include nearby hierarchy nodes around the text component."),
    ] = True,
    neighbor_depth: Annotated[
        int,
        Field(ge=0, le=4, description="How many parent/child neighbor levels to include around the text component."),
    ] = 1,
    include_sibling_texts: Annotated[
        bool,
        Field(description="Include text values from sibling UI elements for translation context."),
    ] = True,
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


@unity_tool(
    title="Snapshot GameObject Subtree",
    category="snapshot",
    bridge_methods="snapshot_gameobject",
    read_only=True,
    description="""
    Capture a structured snapshot of one GameObject and a bounded child subtree.
    Use this for offline analysis, comparison, or sharing context without repeatedly querying the live scene.
    """,
)
async def snapshot_gameobject(
    instance_id: GameObjectInstanceIdParam,
    include_children_depth: Annotated[
        int,
        Field(ge=0, le=8, description="Child subtree depth to include below the selected GameObject."),
    ] = 1,
    include_components: Annotated[
        bool,
        Field(description="Include attached component lists in the snapshot."),
    ] = True,
    include_fields: Annotated[
        bool,
        Field(description="Include component field values in the snapshot. This increases output size."),
    ] = False,
    field_depth: FieldDepthParam = 1,
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


@unity_tool(
    title="Snapshot Scene",
    category="snapshot",
    bridge_methods="snapshot_scene",
    read_only=True,
    description="""
    Capture a bounded structured snapshot of a scene hierarchy and optional component data.
    Use this for broad structure analysis or before/after diffs; prefer narrower tools for routine lookups.
    """,
)
async def snapshot_scene(
    scene_name: SceneNameParam = None,
    scene_handle: SceneHandleParam = None,
    hierarchy_depth: Annotated[
        int,
        Field(ge=0, le=8, description="Hierarchy depth to include from scene roots."),
    ] = 2,
    include_components: Annotated[
        bool,
        Field(description="Include attached component lists for GameObjects in the snapshot."),
    ] = False,
    include_fields: Annotated[
        bool,
        Field(description="Include component field values. Use sparingly because scene snapshots can become large."),
    ] = False,
    field_depth: FieldDepthParam = 1,
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


@unity_tool(
    title="Set GameObject Active",
    category="live-edit",
    bridge_methods="set_gameobject_active",
    read_only=False,
    destructive=True,
    idempotent=True,
    description="""
    Enable or disable a live GameObject by instance id.
    Use this only for runtime experiments, UI state testing, or modding investigation because it changes game state.
    """,
)
async def set_gameobject_active(
    instance_id: GameObjectInstanceIdParam,
    active: Annotated[
        bool,
        Field(description="Target active state for the GameObject."),
    ],
) -> dict[str, Any]:
    """Enable/disable a GameObject by instance id."""
    return await _invoke(
        "set_gameobject_active",
        {
            "instance_id": instance_id,
            "active": active,
        },
    )


@unity_tool(
    title="Set Component Member",
    category="live-edit",
    bridge_methods="set_component_member",
    read_only=False,
    destructive=True,
    idempotent=False,
    description="""
    Write one field or property on a live component, including primitive values and common Unity structs.
    Use this for controlled runtime experiments; prefer read-only inspection tools when you only need evidence.
    """,
)
async def set_component_member(
    component_instance_id: ComponentInstanceIdParam,
    member_name: Annotated[
        str,
        Field(min_length=1, description="Field or property name to set on the target component."),
    ],
    value: Annotated[
        Any,
        Field(description="New value. Primitives and bridge-supported Unity structs such as Vector, Color, and Quaternion are accepted."),
    ],
    include_non_public: IncludeNonPublicParam = False,
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


@unity_tool(
    title="Set Text",
    category="live-edit",
    bridge_methods="set_text",
    read_only=False,
    destructive=True,
    idempotent=True,
    description="""
    Replace the current value of a live text-bearing component.
    Use this for temporary translation/layout experiments in the running game; it does not patch game assets.
    """,
)
async def set_text(
    component_instance_id: ComponentInstanceIdParam,
    text: Annotated[
        str,
        Field(description="Text value to write into the target UI.Text, TMP, or custom text wrapper component."),
    ],
    include_non_public: IncludeNonPublicParam = True,
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


@unity_tool(
    title="Capture Screenshot",
    category="capture",
    bridge_methods="capture_screenshot",
    read_only=False,
    destructive=True,
    idempotent=False,
    description="""
    Capture the current Unity game frame and return PNG image content plus metadata to the MCP client.
    Provide output_path only when you want a retained file; overwrite=true may replace an existing file.
    """,
)
async def capture_screenshot(
    output_path: Annotated[
        str | None,
        Field(description="Optional file path where the bridge should save the PNG. Null returns an ephemeral image only."),
    ] = None,
    super_size: Annotated[
        int,
        Field(ge=1, le=8, description="Unity screenshot supersampling multiplier. Higher values create larger images."),
    ] = 1,
    overwrite: Annotated[
        bool,
        Field(description="Allow replacing output_path if that file already exists."),
    ] = True,
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
