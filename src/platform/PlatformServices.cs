using System;

namespace Keymon.Platform
{
    // 실행 중인 OS를 보고 알맞은 구현체를 골라주는 팩토리입니다.
    // 앱 전체가 Current 하나만 바라보므로, 상위 계층에는 #if WINDOWS 같은
    // 조건부 컴파일이 단 한 줄도 등장하지 않습니다.
    public static class PlatformServices
    {
        private static IPlatformServices? _current;

        public static IPlatformServices Current => _current ??= Create();

        private static IPlatformServices Create()
        {
            if (OperatingSystem.IsWindows()) return new WindowsPlatformServices();
            if (OperatingSystem.IsMacOS()) return new MacPlatformServices();
            return new NoopPlatformServices();
        }

        public static void Shutdown()
        {
            _current?.Dispose();
            _current = null;
        }
    }

    // Linux 등 아직 지원하지 않는 OS에서도 앱이 죽지 않도록 하는 빈 구현입니다.
    // (입력 후킹과 분석은 그대로 동작하고, 창 전환/자동 실행만 비활성화됩니다.)
    public sealed class NoopPlatformServices : IPlatformServices
    {
        public string DisplayName => "Unsupported";
        public int ScrollThreshold => 1;
        public bool TryGetActiveWindowId(out long id) { id = 0; return false; }
        public bool SupportsAutoStart => false;
        public bool IsAutoStartEnabled() => false;
        public void SetAutoStart(bool enabled) { }
        public bool RequiresInputPermission => false;
        public bool HasInputPermission() => true;
        public void RequestInputPermission() { }
        public void StartSessionWatch(Action onInactive, Action onActive) { }
        public void StopSessionWatch() { }
        public void Dispose() { }
    }
}
