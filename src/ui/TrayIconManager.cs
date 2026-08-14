using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Keymon.Platform;

namespace Keymon
{
    // 트레이(Windows 작업 표시줄) / 메뉴바(macOS 우측 상단) 아이콘을 관리합니다.
    //
    // 원본 keymon은 H.NotifyIcon.Wpf + System.Drawing.Icon 조합이라 Windows 전용이었습니다.
    // Avalonia의 TrayIcon + NativeMenu로 바꾸면 두 OS 모두 네이티브 위젯을 씁니다.
    //   Windows → 알림 영역 아이콘 + 우클릭 컨텍스트 메뉴
    //   macOS   → 메뉴바 NSStatusItem + 클릭 시 드롭다운 메뉴
    public class TrayIconManager : IDisposable
    {
        private readonly IPlatformServices _platform = PlatformServices.Current;

        private TrayIcon? _trayIcon;
        private DispatcherTimer? _animTimer;
        private SpriteAnimator? _animator;

        // Bitmap → WindowIcon 변환 결과 캐시.
        // 85ms마다 아이콘을 갈아 끼우므로 매번 새로 만들면 GC 부담이 큽니다.
        private readonly Dictionary<Bitmap, WindowIcon> _iconCache = new();

        private bool _overlayVisible = true;

        private NativeMenuItem? _manualStandbyItem;
        private NativeMenuItem? _overlayItem;
        private NativeMenuItem? _autoStartItem;

        public Action? OnShowDashboard;
        public Action? OnExit;
        public Action<bool>? OnToggleOverlay;
        public Action? OnToggleManualStandby;

        public bool IsStandby
        {
            get => _animator?.IsStandby ?? false;
            set { if (_animator != null) _animator.IsStandby = value; }
        }

        public void Initialize()
        {
            var frames = SpriteLibrary.TrayFrames;
            if (frames.Count == 0)
            {
                Log.Warn("트레이 애니메이션 파일을 찾을 수 없어 아이콘 없이 실행합니다.");
                return;
            }

            _animator = new SpriteAnimator(frames);

            _trayIcon = new TrayIcon
            {
                ToolTipText = "데이터 수집 중...",
                Icon = ToWindowIcon(frames[1][0]),
                Menu = CreateContextMenu(),
                IsVisible = true
            };

            // Windows는 아이콘 좌클릭으로 바로 대시보드가 열립니다.
            // macOS는 클릭하면 메뉴가 내려오므로 메뉴의 '대시보드 열기'가 그 역할을 합니다.
            _trayIcon.Clicked += (s, e) => OnShowDashboard?.Invoke();

            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(85) };
            _animTimer.Tick += OnAnimationTick;
            _animTimer.Start();
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            if (_trayIcon == null || _animator == null) return;

            Bitmap? frame = _animator.NextFrame();
            if (frame != null) _trayIcon.Icon = ToWindowIcon(frame);
        }

        private WindowIcon ToWindowIcon(Bitmap bitmap)
        {
            if (_iconCache.TryGetValue(bitmap, out var cached)) return cached;

            var icon = new WindowIcon(bitmap);
            _iconCache[bitmap] = icon;
            return icon;
        }

        public void UpdateAnimationByState(int focusState) => _animator?.SetState(focusState);

        public void UpdateTooltip(string text)
        {
            if (_trayIcon != null) _trayIcon.ToolTipText = text;
        }

        private NativeMenu CreateContextMenu()
        {
            var menu = new NativeMenu();

            var dashItem = new NativeMenuItem("대시보드 열기");
            dashItem.Click += (s, e) => OnShowDashboard?.Invoke();
            menu.Add(dashItem);

            menu.Add(new NativeMenuItemSeparator());

            _manualStandbyItem = new NativeMenuItem("분석 일시 정지 (수동)")
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = false
            };
            _manualStandbyItem.Click += (s, e) => OnToggleManualStandby?.Invoke();
            menu.Add(_manualStandbyItem);

            menu.Add(new NativeMenuItemSeparator());

            _overlayItem = new NativeMenuItem("오버레이 창 표시")
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = _overlayVisible
            };
            _overlayItem.Click += (s, e) =>
            {
                // 클릭 시 IsChecked를 자동으로 뒤집는지는 OS 백엔드마다 다릅니다.
                // 그래서 표시 여부는 우리가 직접 기억하고, 메뉴 체크는 거기에 맞춥니다.
                _overlayVisible = !_overlayVisible;
                _overlayItem.IsChecked = _overlayVisible;
                OnToggleOverlay?.Invoke(_overlayVisible);
            };
            menu.Add(_overlayItem);

            menu.Add(new NativeMenuItemSeparator());

            if (_platform.SupportsAutoStart)
            {
                // Windows는 "윈도우 시작 시", macOS는 "로그인 시"가 자연스러운 표현입니다.
                string label = OperatingSystem.IsMacOS() ? "로그인 시 자동 실행" : "윈도우 시작 시 자동 실행";
                _autoStartItem = new NativeMenuItem(label)
                {
                    ToggleType = NativeMenuItemToggleType.CheckBox,
                    IsChecked = _platform.IsAutoStartEnabled()
                };
                _autoStartItem.Click += (s, e) =>
                {
                    bool next = !_autoStartItem.IsChecked;
                    _platform.SetAutoStart(next);
                    _autoStartItem.IsChecked = _platform.IsAutoStartEnabled();
                };
                menu.Add(_autoStartItem);
            }

            if (_platform.RequiresInputPermission)
            {
                // macOS 전용: 권한을 나중에 허용하려는 사용자를 위한 바로가기입니다.
                var permissionItem = new NativeMenuItem("입력 모니터링 권한 설정 열기");
                permissionItem.Click += (s, e) => _platform.RequestInputPermission();
                menu.Add(permissionItem);
            }

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("종료");
            exitItem.Click += (s, e) => OnExit?.Invoke();
            menu.Add(exitItem);

            return menu;
        }

        public void SyncManualStandbyState(bool isManualStandby)
        {
            if (_manualStandbyItem != null)
                _manualStandbyItem.IsChecked = isManualStandby;
        }

        public void SyncOverlayState(bool isVisible)
        {
            _overlayVisible = isVisible;
            if (_overlayItem != null)
                _overlayItem.IsChecked = isVisible;
        }

        public void Dispose()
        {
            _animTimer?.Stop();
            _animTimer = null;

            if (_trayIcon != null)
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            _iconCache.Clear();
        }
    }
}
