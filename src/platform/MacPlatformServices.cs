using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace Keymon.Platform
{
    // macOS용 구현. Windows 구현과 같은 계약(IPlatformServices)을 지킵니다.
    //
    // 네이티브 호출은 전부 try/catch로 감싸고, 한 번 실패한 기능은
    // 플래그를 세워 다시 호출하지 않습니다. OS 업데이트로 특정 API가
    // 막히더라도 앱 전체가 멈추지 않게 하기 위해서입니다.
    [SupportedOSPlatform("macos")]
    public sealed class MacPlatformServices : IPlatformServices
    {
        private const string AgentLabel = "com.keymon.app";

        private bool _frontmostAppBroken;
        private bool _screenLockBroken;
        private bool _hidAccessBroken;

        private Timer? _lockPollTimer;
        private bool _wasLocked;
        private Action? _onInactive;
        private Action? _onActive;

        public string DisplayName => "macOS";

        // libuiohook이 넘겨주는 휠 회전량의 단위가 Windows와 다릅니다.
        // (Windows는 WHEEL_DELTA 기반의 큰 값, macOS는 스크롤 델타 기반의 작은 값)
        // 휠 감도는 마우스/트랙패드마다 편차가 커서 환경 변수로 조절할 수 있게 열어 둡니다.
        //   예) KEYMON_SCROLL_THRESHOLD=20 dotnet run
        public int ScrollThreshold
        {
            get
            {
                string? raw = Environment.GetEnvironmentVariable("KEYMON_SCROLL_THRESHOLD");
                if (int.TryParse(raw, out int parsed) && parsed > 0) return parsed;
                return 10;
            }
        }

        // ── 창 전환 감지 ────────────────────────────────────────────
        public bool TryGetActiveWindowId(out long id)
        {
            id = 0;
            if (_frontmostAppBroken) return false;

            try
            {
                int pid = MacInterop.GetFrontmostApplicationPid();
                if (pid <= 0) return false;

                id = pid;
                return true;
            }
            catch (Exception ex)
            {
                _frontmostAppBroken = true;
                Log.Warn($"최전면 앱 감지 비활성화 (창 전환 지표는 0으로 고정됩니다): {ex.Message}");
                return false;
            }
        }

        // ── 자동 실행: LaunchAgent plist ────────────────────────────
        // Windows의 레지스트리 Run 키에 해당하는 macOS의 표준 방식입니다.
        // ~/Library/LaunchAgents/com.keymon.app.plist 파일이 있으면
        // 로그인할 때 launchd가 앱을 대신 실행해 줍니다.
        public bool SupportsAutoStart => true;

        private static string AgentPlistPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", $"{AgentLabel}.plist");

        public bool IsAutoStartEnabled()
        {
            try { return File.Exists(AgentPlistPath); }
            catch { return false; }
        }

        public void SetAutoStart(bool enabled)
        {
            try
            {
                string path = AgentPlistPath;

                if (!enabled)
                {
                    if (File.Exists(path))
                    {
                        RunLaunchctl("bootout", path);
                        File.Delete(path);
                    }
                    return;
                }

                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    Log.Warn("실행 파일 경로를 알 수 없어 자동 실행을 설정하지 못했습니다.");
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, BuildAgentPlist(exePath));
                RunLaunchctl("bootstrap", path);
            }
            catch (Exception ex) { Log.Warn($"자동 실행 설정 실패: {ex.Message}"); }
        }

        private static string BuildAgentPlist(string exePath) =>
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
	<key>Label</key>
	<string>{AgentLabel}</string>
	<key>ProgramArguments</key>
	<array>
		<string>{System.Security.SecurityElement.Escape(exePath)}</string>
	</array>
	<key>RunAtLoad</key>
	<true/>
	<key>KeepAlive</key>
	<false/>
	<key>ProcessType</key>
	<string>Interactive</string>
</dict>
</plist>
";

        // launchctl 등록/해제는 '있으면 좋은' 단계입니다.
        // 실패해도 plist 파일 자체가 남아 있으면 다음 로그인부터 정상 동작합니다.
        private static void RunLaunchctl(string verb, string plistPath)
        {
            try
            {
                string domain = $"gui/{GetUid()}";
                var psi = new ProcessStartInfo("launchctl")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                psi.ArgumentList.Add(verb);
                psi.ArgumentList.Add(verb == "bootout" ? $"{domain}/{AgentLabel}" : domain);
                if (verb == "bootstrap") psi.ArgumentList.Add(plistPath);

                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
            }
            catch (Exception ex) { Log.Warn($"launchctl {verb} 실패(무시 가능): {ex.Message}"); }
        }

        private static int GetUid()
        {
            // getuid()를 직접 부르는 대신, .NET이 이미 알고 있는 값을 씁니다.
            try
            {
                var psi = new ProcessStartInfo("id") { RedirectStandardOutput = true, UseShellExecute = false };
                psi.ArgumentList.Add("-u");
                using var proc = Process.Start(psi);
                if (proc == null) return 501;
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(2000);
                return int.TryParse(output, out int uid) ? uid : 501;
            }
            catch { return 501; }
        }

        // ── 입력 모니터링 권한 ──────────────────────────────────────
        // macOS는 이 권한 없이는 전역 후킹이 '오류 없이 조용히' 실패합니다.
        // (앱은 잘 뜨는데 KPM이 영원히 0인 증상)
        public bool RequiresInputPermission => true;

        public bool HasInputPermission()
        {
            if (_hidAccessBroken) return true; // 확인이 불가능하면 막지 않습니다.

            try { return MacInterop.HasInputMonitoringAccess(); }
            catch (Exception ex)
            {
                _hidAccessBroken = true;
                Log.Warn($"입력 권한 확인 불가: {ex.Message}");
                return true;
            }
        }

        public void RequestInputPermission()
        {
            try { MacInterop.RequestInputMonitoringAccess(); }
            catch (Exception ex) { Log.Warn($"권한 요청 팝업 실패: {ex.Message}"); }

            // 사용자가 이전에 한 번 거부했다면 시스템 팝업이 다시 뜨지 않습니다.
            // 그래서 설정 화면을 직접 열어 줍니다.
            OpenInputMonitoringSettings();
        }

        public static void OpenInputMonitoringSettings()
        {
            try
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add("x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent");
                Process.Start(psi);
            }
            catch (Exception ex) { Log.Warn($"시스템 설정 열기 실패: {ex.Message}"); }
        }

        // ── 화면 잠금 감시 ──────────────────────────────────────────
        // Windows의 SystemEvents.SessionSwitch에 대응합니다.
        // macOS에는 동등한 관리 이벤트를 C#에서 바로 구독할 방법이 없어
        // 1초 폴링으로 잠금 상태 '변화'만 감지합니다. 비용은 무시할 수준입니다.
        //
        // 절전(sleep) 구간 보정은 MonitoringService의 시계 드리프트 감지가 담당하므로,
        // 여기서는 잠금/해제만 다뤄도 데이터 정합성이 유지됩니다.
        public void StartSessionWatch(Action onInactive, Action onActive)
        {
            _onInactive = onInactive;
            _onActive = onActive;
            _wasLocked = IsScreenLocked();

            _lockPollTimer = new Timer(_ => PollLockState(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public void StopSessionWatch()
        {
            _lockPollTimer?.Dispose();
            _lockPollTimer = null;
        }

        private void PollLockState()
        {
            bool locked = IsScreenLocked();
            if (locked == _wasLocked) return;

            _wasLocked = locked;
            if (locked) _onInactive?.Invoke();
            else _onActive?.Invoke();
        }

        private bool IsScreenLocked()
        {
            if (_screenLockBroken) return false;

            try { return MacInterop.IsScreenLocked(); }
            catch (Exception ex)
            {
                _screenLockBroken = true;
                Log.Warn($"화면 잠금 감지 비활성화: {ex.Message}");
                return false;
            }
        }

        public void Dispose() => StopSessionWatch();
    }
}
