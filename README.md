`English` | [한국어](README_KO.md)

# UnityInfoMCP

Unity runtime inspection toolkit for MCP-based automation.

This repository contains two parts:
- `UnityInfoMCP`: an external Python MCP server
- `UnityInfoBridge`: an in-game Unity plugin that exposes runtime data over local JSON-RPC

The split is intentional:
- the MCP server can stay alive while games restart
- AI clients keep a stable MCP endpoint
- the game-side bridge can reconnect whenever the game launches again

## Port layout

This is the part that matters most in practice:
- MCP server: `127.0.0.1:16000` by default
- Game bridge: first free port in `127.0.0.1:16001~16100`

These are different endpoints.
- `--port` changes the MCP server port
- `UNITY_INFO_BRIDGE_PORT` is only a legacy fallback for bridge connection attempts
- bridge auto-discovery still scans `16001~16100`

There are also two separate transport layers:
- Client -> `UnityInfoMCP`: Streamable HTTP
- `UnityInfoMCP` -> `UnityInfoBridge`: TCP

## Repository layout

- `UnityInfoMCP`
  The Python MCP package
- `UnityInfoBridge`
  The Unity plugin project
- `UnityInfoBridge/includes`
  Reference DLLs used to build the bridge
- `docs`
  Bridge protocol and tool mapping documents

## Supported bridge targets

`UnityInfoBridge` currently targets:
- `BepInEx BE #754+` Mono
- `BepInEx BE #754+` IL2CPP
- `MelonLoader 0.7.2` Mono
- `MelonLoader 0.7.2` IL2CPP

## Running the MCP server

Create and activate a virtual environment:

```bash
python -m venv .venv
. .venv/Scripts/activate
pip install -e .
```

Run the MCP server on the default HTTP port:

```bash
unity-info-mcp
```

If the `unity-info-mcp` command is not found on Windows, use:

```bash
python -m UnityInfoMCP
```

That usually means Python's user `Scripts` directory is not on `PATH`.
In this environment, the generated launcher is installed at:

```text
C:\Users\USER\AppData\Local\Python\pythoncore-3.12-64\Scripts\unity-info-mcp.exe
```

Run it on a different port:

```bash
unity-info-mcp --port 8080
```

Behavior:
- MCP transport: Streamable HTTP
- default bind: `127.0.0.1:16000`
- startup failure: prints the error and waits for `Enter` before exiting

## MCP client configuration

Recommended when `unity-info-mcp` is available on `PATH`:

```toml
[mcp_servers.UnityInfoMCP]
command = "unity-info-mcp"
args = []
startup_timeout_sec = 45

[mcp_servers.UnityInfoMCP.env]
UNITY_INFO_BRIDGE_HOST = "127.0.0.1"
UNITY_INFO_BRIDGE_PORT = "16000"
```

If you prefer to invoke the module directly:

```toml
[mcp_servers.UnityInfoMCP]
command = "python"
args = ["-m", "UnityInfoMCP"]
startup_timeout_sec = 45

[mcp_servers.UnityInfoMCP.env]
UNITY_INFO_BRIDGE_HOST = "127.0.0.1"
UNITY_INFO_BRIDGE_PORT = "16000"
```

If neither `unity-info-mcp` nor `python` is reliably on `PATH`, use an explicit interpreter path:

```toml
[mcp_servers.UnityInfoMCP]
command = 'C:\path\to\.venv\Scripts\python.exe'
args = ["-m", "UnityInfoMCP"]
startup_timeout_sec = 45

[mcp_servers.UnityInfoMCP.env]
UNITY_INFO_BRIDGE_HOST = "127.0.0.1"
UNITY_INFO_BRIDGE_PORT = "16000"
```

## Environment variables

- `UNITY_INFO_BRIDGE_TRANSPORT`
  Default: `tcp`
  Transport used between `UnityInfoMCP` and the game-side `UnityInfoBridge`
- `UNITY_INFO_BRIDGE_HOST`
  Default: `127.0.0.1`
- `UNITY_INFO_BRIDGE_PORT`
  Default: `16000`
  Legacy fallback bridge port only
- `UNITY_INFO_BRIDGE_TIMEOUT_SEC`
  Default: `8.0`
- `UNITY_INFO_MCP_NAME`
  Default: `UnityInfoMCP`
- `UNITY_INFO_MCP_LOG_LEVEL`
  Default: `INFO`

Use `.env.example` as a starting point if needed.

