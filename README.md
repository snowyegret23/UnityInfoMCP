`English` | [한국어](README_KO.md)

# UnityInfoMCP

UnityInfoMCP is a local MCP toolkit for inspecting and lightly editing a running Unity game from an AI client.

It is built for modding, localization, UI investigation, and runtime reverse engineering workflows where the AI needs structured Unity scene data instead of screenshots or log snippets alone.

## Architecture

The project has two parts:

- `UnityInfoMCP`: a Python MCP server that the AI client connects to.
- `UnityInfoBridge`: an in-game Unity plugin that exposes runtime data over local line-delimited JSON-RPC.

The split keeps the MCP endpoint stable while games restart. The MCP server can stay running, and the game-side bridge reconnects when the Unity process launches again.

## Ports And Transports

There are two separate connections:

| Connection | Default | Notes |
|---|---:|---|
| AI client -> `UnityInfoMCP` | `http://127.0.0.1:16000/mcp` | Streamable HTTP by default. Use `--transport stdio` for process-launched clients. |
| `UnityInfoMCP` -> `UnityInfoBridge` | `127.0.0.1:16001~16100` | The bridge plugin binds the first free port in this range. |

Important details:

- `--port` only changes the Streamable HTTP MCP server port.
- `--transport stdio` does not open `/mcp`; the client speaks MCP over process stdio.
- `UNITY_INFO_BRIDGE_PORT` is a legacy fixed-port fallback for old bridge setups.
- Bridge discovery tries the configured fallback port when it is outside the auto range, then scans `16001~16100`.
- Port `16000` is reserved for the MCP HTTP server in the default setup, not for the normal game bridge.

## Requirements

- Python 3.10+
- `mcp>=1.27.0`
- `pydantic>=2.0`
- .NET SDK 8.0+ when building `UnityInfoBridge`
- A supported Unity mod loader for the bridge plugin:
  - BepInEx BE #754+ Mono
  - BepInEx BE #754+ IL2CPP
  - MelonLoader 0.7.2 Mono
  - MelonLoader 0.7.2 IL2CPP

## Versioning

The single source of truth for release/local build versioning is `version.txt` in the repository root.

- Python packaging reads it through `pyproject.toml` dynamic version metadata.
- `UnityInfoBridge` generates `PluginMetadata.Version` from it during MSBuild compilation.
- The GitHub release workflow reads it and uses the same value for artifact names, tags, and release names.

## Install And Run

Create a virtual environment and install the Python MCP server:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -e .
```

For release packaging or PyInstaller builds:

```powershell
pip install -e ".[build]"
```

Run the MCP server with the default Streamable HTTP transport:

```powershell
unity-info-mcp
```

Equivalent module invocation:

```powershell
python -m UnityInfoMCP
```

Run on another HTTP port:

```powershell
unity-info-mcp --port 8080
```

Run over stdio for clients that launch the process directly:

```powershell
unity-info-mcp --transport stdio
```

## MCP Client Configuration

URL-based MCP clients should connect to:

```text
http://127.0.0.1:16000/mcp
```

For process-launching clients, use stdio mode.

Codex `config.toml` example:

```toml
[mcp_servers.UnityInfoMCP]
command = "C:\\MCP\\UnityInfoMCP_vx.x.x.exe"
args = ["--transport", "stdio"]
startup_timeout_sec = 45

