# Bridge Protocol (UnityInfoBridge <-> UnityInfoMCP)

Transport: line-delimited JSON-RPC 2.0 over localhost TCP.

- Unity bridge auto-selects first free port in `16001~16100`.
- MCP side can scan/select targets in that range.
- `UNITY_INFO_BRIDGE_PORT` remains a legacy fallback for fixed-port setups.
- Bridge serialization prefers `Newtonsoft.Json` and falls back to the built-in `JsonWire` serializer when older runtimes reject `JsonConvert`.

- Request: one JSON object per line.
- Response: one JSON object per line.
- Encoding: UTF-8.

## Request shape

```json
{
  "jsonrpc": "2.0",
  "id": "uuid-string",
  "method": "list_scenes",
  "params": {}
}
```

## Success response shape

```json
{
  "jsonrpc": "2.0",
  "id": "uuid-string",
  "result": {
    "items": []
  }
}
```

## Error response shape

```json
{
  "jsonrpc": "2.0",
  "id": "uuid-string",
  "error": {
    "code": -32010,
    "message": "game_not_connected",
    "data": {
      "state": "waiting_for_unity"
    }
  }
}
```

## Suggested error codes

- `-32601`: method_not_found
- `-32602`: invalid_params
- `-32010`: game_not_connected
- `-32011`: object_not_found
- `-32012`: scene_not_found
- `-32013`: component_not_found
- `-32021`: file_exists
- `-32022`: capture_failed
- `-32050`: internal_bridge_error

## Required methods

### Runtime

- `ping(params: { include_runtime?: bool })`
- `get_capabilities(params: {})`
- `get_runtime_summary(params: {})`

### Scene and hierarchy

- `list_scenes(params: { include_unloaded?: bool })`
- `get_scene_hierarchy(params: { scene_name?: string, scene_handle?: number, depth_limit?: number, include_components?: bool, include_inactive?: bool })`
- `find_gameobjects_by_name(params: { name_query: string, scene_name?: string, match_mode?: string, include_inactive?: bool, limit?: number })`
- `resolve_instance_id(params: { instance_id: number, include_relations?: bool })`
- `get_gameobject(params: { instance_id: number, include_path?: bool })`
- `get_gameobject_by_path(params: { path: string, scene_name?: string, include_inactive?: bool })`
- `get_gameobject_children(params: { instance_id: number, recursive?: bool, depth_limit?: number, include_components?: bool, include_inactive?: bool })`

### Components and fields

- `get_components(params: { gameobject_instance_id: number, include_fields?: bool, field_depth?: number, include_non_public?: bool })`
- `get_component(params: { component_instance_id: number, include_fields?: bool, field_depth?: number, include_non_public?: bool })`
- `get_component_fields(params: { component_instance_id: number, include_non_public?: bool, max_depth?: number, include_properties?: bool })`
- `search_component_fields(params: { value_query: string, scene_name?: string, component_type?: string, field_name?: string, match_mode?: string, include_inactive?: bool, limit?: number })`

### Text and localization discovery

- `list_text_elements(params: { scene_name?: string, include_inactive?: bool, component_types?: string[], limit?: number })`
- `search_text(params: { query: string, scene_name?: string, include_inactive?: bool, match_mode?: string, component_types?: string[], limit?: number })`
- `get_text_context(params: { component_instance_id: number, include_neighbors?: bool, neighbor_depth?: number, include_sibling_texts?: bool })`

### Snapshot export

- `snapshot_gameobject(params: { instance_id: number, include_children_depth?: number, include_components?: bool, include_fields?: bool, field_depth?: number })`
- `snapshot_scene(params: { scene_name?: string, scene_handle?: number, hierarchy_depth?: number, include_components?: bool, include_fields?: bool, field_depth?: number })`

### Runtime mutation

- `set_gameobject_active(params: { instance_id: number, active: bool })`
- `set_component_member(params: { component_instance_id: number, member_name: string, value: any, include_non_public?: bool })`
- `set_text(params: { component_instance_id: number, text: string, include_non_public?: bool })`

### Capture

- `capture_screenshot(params: { output_path?: string, super_size?: number, overwrite?: bool })`

## Data design notes for bridge implementation

- Always include stable identifiers:
  - `instance_id`
  - `scene_handle`
  - `component_type`
- Include `path` for game objects where possible.
- For text elements include:
  - source component type
  - current text
  - owning object path
  - active/enabled flags
- Bridge-side hierarchy/text discovery should include persistent `DontDestroyOnLoad` UI where Unity exposes it as a valid scene object.
- `list_scenes` currently returns loaded scenes only. `include_unloaded` is accepted for forward compatibility.
- `set_component_member` accepts structured JSON values and also tuple-style strings for common Unity structs such as `Vector2`, `Vector3`, `Quaternion`, and `Color`.
- `capture_screenshot` should default to `GameRoot/UnityInfoBridge/captures/capture_yy-MM-dd-HH-mm-ss-fff.png` when `output_path` is omitted.
- Mutation methods are explicit and opt-in. Read-only inspection remains the default usage pattern.
