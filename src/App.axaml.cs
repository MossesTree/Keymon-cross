using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Keymon.Platform;

namespace Keymon
{
    // 앱의 조립 지점. 원본 keymon의 App.xaml.cs와 순서·역할이 같습니다.
    // 달라진 것은 WPF의 Application 대신 Avalonia의 Application을 상속하고,
    // 창 표시/종료를 IClassicDesktopStyleApplicationLifetime을 통해 다룬다는 점입니다.
    public partial class App : Application
    {
        private InputHookManager? _hookManager;
        private MetricCollector? _collector;
        private AnalysisEngine? _engine;
        private MonitoringService? _monitoring;
        private TrayIconManager? _tray;
        private PersistenceService? _persistence;

        private DashboardWindow? _dashboardWindow;
        private OverlayWindow? _overlayWindow;

        // 대시보드가 "권한이 없어 수집이 안 되고 있음" 배너를 띄울 때 참조합니다.
        public static InputHookManager? Hook { get; private set; }

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // 창을 모두 닫아도 트레이에 남아 계속 수집합니다. (원본과 동일)
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                TaskScheduler.UnobservedTaskException += (sender, args) =>
                {
                    if (args.Exception.InnerException is ObjectDisposedException)
                        args.SetObserved();
                };

                Log.Info($"KEYMON 시작 — 플랫폼: {PlatformServices.Current.DisplayName}");

                _engine = new AnalysisEngine();
                _hookManager = new InputHookManager();
                _collector = new MetricCollector();
                _persistence = new PersistenceService();
                _tray = new TrayIconManager();

                Hook = _hookManager;

                _monitoring = new MonitoringService(_collector, _engine, _tray, _persistence);

                _collector.Subscribe(_hookManager);

                _tray.OnShowDashboard = ShowDashboard;
                _tray.OnExit = () =>
                {
                    // 두 창 모두 '닫기 = 숨기기'로 동작하므로,
                    // 종료 전에 그 보호막을 먼저 걷어 내야 확실히 내려갑니다.
                    _overlayWindow?.AllowClose();
                    _dashboardWindow?.AllowClose();
                    desktop.Shutdown();
                };

                _tray.OnToggleOverlay = isVisible =>
                {
                    if (isVisible) _overlayWindow?.Show();
                    else _overlayWindow?.Hide();
                };

                _tray.OnToggleManualStandby = () =>
                {
                    _monitoring.ToggleManualStandby();
                    _tray.SyncManualStandbyState(_monitoring.IsManualStandby);
                };

                _tray.Initialize();

                _hookManager.Start();
                _monitoring.Start();

                _overlayWindow = new OverlayWindow(_monitoring);
                _overlayWindow.Show();

                ShowDashboard();

                desktop.Exit += (s, e) => Cleanup();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void ShowDashboard()
        {
            if (_dashboardWindow == null)
            {
                _dashboardWindow = new DashboardWindow(_monitoring!);
                // 대시보드를 닫아도 앱은 트레이에 살아 있어야 하므로,
                // 창 자체를 파괴하지 않고 숨기기만 합니다(DashboardWindow.OnClosing 참고).
            }

            _dashboardWindow.Show();
            _dashboardWindow.Activate();
        }

        private void Cleanup()
        {
            _monitoring?.Stop();
            _hookManager?.Stop();
            _tray?.Dispose();

            _overlayWindow?.ForceClose();
            _dashboardWindow?.ForceClose();

            PlatformServices.Shutdown();
        }
    }
}
