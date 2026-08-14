using System;
using System.Threading.Tasks;
using Keymon.Platform;
using SharpHook;

namespace Keymon
{
    // SharpHook(libuiohook)의 전역 후킹을 감싸는 얇은 어댑터입니다.
    //
    // libuiohook은 Windows / macOS / Linux 네이티브 바이너리를 모두 담고 있어
    // 이 클래스는 원본 keymon과 거의 동일합니다. 달라진 점은 딱 하나,
    // macOS에서 '입력 모니터링' 권한이 없으면 후킹이 예외 없이 조용히 실패하므로
    // 시작할 때 권한 상태를 확인하고 알려 주는 부분입니다.
    public class InputHookManager : IDisposable
    {
        private TaskPoolGlobalHook? _globalHook;

        public event EventHandler<KeyboardHookEventArgs>? KeyPressed;
        public event EventHandler<KeyboardHookEventArgs>? KeyReleased;
        public event EventHandler<MouseHookEventArgs>? MousePressed;
        public event EventHandler<MouseHookEventArgs>? MouseMoved;
        public event EventHandler<MouseWheelHookEventArgs>? MouseWheel;

        // 권한이 없어 후킹이 무력화된 상태인지 UI가 알 수 있게 노출합니다.
        public bool IsPermissionMissing { get; private set; }

        public void Start()
        {
            if (_globalHook != null) return;

            var platform = PlatformServices.Current;
            if (platform.RequiresInputPermission && !platform.HasInputPermission())
            {
                IsPermissionMissing = true;
                Log.Warn("입력 모니터링 권한이 없습니다. 권한을 허용한 뒤 앱을 다시 실행해 주세요.");
                platform.RequestInputPermission();
            }

            _globalHook = new TaskPoolGlobalHook();

            // SharpHook의 이벤트를 그대로 토스합니다.
            _globalHook.KeyPressed += OnHookKeyPressed;
            _globalHook.KeyReleased += OnHookKeyReleased;
            _globalHook.MousePressed += OnHookMousePressed;
            _globalHook.MouseMoved += OnHookMouseMoved;
            _globalHook.MouseWheel += OnHookMouseWheel;

            // Run()은 이벤트 루프라서 돌아오지 않습니다. 반드시 별도 스레드에서 돌립니다.
            Task.Run(() =>
            {
                try { _globalHook.Run(); }
                catch (Exception ex) { Log.Warn($"전역 후킹 종료: {ex.Message}"); }
            });
        }

        public void Stop()
        {
            if (_globalHook != null)
            {
                _globalHook.KeyPressed -= OnHookKeyPressed;
                _globalHook.KeyReleased -= OnHookKeyReleased;
                _globalHook.MousePressed -= OnHookMousePressed;
                _globalHook.MouseMoved -= OnHookMouseMoved;
                _globalHook.MouseWheel -= OnHookMouseWheel;

                _globalHook.Dispose();
                _globalHook = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void OnHookKeyPressed(object? sender, KeyboardHookEventArgs e) => KeyPressed?.Invoke(this, e);
        private void OnHookKeyReleased(object? sender, KeyboardHookEventArgs e) => KeyReleased?.Invoke(this, e);
        private void OnHookMousePressed(object? sender, MouseHookEventArgs e) => MousePressed?.Invoke(this, e);
        private void OnHookMouseMoved(object? sender, MouseHookEventArgs e) => MouseMoved?.Invoke(this, e);
        private void OnHookMouseWheel(object? sender, MouseWheelHookEventArgs e) => MouseWheel?.Invoke(this, e);
    }
}
