using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Keymon
{
    // 화면 구석에 떠서 집중 상태를 픽셀 고양이로 보여주는 오버레이 창입니다.
    // (원본 keymon의 MainWindow. 역할이 드러나도록 이름만 바꿨습니다.)
    public partial class OverlayWindow : Window
    {
        private readonly ISessionData _session;
        private readonly SpriteAnimator _animator;
        private DispatcherTimer? _animTimer;

        private bool _allowClose;

        // 창이 열리는 동안 OS가 임의의 기본 좌표로 PositionChanged를 한 번 쏩니다.
        // 그 값을 저장해 버리면 "복원된 위치"가 매번 좌측 상단으로 덮어써지므로,
        // 위치 복원이 끝난 뒤부터만 사용자의 이동을 기록합니다.
        private bool _positionRestored;

        public OverlayWindow(ISessionData session)
        {
            InitializeComponent();
            _session = session;
            _animator = new SpriteAnimator(SpriteLibrary.OverlayFrames);

            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _animTimer.Tick += OnAnimationTick;
            _animTimer.Start();

            PointerPressed += OnPointerPressed;
            PositionChanged += OnPositionChanged;
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            _animator.IsStandby = _session.IsStandby;
            _animator.SetState(_session.FocusState);

            var frame = _animator.NextFrame();
            if (frame != null) AnimImage.Source = frame;
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            RestorePosition();
        }

        // 저장된 위치가 있으면 거기에, 없으면 주 화면 우측 하단에 놓습니다.
        // Avalonia의 Window.Position은 DIP가 아니라 실제 픽셀 좌표라서
        // 창 크기를 화면 배율(Scaling)로 곱해 계산해야 합니다.
        private void RestorePosition()
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null)
            {
                _positionRestored = true;
                return;
            }

            PixelRect area = screen.WorkingArea;
            double scaling = screen.Scaling;

            int widthPx = (int)(Width * scaling);
            int heightPx = (int)(Height * scaling);
            int marginPx = (int)(20 * scaling);

            if (_session.OverlayLeft.HasValue && _session.OverlayTop.HasValue)
            {
                int x = (int)_session.OverlayLeft.Value;
                int y = (int)_session.OverlayTop.Value;

                // 모니터 구성이 바뀌어 저장된 좌표가 화면 밖이면 기본 위치로 되돌립니다.
                bool onScreen = x >= area.X - widthPx / 2
                                && y >= area.Y - heightPx / 2
                                && x <= area.Right - widthPx / 2
                                && y <= area.Bottom - heightPx / 2;

                if (onScreen)
                {
                    Position = new PixelPoint(x, y);
                    _positionRestored = true;
                    return;
                }
            }

            Position = new PixelPoint(
                area.Right - widthPx - marginPx,
                area.Bottom - heightPx - marginPx);
            _positionRestored = true;
        }

        // WPF의 DragMove()에 해당합니다. 창 아무 데나 눌러서 끌 수 있습니다.
        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void OnPositionChanged(object? sender, PixelPointEventArgs e)
        {
            if (!_positionRestored) return;
            _session.UpdateOverlayPosition(e.Point.X, e.Point.Y);
        }

        // 트레이 메뉴의 '오버레이 창 표시'를 끄면 Hide()가 불립니다.
        // 사용자가 OS의 창 닫기로 없애 버리는 일이 없도록 닫기는 막고 숨기기만 합니다.
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        // 앱을 진짜로 종료할 때만 호출합니다.
        public void ForceClose()
        {
            AllowClose();
            _animTimer?.Stop();
            _animTimer = null;
            Close();
        }

        // 앱 종료 절차가 시작됐음을 알립니다. 이후로는 닫기를 막지 않습니다.
        // (이걸 켜지 않고 Shutdown하면 OnClosing의 취소 때문에 종료가 막힐 수 있습니다.)
        public void AllowClose() => _allowClose = true;
    }
}