[mcp_servers.UnityInfoMCP.env]
UNITY_INFO_BRIDGE_HOST = "127.0.0.1"
UNITY_INFO_BRIDGE_PORT = "16000"
```

Claude Desktop `claude_desktop_config.json` example:

```json
{
  "mcpServers": {
    "UnityInfoMCP": {
      "command": "C:\\MCP\\UnityInfoMCP_vx.x.x.exe",
      "args": ["--transport", "stdio"],
      "env": {
        "UNITY_INFO_BRIDGE_HOST": "127.0.0.1",
        "UNITY_INFO_BRIDGE_PORT": "16000"
      }
    }
  }
}
```

## Environment Variables

| Variable | Default | Purpose |
|---|---|---|
| `UNITY_INFO_BRIDGE_TRANSPORT` | `tcp` | Transport between the MCP server and game bridge. Only TCP is currently implemented. |
| `UNITY_INFO_BRIDGE_HOST` | `127.0.0.1` | Bridge host. |
| `UNITY_INFO_BRIDGE_PORT` | `16000` | Legacy fallback bridge port. Auto discovery still scans `16001~16100`. |
| `UNITY_INFO_BRIDGE_TIMEOUT_SEC` | `8.0` | Bridge request timeout. |
| `UNITY_INFO_MCP_NAME` | `UnityInfoMCP` | MCP server name. |
| `UNITY_INFO_MCP_LOG_LEVEL` | `INFO` | Python log level. |

See `.env.example` for a copyable template.

## Build UnityInfoBridge

The bridge project uses local reference DLLs from `UnityInfoBridge/includes` and does not require an external dependency sync step.

Build all supported variants:

```powershell
Set-Location UnityInfoBridge
.\build.ps1
```

Build a specific variant:

```powershell
Set-Location UnityInfoBridge
.\build.ps1 -Configurations Release_BepInEx_IL2CPP
```

Build outputs:

- `UnityInfoBridge/Release/UnityInfoBridge.BepInEx.Mono/`
- `UnityInfoBridge/Release/UnityInfoBridge.BepInEx.IL2CPP/`
- `UnityInfoBridge/Release/UnityInfoBridge.MelonLoader.Mono/`
- `UnityInfoBridge/Release/UnityInfoBridge.MelonLoader.IL2CPP/`

Each output includes the bridge assembly and `Newtonsoft.Json.dll`. IL2CPP outputs also include `UnityInfoBridge.*.deps.json`.

## Release Packages

The release workflow produces:

- `UnityInfoMCP_vx.x.x.exe`
- `UnityInfoMCP-vx.x.x.tar.gz`
- `UnityInfoMCP-vx.x.x-py3-none-any.whl`
- `UnityInfoBridge_vx.x.x_MelonLoader_Mono.zip`
- `UnityInfoBridge_vx.x.x_MelonLoader_IL2CPP.zip`
- `UnityInfoBridge_vx.x.x_BepInEx_Mono.zip`
- `UnityInfoBridge_vx.x.x_BepInEx_IL2CPP.zip`
- `SHA256SUMS.txt`

Release versioning comes from `version.txt`. Update that file once, then run the release workflow.

Bridge zip layout:

| Package | Files |
|---|---|
| MelonLoader Mono | `Mods/UnityInfoBridge.dll`, `Mods/Newtonsoft.Json.dll` |
| MelonLoader IL2CPP | `Mods/UnityInfoBridge.dll`, `Mods/Newtonsoft.Json.dll`, `Mods/UnityInfoBridge.deps.json` |
| BepInEx Mono | `BepInEx/plugins/UnityInfoBridge/UnityInfoBridge.dll`, `BepInEx/plugins/UnityInfoBridge/Newtonsoft.Json.dll` |
| BepInEx IL2CPP | `BepInEx/plugins/UnityInfoBridge/UnityInfoBridge.dll`, `BepInEx/plugins/UnityInfoBridge/Newtonsoft.Json.dll`, `BepInEx/plugins/UnityInfoBridge/UnityInfoBridge.deps.json` |

Extract the bridge zip that matches the game's loader/runtime into the game root.

## MCP Tool Surface

The public tools are registered with MCP-standard discovery metadata:

- `title`: short display name for clients and tool pickers.
- `description`: model-facing usage guidance.
- `inputSchema` property descriptions and ranges.
- `annotations.readOnlyHint`, `destructiveHint`, `idempotentHint`, and `openWorldHint`.
- `_meta.unityInfoMcp.category` and `_meta.unityInfoMcp.bridgeMethods` for clients that preserve `_meta`.

Tool groups:

| Group | Tools |
|---|---|
| Bridge/runtime | `bridge_status`, `list_bridge_targets`, `select_bridge_target`, `get_runtime_summary` |
| Scene and hierarchy | `list_scenes`, `get_scene_hierarchy`, `find_gameobjects_by_name`, `resolve_instance_id`, `get_gameobject`, `get_gameobject_by_path`, `get_gameobject_children` |
| Components and fields | `get_components`, `get_component`, `get_component_fields`, `search_component_fields` |
| Text and localization | `list_text_elements`, `search_text`, `get_text_context` |
| Snapshots | `snapshot_gameobject`, `snapshot_scene` |
| Live edits | `set_gameobject_active`, `set_component_member`, `set_text` |
| Capture | `capture_screenshot` |

Notes:

- Text and hierarchy tools include persistent `DontDestroyOnLoad` UI when Unity exposes it as a valid runtime scene object.
- Live-edit tools change the running game only; they do not patch game assets.
- `capture_screenshot` returns PNG image content to the MCP client.
- If `output_path` is omitted, the bridge writes a temporary capture under `GameRoot\UnityInfoBridge\captures\` and the MCP server deletes it after embedding the image.
- Provide `output_path` when the PNG should remain on disk.

## Example Workflow

Find which font a live dialogue line is using:

```json
UnityInfoMCP.search_text({
  "query": "어디까지나 이리스의 의견이니까",
  "include_inactive": true,
  "limit": 10
})
```

Typical follow-up:

```json
UnityInfoMCP.get_text_context({
  "component_instance_id": 485632
})
```

Move the owning UI object up by changing its `RectTransform.anchoredPosition`:

```json
UnityInfoMCP.set_component_member({
  "component_instance_id": 485632,
  "member_name": "anchoredPosition",
  "value": { "x": 0.0, "y": -258.0 },
  "include_non_public": false
})
```

Runtime object and component IDs are session-specific. Re-discover them with `search_text`, `find_gameobjects_by_name`, `get_components`, or snapshots for each new game run.

## Documentation

- Bridge protocol: `docs/bridge-protocol.md`
- Tool catalog: `docs/tool-catalog.md`
