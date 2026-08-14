# KEYMON (크로스플랫폼 확장판)

타이핑 리듬을 지켜보다가 지금의 집중 상태를 픽셀 고양이로 알려 주는 백그라운드 앱입니다.

> **원본 프로젝트**: <https://github.com/Skarf19/keymon>
>
> 이 저장소는 위 원본을 **macOS에서도 쓸 수 있도록 확장한 포크**입니다.
> 원본은 WPF 기반이라 Windows 전용이었고, 여기서는 UI를 Avalonia로 옮기고
> OS 의존 코드를 분리해 **Windows와 macOS 하나의 코드베이스**로 만들었습니다.
> 집중도/피로도 분석 알고리즘(`src/analysis/`)은 원본을 그대로 유지합니다.

---

## 지원 환경

| OS | 상태 | 비고 |
|---|---|---|
| Windows 10 / 11 | ✅ | 원본과 동일하게 동작 |
| macOS 11 (Big Sur) 이상 | ✅ | Apple Silicon · Intel 모두 지원. 입력 모니터링 권한 필요 |
| Linux | ⚠️ | 실행은 되지만 창 전환 감지·자동 실행은 비활성화 |

---

## 시작하기 전에

[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)만 설치하면 됩니다.

```bash
dotnet --version   # 10. 으로 시작하는 숫자가 나와야 합니다
```

---

## 실행 방법

### Windows

```bash
cd keymon-cross
dotnet run --project src/Keymon.Cross.csproj
```

### macOS — 개발 중 빠르게 확인할 때

```bash
cd keymon-cross
dotnet run --project src/Keymon.Cross.csproj
```

처음 실행하면 **입력 모니터링 권한** 팝업이 뜹니다. 허용한 뒤 앱을 껐다 켜 주세요.

> ⚠️ `dotnet run`은 실행할 때마다 macOS가 다른 앱으로 인식해서 권한이 유지되지 않습니다.
> 계속 쓸 거라면 아래 `.app` 번들로 만드세요.

### macOS — 제대로 설치해서 쓸 때

```bash
cd keymon-cross
chmod +x build-mac.sh
./build-mac.sh
```

`dist/KEYMON.app`이 만들어집니다. 응용 프로그램 폴더로 옮긴 뒤:

1. `KEYMON.app` 실행 → 권한 요청 팝업에서 **허용**
2. **시스템 설정 → 개인정보 보호 및 보안 → 입력 모니터링**에서 KEYMON 켜기
3. 메뉴바 아이콘 → **종료** 후 다시 실행 (권한은 재시작해야 적용됩니다)

빌드 옵션:

| 옵션 | 설명 |
|---|---|
| `--arch osx-x64` | 인텔 맥용으로 빌드 (기본값은 현재 맥에 맞춰 자동 감지) |
| `--menubar-only` | Dock 아이콘 없이 메뉴바에만 존재 (`LSUIElement`) |
| `--framework-dependent` | .NET 런타임을 별도 설치하는 대신 용량을 줄임 |

---

## 무엇이 보이나요

- **픽셀 고양이 창** — 화면 구석에 떠서 집중 상태에 따라 움직입니다. 드래그해서 옮길 수 있고, 위치는 저장됩니다.
- **작업 표시줄(Windows) / 메뉴바(macOS) 아이콘** — 앱이 백그라운드에서 돌고 있다는 표시입니다.
  - Windows: 아이콘 **좌클릭**으로 대시보드, **우클릭**으로 메뉴
  - macOS: 아이콘 **클릭**으로 메뉴 → *대시보드 열기*

---

## 동작 방식

1. 백그라운드에서 키보드·마우스 입력 리듬을 관찰합니다.
2. 1분마다 집중도를 산출합니다. (대시보드의 스위치로 10초 간격 빠른 모드로 바꿀 수 있습니다.)
3. 상태는 5단계입니다: `Idle → Distracted → Engaged → Focused → Deep Focus`
4. 고양이 애니메이션과 트레이 아이콘이 상태에 맞춰 바뀝니다.
5. 대시보드에서 실시간 지표와 날짜별 통계를 볼 수 있습니다.

수집한 데이터는 **전부 내 컴퓨터에만** 저장되며 어디로도 전송되지 않습니다.

| OS | 저장 위치 |
|---|---|
| Windows | `%APPDATA%\Keymon\userData.json` |
| macOS | `~/Library/Application Support/Keymon/userData.json` |

원본 keymon처럼 실행 파일 옆에 `userData.json`이 있으면 첫 실행 때 위 위치로 자동 이전합니다.

---

## 원본에서 무엇이 바뀌었나

