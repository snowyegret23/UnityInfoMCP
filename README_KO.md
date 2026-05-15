[English](README.md) | `한국어`

# UnityInfoMCP

UnityInfoMCP는 실행 중인 Unity 게임을 AI 클라이언트에서 조사하고 가볍게 수정하기 위한 로컬 MCP 툴킷입니다.

스크린샷이나 로그 조각만으로는 부족한 모딩, 현지화, UI 조사, 런타임 리버스 엔지니어링 작업에서 구조화된 Unity 씬 데이터를 AI가 직접 사용할 수 있게 만드는 것이 목적입니다.

## 구조

프로젝트는 두 부분으로 나뉩니다.

- `UnityInfoMCP`: AI 클라이언트가 연결하는 Python MCP 서버
- `UnityInfoBridge`: 게임 내부에서 동작하며 로컬 line-delimited JSON-RPC로 런타임 데이터를 노출하는 Unity 플러그인

이 분리 구조 덕분에 게임이 재시작되어도 MCP 엔드포인트는 안정적으로 유지됩니다. MCP 서버는 계속 실행해 두고, Unity 프로세스가 다시 켜지면 게임 내부 브리지만 다시 연결하면 됩니다.

## 포트와 전송 방식

연결은 두 종류입니다.

| 연결 | 기본값 | 설명 |
|---|---:|---|
| AI 클라이언트 -> `UnityInfoMCP` | `http://127.0.0.1:16000/mcp` | 기본값은 Streamable HTTP입니다. 프로세스 실행형 클라이언트는 `--transport stdio`를 사용합니다. |
| `UnityInfoMCP` -> `UnityInfoBridge` | `127.0.0.1:16001~16100` | 브리지 플러그인은 이 범위에서 첫 번째 사용 가능한 포트에 바인드됩니다. |

중요한 점:

- `--port`는 Streamable HTTP MCP 서버 포트만 바꿉니다.
- `--transport stdio`는 `/mcp`를 열지 않습니다. 클라이언트가 프로세스 stdio로 MCP를 사용합니다.
- `UNITY_INFO_BRIDGE_PORT`는 예전 고정 포트 브리지 구성을 위한 fallback 값입니다.
- 브리지 탐색은 설정된 fallback 포트가 자동 범위 밖에 있으면 그 포트도 시도한 뒤 `16001~16100`을 스캔합니다.
- 기본 구성에서 `16000`은 MCP HTTP 서버 포트이며, 일반적인 게임 브리지 포트가 아닙니다.

## 요구 사항

- Python 3.10+
- `mcp>=1.27.0`
- `pydantic>=2.0`
- `UnityInfoBridge` 빌드 시 .NET SDK 8.0+
- 지원되는 Unity 모드 로더:
  - BepInEx BE #754+ Mono
  - BepInEx BE #754+ IL2CPP
  - MelonLoader 0.7.2 Mono
  - MelonLoader 0.7.2 IL2CPP

## 버전 관리

릴리즈와 로컬 빌드 버전의 단일 기준은 저장소 루트의 `version.txt`입니다.

- Python 패키징은 `pyproject.toml`의 dynamic version metadata로 이 파일을 읽습니다.
- `UnityInfoBridge`는 MSBuild 컴파일 중 이 파일에서 `PluginMetadata.Version`을 생성합니다.
- GitHub 릴리즈 워크플로우도 같은 값을 읽어 산출물 이름, 태그, 릴리즈 이름에 사용합니다.

## 설치와 실행

