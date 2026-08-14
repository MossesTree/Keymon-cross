using System;

namespace Keymon.Platform
{
    // OS마다 다른 네이티브 기능을 하나의 '계약서'로 묶습니다.
    //
    // 원본 keymon(WPF)은 이 기능들을 코드 곳곳에 흩뿌려 놓았습니다.
    //   - MetricCollector    : user32.dll의 GetForegroundWindow (창 전환 감지)
    //   - MonitoringService  : Microsoft.Win32.SystemEvents (절전/화면 잠금)
    //   - TrayIconManager    : Registry의 Run 키 (자동 실행)
    // 세 곳 모두 Windows 전용 API라서 macOS에서는 실행 즉시 예외가 납니다.
    //
    // 이 인터페이스를 도입해 상위 계층(input/analysis/services/ui)은
    // "지금 어느 OS인지" 전혀 모른 채로 동작하게 만듭니다.
    // 새 OS를 추가하려면 이 인터페이스 구현체 하나만 더 쓰면 됩니다. (개방-폐쇄 원칙)
    public interface IPlatformServices : IDisposable
    {
        // 로그/대시보드 표기용 이름 ("Windows", "macOS", ...)
        string DisplayName { get; }

        // ── 창 전환 감지 ────────────────────────────────────────────
        // 현재 최상단(포어그라운드) 앱을 구분하는 식별자를 돌려줍니다.
        // Windows: 창 핸들(HWND), macOS: 최전면 앱의 프로세스 ID(pid)
        // 지원하지 않거나 실패하면 false를 반환하고, 호출자는 전환이 없던 것으로 처리합니다.
        bool TryGetActiveWindowId(out long id);

        // ── 마우스 휠 ──────────────────────────────────────────────
        // libuiohook이 넘겨주는 Rotation 값의 단위가 OS마다 다릅니다.
        // 이 값을 넘어설 때마다 '스크롤 1회'로 셉니다.
        int ScrollThreshold { get; }

        // ── 자동 실행 ──────────────────────────────────────────────
        bool SupportsAutoStart { get; }
        bool IsAutoStartEnabled();
        void SetAutoStart(bool enabled);

        // ── 입력 모니터링 권한 (macOS 전용) ─────────────────────────
        // macOS는 손쉬운 사용(Accessibility) 권한이 없으면 전역 후킹이 조용히 실패합니다.
        // Windows는 권한 개념이 없으므로 RequiresInputPermission == false 입니다.
        bool RequiresInputPermission { get; }
        bool HasInputPermission();
        void RequestInputPermission();

        // ── 절전 / 화면 잠금 ────────────────────────────────────────
        // 시스템이 잠들거나 화면이 잠기면 onInactive, 깨어나면 onActive가 호출됩니다.
        // 구현체가 OS 이벤트를 못 받는 경우에도 MonitoringService의
        // 시계 드리프트 감지가 절전 구간을 보정하므로 데이터는 어긋나지 않습니다.
        void StartSessionWatch(Action onInactive, Action onActive);
        void StopSessionWatch();
    }
}
