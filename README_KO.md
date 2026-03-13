[English](README.md) | `한국어`

# UnityInfoMCP

MCP 기반 자동화를 위한 Unity 런타임 조사 툴킷입니다.

이 저장소는 두 부분으로 구성됩니다.
- `UnityInfoMCP`: 외부에서 실행하는 Python MCP 서버
- `UnityInfoBridge`: 게임 내부에서 동작하는 Unity 플러그인

이 구조를 분리한 이유는 명확합니다.
- 게임이 재시작되어도 MCP 서버는 계속 살아 있을 수 있습니다.
- AI 클라이언트는 안정적인 MCP 엔드포인트를 유지할 수 있습니다.
- 게임이 다시 켜지면 브리지만 재연결하면 됩니다.

## 포트 구성

실사용에서 가장 중요한 부분입니다.
- MCP 서버: 기본값 `http://127.0.0.1:16000/mcp`
- 게임 브리지: `127.0.0.1:16001~16100` 범위에서 첫 번째 사용 가능한 포트

이 둘은 서로 다른 엔드포인트입니다.
- `--port`는 MCP 서버 포트를 바꿉니다.
- `UNITY_INFO_BRIDGE_PORT`는 브리지 연결 시도용 레거시 fallback 값입니다.
- 브리지 자동 탐색은 여전히 `16001~16100`을 스캔합니다.
- `16000`은 `UnityInfoMCP` HTTP 서버 포트입니다. 게임 내부의 `UnityInfoBridge` 플러그인 자체는 `16001~16100` 중 첫 번째 사용 가능한 포트에 바인드됩니다.

전송 계층도 두 종류가 따로 있습니다.
- 클라이언트 -> `UnityInfoMCP`: Streamable HTTP
- `UnityInfoMCP` -> `UnityInfoBridge`: TCP

## 저장소 구성

- `UnityInfoMCP`
  Python MCP 패키지
- `UnityInfoBridge`
  Unity 플러그인 프로젝트
- `UnityInfoBridge/includes`
  브리지 빌드에 사용하는 참조 DLL
- `docs`
  브리지 프로토콜과 도구 매핑 문서

## 지원되는 브리지 대상

현재 `UnityInfoBridge`는 다음 조합을 대상으로 합니다.
- `BepInEx BE #754+` Mono
- `BepInEx BE #754+` IL2CPP
- `MelonLoader 0.7.2` Mono
- `MelonLoader 0.7.2` IL2CPP

## MCP 서버 실행

가상환경 생성 및 활성화:

```bash
python -m venv .venv
. .venv/Scripts/activate
pip install -e .
```

PyInstaller나 릴리즈 빌드를 할 때는 build extra로 설치하세요:

```bash
pip install -e ".[build]"
```

기본 HTTP 포트로 MCP 서버 실행:

```bash
unity-info-mcp
```

Windows에서 `unity-info-mcp` 명령이 인식되지 않으면 다음처럼 실행하면 됩니다.

```bash
python -m UnityInfoMCP
```

이 경우는 보통 Python 사용자 `Scripts` 경로가 `PATH`에 없다는 뜻입니다.
현재 환경에서 생성된 실행 파일 위치는 다음입니다.

```text
C:\Users\USER\AppData\Local\Python\pythoncore-3.12-64\Scripts\unity-info-mcp.exe
```

다른 포트로 실행:

```bash
unity-info-mcp --port 8080
```

동작 방식:
- MCP transport: Streamable HTTP
- 기본 바인드: `127.0.0.1:16000`
- 기본 MCP 엔드포인트: `http://127.0.0.1:16000/mcp`
- 시작 실패 시: 오류를 출력하고 `Enter` 입력을 기다린 뒤 종료

## MCP 클라이언트 설정 예시

`UnityInfoMCP`는 stdio MCP 서버가 아니라 HTTP MCP 서버입니다.

즉 MCP 클라이언트는 다음과 같은 URL에 연결해야 합니다.

```text
http://127.0.0.1:16000/mcp
```

서버를 다른 포트로 실행했다면 그 포트에 맞는 엔드포인트를 사용하면 됩니다.

```text
http://127.0.0.1:8080/mcp
```

서버 프로세스를 띄울 때는 브리지 쪽 환경 변수를 같이 줄 수 있습니다.

```powershell
$env:UNITY_INFO_BRIDGE_HOST = "127.0.0.1"
$env:UNITY_INFO_BRIDGE_PORT = "16000"
unity-info-mcp
```