가상환경을 만들고 Python MCP 서버를 설치합니다.

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -e .
```

릴리즈 패키징이나 PyInstaller 빌드가 필요하면 build extra로 설치합니다.

```powershell
pip install -e ".[build]"
```

기본 Streamable HTTP transport로 MCP 서버 실행:

```powershell
unity-info-mcp
```

동일한 모듈 직접 실행:

```powershell
python -m UnityInfoMCP
```

다른 HTTP 포트로 실행:

```powershell
unity-info-mcp --port 8080
```

프로세스를 직접 실행하는 클라이언트용 stdio 모드:

```powershell
unity-info-mcp --transport stdio
```

## MCP 클라이언트 설정

URL 기반 MCP 클라이언트는 다음 주소에 연결하면 됩니다.

```text
http://127.0.0.1:16000/mcp
```

프로세스를 직접 실행하는 클라이언트는 stdio 모드를 사용합니다.

Codex `config.toml` 예시:

```toml
[mcp_servers.UnityInfoMCP]
command = "C:\\MCP\\UnityInfoMCP_vx.x.x.exe"
args = ["--transport", "stdio"]
startup_timeout_sec = 45

[mcp_servers.UnityInfoMCP.env]
UNITY_INFO_BRIDGE_HOST = "127.0.0.1"
UNITY_INFO_BRIDGE_PORT = "16000"
```

Claude Desktop `claude_desktop_config.json` 예시:

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

## 환경 변수

| 변수 | 기본값 | 용도 |
|---|---|---|
| `UNITY_INFO_BRIDGE_TRANSPORT` | `tcp` | MCP 서버와 게임 브리지 사이의 전송 방식입니다. 현재 TCP만 구현되어 있습니다. |
| `UNITY_INFO_BRIDGE_HOST` | `127.0.0.1` | 브리지 호스트입니다. |
| `UNITY_INFO_BRIDGE_PORT` | `16000` | 레거시 fallback 브리지 포트입니다. 자동 탐색은 여전히 `16001~16100`을 스캔합니다. |
| `UNITY_INFO_BRIDGE_TIMEOUT_SEC` | `8.0` | 브리지 요청 제한 시간입니다. |
| `UNITY_INFO_MCP_NAME` | `UnityInfoMCP` | MCP 서버 이름입니다. |
| `UNITY_INFO_MCP_LOG_LEVEL` | `INFO` | Python 로그 레벨입니다. |

복사해서 쓸 템플릿은 `.env.example`에 있습니다.

## UnityInfoBridge 빌드

브리지 프로젝트는 `UnityInfoBridge/includes` 아래의 로컬 참조 DLL을 사용하며, 별도의 외부 의존성 동기화 단계가 필요 없습니다.

지원되는 모든 변형 빌드:

```powershell
Set-Location UnityInfoBridge
.\build.ps1
```

특정 변형만 빌드:

```powershell
Set-Location UnityInfoBridge
.\build.ps1 -Configurations Release_BepInEx_IL2CPP
```

빌드 출력:

- `UnityInfoBridge/Release/UnityInfoBridge.BepInEx.Mono/`
- `UnityInfoBridge/Release/UnityInfoBridge.BepInEx.IL2CPP/`
- `UnityInfoBridge/Release/UnityInfoBridge.MelonLoader.Mono/`
- `UnityInfoBridge/Release/UnityInfoBridge.MelonLoader.IL2CPP/`

각 출력에는 브리지 어셈블리와 `Newtonsoft.Json.dll`이 포함됩니다. IL2CPP 출력에는 `UnityInfoBridge.*.deps.json`도 포함됩니다.

## 릴리즈 패키지

릴리즈 워크플로우는 다음 파일을 생성합니다.

- `UnityInfoMCP_vx.x.x.exe`
- `UnityInfoMCP-vx.x.x.tar.gz`
- `UnityInfoMCP-vx.x.x-py3-none-any.whl`
- `UnityInfoBridge_vx.x.x_MelonLoader_Mono.zip`
- `UnityInfoBridge_vx.x.x_MelonLoader_IL2CPP.zip`
- `UnityInfoBridge_vx.x.x_BepInEx_Mono.zip`
- `UnityInfoBridge_vx.x.x_BepInEx_IL2CPP.zip`
- `SHA256SUMS.txt`

릴리즈 버전은 `version.txt`에서 읽습니다. 릴리즈할 때는 이 파일만 한 번 수정한 뒤 릴리즈 워크플로우를 실행하면 됩니다.

브리지 zip 내부 구조:

| 패키지 | 파일 |
|---|---|
| MelonLoader Mono | `Mods/UnityInfoBridge.dll`, `Mods/Newtonsoft.Json.dll` |
| MelonLoader IL2CPP | `Mods/UnityInfoBridge.dll`, `Mods/Newtonsoft.Json.dll`, `Mods/UnityInfoBridge.deps.json` |
| BepInEx Mono | `BepInEx/plugins/UnityInfoBridge/UnityInfoBridge.dll`, `BepInEx/plugins/UnityInfoBridge/Newtonsoft.Json.dll` |
| BepInEx IL2CPP | `BepInEx/plugins/UnityInfoBridge/UnityInfoBridge.dll`, `BepInEx/plugins/UnityInfoBridge/Newtonsoft.Json.dll`, `BepInEx/plugins/UnityInfoBridge/UnityInfoBridge.deps.json` |

게임의 로더와 런타임에 맞는 브리지 zip을 게임 루트에 압축 해제하면 됩니다.

## MCP 도구 구성

공개 도구들은 MCP 표준 discovery metadata를 포함합니다.

- `title`: 클라이언트와 도구 선택 UI에 쓰는 짧은 이름
- `description`: 모델이 도구 용도를 판단하기 위한 설명
- `inputSchema` 속성별 설명과 주요 범위
- `annotations.readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`
- `_meta.unityInfoMcp.category`, `_meta.unityInfoMcp.bridgeMethods`

도구 그룹:

| 그룹 | 도구 |
|---|---|
| 브리지/런타임 | `bridge_status`, `list_bridge_targets`, `select_bridge_target`, `get_runtime_summary` |
| 씬과 계층 | `list_scenes`, `get_scene_hierarchy`, `find_gameobjects_by_name`, `resolve_instance_id`, `get_gameobject`, `get_gameobject_by_path`, `get_gameobject_children` |
| 컴포넌트와 필드 | `get_components`, `get_component`, `get_component_fields`, `search_component_fields` |
| 텍스트와 현지화 | `list_text_elements`, `search_text`, `get_text_context` |
| 스냅샷 | `snapshot_gameobject`, `snapshot_scene` |
| 라이브 수정 | `set_gameobject_active`, `set_component_member`, `set_text` |
| 캡처 | `capture_screenshot` |

참고:

- 텍스트와 계층 도구는 Unity가 유효한 런타임 씬 오브젝트로 노출하는 `DontDestroyOnLoad` UI도 포함합니다.
- 라이브 수정 도구는 실행 중인 게임 상태만 바꾸며, 게임 자산을 패치하지 않습니다.
- `capture_screenshot`는 PNG 이미지 내용을 MCP 클라이언트로 반환합니다.
- `output_path`를 생략하면 브리지는 `GameRoot\UnityInfoBridge\captures\` 아래에 임시 캡처를 쓰고, MCP 서버가 이미지를 포함한 뒤 삭제합니다.
- PNG를 디스크에 남기려면 `output_path`를 명시하세요.

## 사용 예시

실행 중인 대사 텍스트가 어떤 폰트를 쓰는지 찾기:

```json
UnityInfoMCP.search_text({
  "query": "어디까지나 이리스의 의견이니까",
  "include_inactive": true,
  "limit": 10
})
```

일반적인 후속 호출:

```json
UnityInfoMCP.get_text_context({
  "component_instance_id": 485632
})
```

대상 UI 오브젝트를 위로 옮기기 위해 `RectTransform.anchoredPosition` 수정:

```json
UnityInfoMCP.set_component_member({
  "component_instance_id": 485632,
  "member_name": "anchoredPosition",
  "value": { "x": 0.0, "y": -258.0 },
  "include_non_public": false
})
```

런타임 오브젝트/컴포넌트 ID는 세션마다 달라집니다. 새 게임 실행마다 `search_text`, `find_gameobjects_by_name`, `get_components`, 스냅샷 도구로 다시 찾아야 합니다.

## 문서

- 브리지 프로토콜: `docs/bridge-protocol.md`
- 도구 카탈로그: `docs/tool-catalog.md`