## Building UnityInfoBridge

Build inputs:
- bridge references are resolved from `UnityInfoBridge/includes`
- the project only uses local reference DLLs under:
  `UnityInfoBridge/includes/bepinex/mono`
  `UnityInfoBridge/includes/bepinex/il2cpp`
  `UnityInfoBridge/includes/melonloader/mono`
  `UnityInfoBridge/includes/melonloader/il2cpp`
  `UnityInfoBridge/includes/unity/mono`
  `UnityInfoBridge/includes/unity/il2cpp`
- `UnityInfoBridge/build.ps1` builds all supported variants

The repository already includes the required reference DLLs, so builds do not depend on any extra sync step.

Typical build:

```powershell
Set-Location UnityInfoBridge
.\build.ps1
```

Build specific targets:

```powershell
Set-Location UnityInfoBridge
.\build.ps1 -Configurations Release_BepInEx_IL2CPP
```

Build outputs:
- `UnityInfoBridge/Release/UnityInfoBridge.BepInEx.Mono/`
- `UnityInfoBridge/Release/UnityInfoBridge.BepInEx.IL2CPP/`
- `UnityInfoBridge/Release/UnityInfoBridge.MelonLoader.Mono/`
- `UnityInfoBridge/Release/UnityInfoBridge.MelonLoader.IL2CPP/`

## Release assets

The GitHub release workflow produces:
- `UnityInfoMCP_vx.x.x.exe`
- `UnityInfoBridge_vx.x.x_MelonLoader_Mono.zip`
- `UnityInfoBridge_vx.x.x_MelonLoader_IL2CPP.zip`
- `UnityInfoBridge_vx.x.x_BepInEx_Mono.zip`
- `UnityInfoBridge_vx.x.x_BepInEx_IL2CPP.zip`

Package structure:
- MelonLoader zip: `Mods/UnityInfoBridge.dll`
- BepInEx zip: `BepInEx/plugins/UnityInfoBridge/UnityInfoBridge.dll`

## MCP tool surface

Runtime:
- `bridge_status`
- `list_bridge_targets`
- `select_bridge_target`
- `get_runtime_summary`

Scene and hierarchy:
- `list_scenes`
- `get_scene_hierarchy`
- `find_gameobjects_by_name`
- `resolve_instance_id`
- `get_gameobject`
- `get_gameobject_by_path`
- `get_gameobject_children`

Components and fields:
- `get_components`
- `get_component`
- `get_component_fields`
- `search_component_fields`

Text and localization discovery:
- `list_text_elements`
- `search_text`
- `get_text_context`

Snapshots:
- `snapshot_gameobject`
- `snapshot_scene`

## Example workflow

Find which font a live dialogue line is using:

User prompt:

```text
"어디까지나 이리스의 의견이니까"라는 텍스트가 어느 폰트를 사용하고 있는지 알려줘.
```

Primary tool call:

```json
UnityInfoMCP.search_text({
  "query": "어디까지나 이리스의 의견이니까",
  "include_inactive": true,
  "limit": 10
})
```

Typical result summary:
- scene: `Search`
- object path: `_root/Canvas2/ScreenScaler2/GameObject/messagewindow/messagearea/text_message (TMP)`
- component type: `TMPro.TextMeshProUGUI`
- current TMP font asset: `message#en-font`

Move the same text up by `100px`:

User prompt:

```text
그 텍스트를 위로 100px 올려줘.
```

Tool flow:

```json
UnityInfoMCP.get_components({
  "gameobject_instance_id": 480506,
  "include_fields": true,
  "include_non_public": false,
  "field_depth": 1
})
```

```json
UnityInfoMCP.set_component_member({
  "component_instance_id": 485632,
  "member_name": "anchoredPosition",
  "value": "{\"x\":0.0,\"y\":-258.0}",
  "include_non_public": false
})
```

Verification:

```json
UnityInfoMCP.get_component_fields({
  "component_instance_id": 485632,
  "include_non_public": false,
  "include_properties": true,
  "max_depth": 1
})
```

Typical result summary:
- target component: `UnityEngine.RectTransform`
- `anchoredPosition`: `(0.0, -358.0)` -> `(0.0, -258.0)`
- effective change: moved upward by `100px`

Runtime object IDs such as `480506` and `485632` are example values and will differ each session.

## Documentation

- Bridge protocol: `docs/bridge-protocol.md`
- Tool mapping: `docs/tool-catalog.md`