중요한 점:
- `UNITY_INFO_BRIDGE_PORT=16000`은 레거시 fallback 브리지 포트일 뿐입니다.
- 일반적인 브리지 자동 탐색은 여전히 `16001~16100`을 먼저 스캔합니다.
- MCP 클라이언트가 프로세스를 대신 실행해 주더라도, 그 프로세스는 stdio가 아니라 HTTP 서버를 띄웁니다.

## 직접 실행

MCP 클라이언트 설정과 별개로 `UnityInfoMCP` 자체를 직접 실행할 수도 있습니다.

`unity-info-mcp`가 `PATH`에 있을 때:

```bash
unity-info-mcp
```

모듈을 직접 실행할 때:

```bash
python -m UnityInfoMCP
```

다른 포트로 띄울 때:

```bash
unity-info-mcp --port 8080
```

PyInstaller로 만든 실행 파일이 있다면 그것도 직접 실행할 수 있습니다.

```powershell
& "C:\path\to\UnityInfoMCP_v1.0.1.exe"
```

이 직접 실행 방식들은 모두 `UnityInfoMCP`의 Streamable HTTP 서버를 시작하고, 선택된 포트의 `/mcp`를 노출합니다.

## 환경 변수

- `UNITY_INFO_BRIDGE_TRANSPORT`
  기본값: `tcp`
  `UnityInfoMCP`와 게임 내부 `UnityInfoBridge` 사이에서 사용하는 전송 방식
- `UNITY_INFO_BRIDGE_HOST`
  기본값: `127.0.0.1`
- `UNITY_INFO_BRIDGE_PORT`
  기본값: `16000`
  브리지 연결용 레거시 fallback 포트입니다. 자동 탐색은 여전히 `16001~16100`을 스캔합니다.
- `UNITY_INFO_BRIDGE_TIMEOUT_SEC`
  기본값: `8.0`
- `UNITY_INFO_MCP_NAME`
  기본값: `UnityInfoMCP`
- `UNITY_INFO_MCP_LOG_LEVEL`
  기본값: `INFO`

필요하면 `.env.example`을 시작점으로 사용할 수 있습니다.

## UnityInfoBridge 빌드

빌드 입력:
- 브리지 참조 DLL은 `UnityInfoBridge/includes`에서 가져옵니다.
- 프로젝트는 아래 로컬 참조 DLL 경로만 사용합니다.
  `UnityInfoBridge/includes/bepinex/mono`
  `UnityInfoBridge/includes/bepinex/il2cpp`
  `UnityInfoBridge/includes/melonloader/mono`
  `UnityInfoBridge/includes/melonloader/il2cpp`
  `UnityInfoBridge/includes/unity/mono`
  `UnityInfoBridge/includes/unity/il2cpp`
- `UnityInfoBridge/build.ps1`는 지원되는 모든 변형을 빌드합니다.

필요한 참조 DLL은 이미 저장소에 포함되어 있으므로, 별도의 동기화 단계 없이 바로 빌드할 수 있습니다.

일반적인 빌드:

```powershell
Set-Location UnityInfoBridge
.\build.ps1
```

특정 대상만 빌드:

```powershell
Set-Location UnityInfoBridge
.\build.ps1 -Configurations Release_BepInEx_IL2CPP
```

빌드 출력:
- `UnityInfoBridge/Release/UnityInfoBridge.BepInEx.Mono/`
- `UnityInfoBridge/Release/UnityInfoBridge.BepInEx.IL2CPP/`
- `UnityInfoBridge/Release/UnityInfoBridge.MelonLoader.Mono/`
- `UnityInfoBridge/Release/UnityInfoBridge.MelonLoader.IL2CPP/`

각 출력 폴더에는 브리지 DLL과 함께 `Newtonsoft.Json.dll`이 포함됩니다.
IL2CPP 출력에는 `UnityInfoBridge.*.deps.json`도 같이 들어갑니다.

## 릴리즈 산출물

GitHub 릴리즈 워크플로우는 다음 파일을 생성합니다.
- `UnityInfoMCP_vx.x.x.exe`
- `UnityInfoMCP-vx.x.x.tar.gz`
- `UnityInfoMCP-vx.x.x-py3-none-any.whl`
- `UnityInfoBridge_vx.x.x_MelonLoader_Mono.zip`
- `UnityInfoBridge_vx.x.x_MelonLoader_IL2CPP.zip`
- `UnityInfoBridge_vx.x.x_BepInEx_Mono.zip`
- `UnityInfoBridge_vx.x.x_BepInEx_IL2CPP.zip`
- `SHA256SUMS.txt`

