using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Keymon.Platform
{
    // 원본 keymon(WPF)이 쓰던 Windows 네이티브 코드를 이 클래스 한 곳에 모았습니다.
    // 동작은 원본과 100% 동일합니다.
    [SupportedOSPlatform("windows")]
    public sealed class WindowsPlatformServices : IPlatformServices
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "Keymon";

        private Action? _onInactive;
        private Action? _onActive;
        private bool _watching;

        public string DisplayName => "Windows";

        // 윈도우의 휠 1단계는 WHEEL_DELTA(120)이지만, libuiohook은 이를
        // 다시 스케일링해서 넘겨줍니다. 원본 keymon이 실측으로 정한 값을 유지합니다.
        public int ScrollThreshold => 2500;

        // ── 창 전환 감지 ────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        public bool TryGetActiveWindowId(out long id)
        {
            IntPtr handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                id = 0;
                return false;
            }

            // 원본과 동일하게 '제목을 읽을 수 있는 창'만 유효한 전환으로 인정합니다.
            // (제목이 빈 셸 창으로 전환될 때 잡음이 섞이는 것을 막습니다.)
            var title = new StringBuilder(256);
            if (GetWindowText(handle, title, 256) <= 0)
            {
                id = 0;
                return false;
            }

            id = handle.ToInt64();
            return true;
        }

        // ── 자동 실행: HKCU\...\Run 레지스트리 ──────────────────────
        public bool SupportsAutoStart => true;

        public bool IsAutoStartEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        public void SetAutoStart(bool enabled)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null) return;

                if (enabled) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
                else key.DeleteValue(AppName, false);
            }
            catch (Exception ex) { Log.Warn($"자동 실행 설정 실패: {ex.Message}"); }
        }

        // ── 입력 권한: Windows는 별도 권한이 필요 없습니다 ──────────
        public bool RequiresInputPermission => false;
        public bool HasInputPermission() => true;
        public void RequestInputPermission() { }

        // ── 절전 / 화면 잠금 ────────────────────────────────────────
        public void StartSessionWatch(Action onInactive, Action onActive)
        {
            if (_watching) return;
            _onInactive = onInactive;
            _onActive = onActive;

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _watching = true;
        }

        public void StopSessionWatch()
        {
            if (!_watching) return;

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _watching = false;
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) _onInactive?.Invoke();
            else if (e.Mode == PowerModes.Resume) _onActive?.Invoke();
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) _onInactive?.Invoke();
            else if (e.Reason == SessionSwitchReason.SessionUnlock) _onActive?.Invoke();
        }

        public void Dispose() => StopSessionWatch();
    }
}