포팅의 핵심은 **OS에 의존하는 코드를 `src/platform/` 한 곳에 가둔 것**입니다.
나머지 계층(`input` / `analysis` / `services` / `ui`)은 지금 어느 OS에서 도는지 전혀 모릅니다.

| 영역 | 원본 (Windows 전용) | 확장판 (Windows + macOS) |
|---|---|---|
| UI 프레임워크 | WPF | Avalonia UI 11 |
| 트레이 아이콘 | `H.NotifyIcon.Wpf` + `System.Drawing.Icon` | Avalonia `TrayIcon` + `NativeMenu` |
| 창 전환 감지 | `user32.dll`의 `GetForegroundWindow` | `IPlatformServices.TryGetActiveWindowId`<br>→ Windows: HWND / macOS: `NSWorkspace.frontmostApplication`의 pid |
| 절전 복귀 | `SystemEvents.PowerModeChanged` | 시계 드리프트 감지 (OS 무관) + Windows는 기존 이벤트도 유지 |
| 화면 잠금 | `SystemEvents.SessionSwitch` | Windows: 동일 / macOS: `CGSessionCopyCurrentDictionary` 폴링 |
| 자동 실행 | 레지스트리 `Run` 키 | Windows: 동일 / macOS: `~/Library/LaunchAgents` LaunchAgent |
| 입력 권한 | 불필요 | macOS: `IOHIDCheckAccess`로 확인 후 안내 배너 표시 |
| 스프라이트 로딩 | `"..\..\..\Assets\..."` 파일 경로 | 어셈블리 내장 리소스(`avares://`) |
| 데이터 저장 위치 | 실행 파일 옆 | OS 표준 사용자 데이터 폴더 |

전역 입력 후킹에 쓰는 **SharpHook(libuiohook)** 은 원본에서 이미 크로스플랫폼이라
`src/input/InputHookManager.cs`는 거의 손대지 않았습니다.

정리하면서 함께 손본 것:

- 트레이와 오버레이에 **똑같이 복사돼 있던 스프라이트 재생 로직**을 `SpriteAnimator` 하나로 합쳤습니다.
- XAML에 선언만 해 두고 **빈 메서드로 남아 있던 캐릭터 애니메이션**(`UpdateCharacterAnimation`)을 5개 상태 모두 실제로 구현했습니다.

---

## 폴더 구조

```
keymon-cross/
├── build-mac.sh              macOS .app 번들 생성 스크립트
├── keymon-cross.sln
└── src/
    ├── Program.cs            진입점 (Avalonia)
    ├── App.axaml(.cs)        앱 조립 지점
    ├── platform/             ★ OS 추상화 계층 (신규)
    │   ├── IPlatformServices.cs      OS별 기능 계약
    │   ├── PlatformServices.cs       실행 중인 OS에 맞는 구현체 선택
    │   ├── WindowsPlatformServices.cs
    │   ├── MacPlatformServices.cs
    │   ├── MacInterop.cs             Objective-C / CoreGraphics / IOKit P/Invoke
    │   └── Log.cs
    ├── input/                Layer 1 — 입력 캡처
    ├── analysis/             Layer 2 — 분석 엔진 (원본 그대로)
    ├── services/             Layer 3 — 세션 조율·저장
    ├── ui/                   Layer 4 — 대시보드·오버레이·트레이
    └── Assets/               픽셀 고양이 스프라이트
```

---

## 문제 해결

**타수(KPM)가 계속 0입니다 (macOS)**
→ 입력 모니터링 권한이 없는 상태입니다. 대시보드 상단에 빨간 배너가 뜹니다.
   시스템 설정에서 KEYMON을 켜고 **앱을 완전히 종료했다가 다시 실행**하세요.
   `dotnet run`으로 띄웠다면 권한이 유지되지 않으니 `./build-mac.sh`로 만든 `.app`을 쓰세요.

**스크롤이 잘 안 세집니다 (macOS)**
→ 휠 감도는 마우스·트랙패드마다 편차가 큽니다. 환경 변수로 조절할 수 있습니다.

```bash
KEYMON_SCROLL_THRESHOLD=20 dotnet run --project src/Keymon.Cross.csproj
```

**창 전환 횟수가 Windows보다 적게 나옵니다 (macOS)**
→ 의도된 차이입니다. Windows는 *창* 단위로, macOS는 *앱* 단위로 셉니다.
   같은 앱 안에서 탭·창만 옮겨 다니는 것은 macOS에서 전환으로 세지 않습니다.

**`dotnet` 명령을 찾을 수 없습니다**
→ .NET 10 SDK가 아직 없거나, 설치 후 터미널을 다시 열지 않았습니다.

---

## 팀 규칙

- `main`에 직접 push 금지
- 항상 기능 브랜치(`feature/기능-이름`)에서 작업
- 병합 전 Pull Request 열기