압축 내부 구조:
- MelonLoader Mono zip:
  `Mods/UnityInfoBridge.dll`
  `Mods/Newtonsoft.Json.dll`
- MelonLoader IL2CPP zip:
  `Mods/UnityInfoBridge.dll`
  `Mods/Newtonsoft.Json.dll`
  `Mods/UnityInfoBridge.deps.json`
- BepInEx Mono zip:
  `BepInEx/plugins/UnityInfoBridge/UnityInfoBridge.dll`
  `BepInEx/plugins/UnityInfoBridge/Newtonsoft.Json.dll`
- BepInEx IL2CPP zip:
  `BepInEx/plugins/UnityInfoBridge/UnityInfoBridge.dll`
  `BepInEx/plugins/UnityInfoBridge/Newtonsoft.Json.dll`
  `BepInEx/plugins/UnityInfoBridge/UnityInfoBridge.deps.json`

## MCP 도구 구성

런타임:
- `bridge_status`
- `list_bridge_targets`
- `select_bridge_target`
- `get_runtime_summary`

씬과 계층:
- `list_scenes`
- `get_scene_hierarchy`
- `find_gameobjects_by_name`
- `resolve_instance_id`
- `get_gameobject`
- `get_gameobject_by_path`
- `get_gameobject_children`

컴포넌트와 필드:
- `get_components`
- `get_component`
- `get_component_fields`
- `search_component_fields`

텍스트와 현지화 탐색:
- `list_text_elements`
- `search_text`
- `get_text_context`

스냅샷:
- `snapshot_gameobject`
- `snapshot_scene`

쓰기 작업:
- `set_gameobject_active`
- `set_component_member`
- `set_text`

캡처:
- `capture_screenshot`

참고:
- 텍스트와 계층 탐색은 Unity가 유효한 런타임 씬 오브젝트로 노출하는 `DontDestroyOnLoad` UI도 포함합니다.
- `capture_screenshot`는 PNG 이미지 내용을 MCP 클라이언트로 바로 반환합니다.
- `output_path`를 생략하면 브리지는 `GameRoot\UnityInfoBridge\captures\capture_yy-MM-dd-HH-mm-ss-fff.png` 경로를 임시로 사용하고, `UnityInfoMCP`가 이미지를 읽은 뒤 임시 파일을 삭제합니다.
- MCP 응답 뒤에도 PNG를 남겨두고 싶다면 `output_path`를 명시하세요.

## 사용 예시

실행 중인 대사 텍스트가 어떤 폰트를 쓰는지 찾기:

사용자 프롬프트:

```text
"어디까지나 이리스의 의견이니까"라는 텍스트가 어느 폰트를 사용하고 있는지 알려줘.
```

주요 호출:

```json
UnityInfoMCP.search_text({
  "query": "어디까지나 이리스의 의견이니까",
  "include_inactive": true,
  "limit": 10
})
```

결과 요약:
- scene: `Search`
- 오브젝트 경로: `_root/Canvas2/ScreenScaler2/GameObject/messagewindow/messagearea/text_message (TMP)`
- 컴포넌트 타입: `TMPro.TextMeshProUGUI`
- 현재 TMP 폰트 자산: `message#en-font`

같은 텍스트를 위로 `100px` 이동:

사용자 프롬프트:

```text
그 텍스트를 위로 100px 올려줘.
```

호출 흐름:

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
  "value": { "x": 0.0, "y": -258.0 },
  "include_non_public": false
})
```

검증:

```json
UnityInfoMCP.get_component_fields({
  "component_instance_id": 485632,
  "include_non_public": false,
  "include_properties": true,
  "max_depth": 1
})
```

결과 요약:
- 대상 컴포넌트: `UnityEngine.RectTransform`
- `anchoredPosition`: `(0.0, -358.0)` -> `(0.0, -258.0)`
- 실제 변경: 위로 `100px` 이동

`480506`, `485632` 같은 런타임 오브젝트 ID는 예시이며 세션마다 달라질 수 있습니다.
일반적인 Unity 구조체 값은 `"(0, -258)"` 같은 튜플 문자열 형태로도 전달할 수 있습니다.

## 문서

- 브리지 프로토콜: `docs/bridge-protocol.md`
- 도구 매핑: `docs/tool-catalog.md`
