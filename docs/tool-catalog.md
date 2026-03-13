# Tool Catalog

This file maps MCP tools to bridge methods and highlights why each tool is useful for game modding and localization analysis.

| MCP Tool | Bridge Method | Main Purpose |
|---|---|---|
| `bridge_status` | `ping`, `get_capabilities` | Check whether game bridge is reachable and what methods are implemented. |
| `list_bridge_targets` | `ping` (multi-port scan) | Discover all running game bridges on localhost (`16001~16100`). |
| `select_bridge_target` | `ping` | Choose which discovered game target subsequent tools should use. |
| `get_runtime_summary` | `get_runtime_summary` | Quick runtime metadata (process, Unity version, active scene). |
| `list_scenes` | `list_scenes` | Enumerate currently loaded scenes. |
| `get_scene_hierarchy` | `get_scene_hierarchy` | Inspect hierarchy tree for scene logic/UI discovery. |
| `find_gameobjects_by_name` | `find_gameobjects_by_name` | Name-based search (buttons, menus, controllers, managers). |
| `resolve_instance_id` | `resolve_instance_id` | Resolve raw IDs from logs/hooking outputs. |
| `get_gameobject` | `get_gameobject` | Object details for known instance id. |
| `get_gameobject_by_path` | `get_gameobject_by_path` | Path-based access to known hierarchy targets. |
| `get_gameobject_children` | `get_gameobject_children` | Traverse child tree from a parent object. |
| `get_components` | `get_components` | Component inventory per object. |
| `get_component` | `get_component` | Deep inspection for one component instance. |
| `get_component_fields` | `get_component_fields` | Field/property readout for reverse engineering. |
| `search_component_fields` | `search_component_fields` | String and value hunting in component state. |
| `list_text_elements` | `list_text_elements` | Enumerate candidate translatable text nodes. |
| `search_text` | `search_text` | Locate specific on-screen/internal text content, including persistent `DontDestroyOnLoad` UI. |
| `get_text_context` | `get_text_context` | Gather parent/sibling context around a text node. |
| `snapshot_gameobject` | `snapshot_gameobject` | Export subtree snapshot for offline AI analysis. |
| `snapshot_scene` | `snapshot_scene` | Export scene snapshot for broad structure analysis and diffing. |
| `set_gameobject_active` | `set_gameobject_active` | Toggle objects on and off to test state-dependent UI and flows. |
| `set_component_member` | `set_component_member` | Rewrite fields and properties, including common Unity structs such as positions and colors. |
| `set_text` | `set_text` | Temporarily patch live UI text without rebuilding the game. |
| `capture_screenshot` | `capture_screenshot` | Send the current game frame back to the MCP client as a PNG plus metadata. Provide `output_path` if you also want the file to remain on disk. |

## Typical localization workflow with these tools

1. `bridge_status`
2. `list_scenes`
3. `search_text` with known Korean/English fragments
4. `get_text_context` on matching component id
5. `get_components` and `get_component_fields` on owner object
6. `search_component_fields` for the same key/token across scene
7. `snapshot_gameobject` for persistent investigation

## Typical live-edit workflow with these tools

1. `bridge_status`
2. `search_text` or `find_gameobjects_by_name`
3. `get_components`
4. `set_component_member` for layout values such as `anchoredPosition`
5. `set_text` for quick translation or wording experiments
6. `capture_screenshot` to verify the result from the MCP client side
