using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Keymon.Platform;

namespace Keymon
{
    // 실시간 지표와 일별 통계를 보여주는 대시보드입니다.
    // 화면 구성과 계산식은 원본 keymon과 동일하고, WPF API만 Avalonia로 옮겼습니다.
    //
    // 주요 대응 관계:
    //   ColorConverter.ConvertFromString  →  Color.Parse
    //   Line(X1,Y1,X2,Y2)                 →  Line(StartPoint, EndPoint)
    //   MouseLeftButtonUp                 →  PointerReleased
    //   Storyboard(XAML 리소스)           →  30fps 타이머 기반 변환 갱신(코드)
    public partial class DashboardWindow : Window
    {
        private readonly ISessionData _session;
        private readonly DispatcherTimer _uiTimer;
        private string _selectedDateString;
        private int _currentAnimState = -1;

        private bool _allowClose;

        // 캐릭터(이모지)에 붙일 변환들. 애니메이션 타이머가 이 값들을 직접 갱신합니다.
        private readonly ScaleTransform _charScale = new(1, 1);
        private readonly RotateTransform _charRotate = new(0);
        private readonly TranslateTransform _charTranslate = new(0, 0);
        private DispatcherTimer? _charAnimTimer;
        private double _charPhase;

        private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

        public DashboardWindow(ISessionData session)
        {
            InitializeComponent();
            _session = session;

            _selectedDateString = DateTime.Now.ToString("yyyy-MM-dd");

            TxtCharacter.RenderTransform = new TransformGroup
            {
                Children = { _charScale, _charRotate, _charTranslate }
            };

            BtnManualStandby.Click += BtnManualStandby_Click;
            BtnFastMode.Click += BtnFastMode_Click;
            BtnOpenPermission.Click += (s, e) => PlatformServices.Current.RequestInputPermission();

            // macOS에서 권한이 없으면 아무리 타이핑해도 KPM이 0입니다.
            // 원인을 모른 채 헤매지 않도록 눈에 띄는 배너로 알려 줍니다.
            PermissionBanner.IsVisible = App.Hook?.IsPermissionMissing == true;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += UpdateRealTimeTab;
            _uiTimer.Start();

            RefreshDailyTab();
        }

        // ────────────────────────────────────────────────────────────
        // 탭 1: 실시간
        // ────────────────────────────────────────────────────────────
        private void UpdateRealTimeTab(object? sender, EventArgs e)
        {
            try
            {
                BtnFastMode.IsChecked = _session.IsFastMode;

                if (_session.IsManualStandby)
                {
                    BtnManualStandby.IsChecked = true;

                    TxtStatus.Text = "나의 집중 패턴 모니터링";
                    TxtStateTitle.Text = "수동 대기 모드";
                    TxtCharacter.Text = "⏸️";
                    TxtCharState.Text = "분석이 일시 정지되었습니다. (개인 시간)";
                    TxtReason.Text = "영상 시청 또는 휴식을 위해 데이터 분석을 멈추었습니다.";

                    TxtKpm.Text = "-"; TxtMpm.Text = "-"; TxtApm.Text = "-";
                    TxtFocus.Text = "- %"; TxtEr.Text = "- 회"; TxtCsr.Text = "- 번";
                    TxtJerk.Text = "- 회"; TxtFatigue.Text = "- 점";

                    BarUpdate.Value = 0;
                    TxtUpdateSec.Text = "중지";

                    StopCharacterAnimation();
                    _currentAnimState = -1;
                    return;
                }

                BtnManualStandby.IsChecked = false;

                TxtKpm.Text = _session.CurrentKpm.ToString();
                TxtMpm.Text = _session.CurrentMpm.ToString();
                TxtApm.Text = _session.CurrentApm.ToString();
                TxtFocus.Text = $"{_session.FocusScore}%";
                TxtEr.Text = $"{_session.BackspaceCount} 회";
                TxtCsr.Text = $"{_session.ContextSwitchCount} 번";
                TxtJerk.Text = $"{_session.JerkCount} 회";
                TxtFatigue.Text = $"{(int)_session.FatigueScore} 점";

                if (_session.FatigueScore >= 71) TxtFatigue.Foreground = new SolidColorBrush(Colors.IndianRed);
                else if (_session.FatigueScore >= 31) TxtFatigue.Foreground = new SolidColorBrush(Colors.DarkOrange);
                else TxtFatigue.Foreground = new SolidColorBrush(Colors.MediumSeaGreen);

                TxtReason.Text = _session.StateReason;

                BarUpdate.Maximum = _session.IsFastMode ? 10 : 60;
                BarUpdate.Value = Math.Max(0, _session.RemainingSeconds);
                TxtUpdateSec.Text = $"{_session.RemainingSeconds}초";

                if (_selectedDateString == DateTime.Now.ToString("yyyy-MM-dd"))
                {
                    RefreshDailyTab();
                }

                bool isDataReady = _session.IsFirstAnalysisComplete || _session.HistoryScores.Count > 0;

                if (!isDataReady && !_session.IsStandby)
                {
                    TxtStatus.Text = "나의 집중 패턴 모니터링";
                    TxtCharacter.Text = "⏳";
                    TxtStateTitle.Text = "패턴 분석 준비";
                    TxtCharState.Text = "데이터를 불러오고 있습니다...";
                    UpdateCharacterAnimation(0);
                    return;
                }

                TxtStatus.Text = "나의 집중 패턴 모니터링";

                string[] stateTitles = { "IDLE (대기)", "산만", "평안 (안정)", "집중", "초집중" };
                string[] stateDescs = {
                    "작업 흐름이 일시 정지되었습니다.",
                    "주의력이 분산되고 있습니다. 다시 집중해 보세요!",
                    "안정적이고 편안한 페이스로 작업 중입니다.",
                    "좋은 몰입도를 유지하고 있습니다.",
                    "최고의 효율! 고도의 몰입 상태입니다."
                };
                string[] stateEmojis = { "💤", "👀", "😌", "💻", "🔥" };

                int currentState = Math.Clamp(_session.FocusState, 0, 4);

                string finalTitle = stateTitles[currentState];
                string finalDesc = stateDescs[currentState];
                string finalEmoji = stateEmojis[currentState];

                if (_session.FatigueScore >= 71)
                {
                    finalTitle = "탈진 (휴식 필요)";
                    finalEmoji = "😵";
                    finalDesc = "인지 능력이 한계에 도달했습니다. 즉시 휴식이 필요합니다. 🚨";
                }

                TxtStateTitle.Text = finalTitle;
                TxtCharacter.Text = finalEmoji;
                TxtCharState.Text = finalDesc;
                TxtReason.Text = _session.StateReason;

                UpdateCharacterAnimation(currentState);
            }
            catch (Exception ex) { Log.Warn($"실시간 UI 업데이트 실패: {ex.Message}"); }
        }

        // ────────────────────────────────────────────────────────────
        // 캐릭터 애니메이션
        //
        // 원본 keymon은 XAML에 Storyboard 5종을 선언해 두고도 실제로 재생하는
        // UpdateCharacterAnimation()이 빈 메서드로 남아 있었습니다(미완성).
        // Avalonia로 옮기면서 그 의도대로 5종을 실제로 구현했습니다.
        //
        // Avalonia의 Animation 클래스는 Transform 객체를 직접 겨냥하지 못하고
        // RenderTransform 보간에도 별도 애니메이터 등록이 필요합니다. 여기서는
        // 30fps 타이머로 사인파를 계산해 변환 값을 직접 써 넣는 편이 훨씬 단순해서
        // 그렇게 구현했습니다. WPF의 AutoReverse 왕복과 시각적으로 동일합니다.
        // ────────────────────────────────────────────────────────────
        private void UpdateCharacterAnimation(int focusState)
        {
            if (_currentAnimState == focusState) return;

            _currentAnimState = focusState;
            _charPhase = 0;
            ResetCharacterTransform();

            _charAnimTimer ??= CreateCharacterTimer();
            _charAnimTimer.Start();
        }

        private DispatcherTimer CreateCharacterTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // 약 30fps
            timer.Tick += OnCharacterTick;
            return timer;
        }

        private void OnCharacterTick(object? sender, EventArgs e)
        {
            _charPhase += 0.033;

            switch (_currentAnimState)
            {
                case 0: // 대기: 천천히 부풀었다 줄었다
                    _charScale.ScaleX = _charScale.ScaleY = Osc(1, 1.05, 2.0);
                    break;

                case 1: // 산만: 좌우로 흔들림
                    _charRotate.Angle = Osc(-15, 15, 0.5);
                    _charTranslate.X = Osc(-5, 5, 0.3);
                    break;

                case 2: // 평안: 아주 작게 기울임
                    _charRotate.Angle = Osc(-3, 3, 1.0);
                    break;

                case 3: // 집중: 위아래로 둥실
                    _charTranslate.Y = Osc(0, -5, 0.8);
                    break;

                case 4: // 초집중: 빠른 진동 + 확대
                    _charTranslate.Y = Osc(-2, 2, 0.1);
                    _charScale.ScaleX = _charScale.ScaleY = Osc(1.1, 1.15, 0.2);
                    break;
            }
        }

        // from ↔ to 사이를 seconds 초에 걸쳐 한 방향으로 왕복하는 사인파 값.
        // (왕복 한 바퀴는 2 * seconds 초가 걸립니다 = WPF의 Duration + AutoReverse)
        private double Osc(double from, double to, double seconds)
        {
            double u = (Math.Sin(_charPhase * Math.PI / seconds) + 1) / 2; // 0 ~ 1
            return from + (to - from) * u;
        }

        private void StopCharacterAnimation()
        {
            _charAnimTimer?.Stop();
            ResetCharacterTransform();
        }

        private void ResetCharacterTransform()
        {
            _charScale.ScaleX = 1;
            _charScale.ScaleY = 1;
            _charRotate.Angle = 0;
            _charTranslate.X = 0;
            _charTranslate.Y = 0;
        }

        // ────────────────────────────────────────────────────────────
        // 탭 2: 통계
        // ────────────────────────────────────────────────────────────
        private void RefreshDailyTab()
        {
            try { DrawDateSelector(); DrawDailySummary(); DrawDailyLineChart(); DrawStateDistribution(); }
            catch (Exception ex) { Log.Warn($"통계 UI 렌더링 실패: {ex.Message}"); }
        }

        private void DrawDateSelector()
        {
            DatePanel.Children.Clear();
            DateTime now = DateTime.Now;

            for (int i = 6; i >= 0; i--)
            {
                DateTime date = now.Date.AddDays(-i);
                string dateStr = date.ToString("yyyy-MM-dd");

                bool isSelected = (_selectedDateString == dateStr);
                bool hasData = _session.DailyStats.ContainsKey(dateStr) && _session.DailyStats[dateStr].TotalMinutes > 0;

                string scoreText = hasData ? $"{_session.DailyStats[dateStr].AvgFocus}%" : "-";
                string dotColor = hasData ? "#3B82F6" : "#CBD5E1";

                var border = new Border
                {
                    Width = 60,
                    Height = 75,
                    Margin = new Thickness(0, 0, 8, 0),
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(isSelected ? Color.Parse("#EFF6FF") : Colors.Transparent),
                    BorderBrush = new SolidColorBrush(Color.Parse(isSelected ? "#3B82F6" : "#E2E8F0")),
                    BorderThickness = new Thickness(1),
                    Cursor = HandCursor
                };

                border.PointerReleased += (s, e) => { _selectedDateString = dateStr; RefreshDailyTab(); };

                var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                panel.Children.Add(new TextBlock
                {
                    Text = $"{date:M/d}\n{date:ddd}",
                    TextAlignment = TextAlignment.Center,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse(isSelected ? "#3B82F6" : "#64748B"))
                });
                panel.Children.Add(new TextBlock
                {
                    Text = scoreText,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, 4, 0, 4),
                    Foreground = new SolidColorBrush(Color.Parse(isSelected ? "#3B82F6" : "#334155"))
                });
                panel.Children.Add(new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = new SolidColorBrush(Color.Parse(dotColor)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                border.Child = panel;
                DatePanel.Children.Add(border);
            }
        }

        private void DrawDailySummary()
        {
            if (!_session.DailyStats.ContainsKey(_selectedDateString)) return;
            var stat = _session.DailyStats[_selectedDateString];
            DateTime dt = DateTime.Parse(_selectedDateString);
            TxtDailyReportTitle.Text = $"📋 {dt:M월 d일} 리포트";

            if (stat.TotalMinutes == 0) return;

            int pureFocusMinutes = stat.StateCounts[3] + stat.StateCounts[4];
            int totalLoggedMinutes = stat.TotalWorkMinutes;
            int distractedMinutes = stat.StateCounts[1];
            int idleMinutes = stat.StateCounts[0];

            TxtDailyAvgFocus.Text = $"{stat.AvgFocus}%";
            TxtDailyAvgFatigue.Text = $"{stat.AvgFatigue}점";

            TxtDailyWorkTime.Text = pureFocusMinutes >= 60 ? $"{pureFocusMinutes / 60}h {pureFocusMinutes % 60}m" : $"{pureFocusMinutes}m";
            TxtDailyIdleTime.Text = idleMinutes >= 60 ? $"{idleMinutes / 60}h {idleMinutes % 60}m" : $"{idleMinutes}m";

            if (pureFocusMinutes > 0 && pureFocusMinutes >= (distractedMinutes + idleMinutes))
                TxtSummarySentence.Text = "훌륭합니다! 오늘 하루 집중을 아주 높은 비중으로 잘 유지하셨네요.";
            else if (idleMinutes > totalLoggedMinutes * 0.5)
                TxtSummarySentence.Text = "오늘은 컴퓨터를 켜두고 중간중간 자리를 비우거나 휴식한 시간이 많았습니다.";
            else if (distractedMinutes > 0)
                TxtSummarySentence.Text = "창 전환이나 입력 리듬 끊김이 다소 있었습니다. 한 가지 작업에 조금 더 몰두해 보세요.";
            else
                TxtSummarySentence.Text = "안정적인 업무 컨디션으로 큰 기복 없이 차분하게 작업을 진행하셨습니다.";

            if (stat.AvgFocus >= 60) { TxtDailyHint1Title.Text = "훌륭한 집중력"; TxtDailyHint1Desc.Text = "방해 요소 없이 스스로 작업 흐름을 제어하는 능력이 탁월합니다."; }
            else if (distractedMinutes > 0) { TxtDailyHint1Title.Text = "주의력 분산 감지"; TxtDailyHint1Desc.Text = "내일은 창 전환을 줄이고 하나의 메인 태스크에 집중해 보세요."; }
            else if (idleMinutes > stat.TotalWorkMinutes * 0.3) { TxtDailyHint1Title.Text = "잦은 흐름 끊김"; TxtDailyHint1Desc.Text = "입력이 멈춘 대기 시간이 많아 평균 집중도가 낮아졌습니다. 몰입 시간을 늘려보세요."; }
            else { TxtDailyHint1Title.Text = "얕은 몰입 상태"; TxtDailyHint1Desc.Text = "작업은 꾸준히 했지만 깊은 몰입(Deep Focus)으로 연결되지는 못했습니다."; }

            TxtDailyHint2Title.Text = stat.AvgFatigue >= 60 ? "피로도 컨디션 경고" : "안정적인 에너지 조절";
            TxtDailyHint2Desc.Text = stat.AvgFatigue >= 60
                ? "뇌가 많이 피로한 상태입니다. 작업 종료 후 완전한 휴식이 필요합니다."
                : "체력 소모가 과도하지 않도록 페이스 배분을 아주 잘 조절했습니다.";
        }

        // 차트 좌표계 (Canvas 600 x 170 기준)
        //   y = 10  → 100점,  y = 75 → 50점,  y = 140 → 0점
        //   x = 20 ~ 600 구간에 시간대를 균등 배치하고, 시각 라벨은 y = 148에 둡니다.
        private const double ChartLeft = 20;
        private const double ChartWidth = 580;
        private const double ChartTop = 10;
        private const double ChartHeight = 130;

        private void DrawDailyLineChart()
        {
            DailyChartCanvas.Children.Clear();
            DrawChartGrid();

            if (!_session.DailyStats.ContainsKey(_selectedDateString)) return;
            var stat = _session.DailyStats[_selectedDateString];

            stat.HourlyActiveMinutes ??= new int[24];
            stat.HourlyMinutes ??= new int[24];
            stat.HourlyFocusSum ??= new int[24];
            stat.HourlyFatigueSum ??= new int[24];

            bool isToday = _selectedDateString == DateTime.Now.ToString("yyyy-MM-dd");
            int currentHour = DateTime.Now.Hour;

            var activeHours = new List<int>();
            for (int h = 0; h < 24; h++)
            {
                if (isToday && h >= currentHour) continue;
                if (stat.HourlyMinutes[h] > 0 && stat.HourlyActiveMinutes[h] > 0) activeHours.Add(h);
            }

            if (activeHours.Count == 0) return;

            const double maxVal = 100;
            const double paddingX = 25;
            double stepX = activeHours.Count > 1 ? (ChartWidth - paddingX * 2) / (activeHours.Count - 1) : 0;

            var focusPoints = new List<Point>();
            var fatiguePoints = new List<Point>();

            var focusBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
            var fatigueBrush = new SolidColorBrush(Color.Parse("#F59E0B"));

            for (int i = 0; i < activeHours.Count; i++)
            {
                int h = activeHours[i];

                double x = ChartLeft + (activeHours.Count == 1 ? ChartWidth / 2 : paddingX + (i * stepX));

                double fScore = (double)stat.HourlyFocusSum[h] / stat.HourlyActiveMinutes[h];
                double fFatigue = (double)stat.HourlyFatigueSum[h] / stat.HourlyActiveMinutes[h];

                double focusY = ChartTop + (ChartHeight - ((fScore / maxVal) * ChartHeight));
                double fatigueY = ChartTop + (ChartHeight - ((fFatigue / maxVal) * ChartHeight));

                focusPoints.Add(new Point(x, focusY));
                fatiguePoints.Add(new Point(x, fatigueY));

                AddDot(x, focusY, focusBrush);
                AddLabel(Math.Round(fScore).ToString(), x - 8, focusY - 18, focusBrush, 11, FontWeight.Bold);

                AddDot(x, fatigueY, fatigueBrush);
                AddLabel(Math.Round(fFatigue).ToString(), x - 8, fatigueY + 6, fatigueBrush, 11, FontWeight.Bold);

                AddLabel($"{h}시", x - 10, 148, new SolidColorBrush(Color.Parse("#64748B")), 11, FontWeight.SemiBold);
            }

            // 선은 점보다 뒤에 깔려야 하므로 마지막에 맨 앞으로 끼워 넣습니다.
            DailyChartCanvas.Children.Insert(0, new Polyline { Stroke = focusBrush, StrokeThickness = 2, Points = focusPoints });
            DailyChartCanvas.Children.Insert(0, new Polyline { Stroke = fatigueBrush, StrokeThickness = 2, Points = fatiguePoints });
        }

        private void DrawChartGrid()
        {
            var gridBrush = new SolidColorBrush(Color.Parse("#E2E8F0"));
            var labelBrush = new SolidColorBrush(Color.Parse("#94A3B8"));

            (double y, string label)[] rows =
            {
                (ChartTop, "100"),
                (ChartTop + ChartHeight / 2, "50"),
                (ChartTop + ChartHeight, "0")
            };

            foreach (var (y, label) in rows)
            {
                DailyChartCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(ChartLeft, y),
                    EndPoint = new Point(ChartLeft + ChartWidth, y),
                    Stroke = gridBrush,
                    StrokeThickness = 1
                });

                AddLabel(label, 0, y - 7, labelBrush, 10, FontWeight.Normal);
            }
        }

        private void AddDot(double x, double y, IBrush stroke)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = stroke,
                StrokeThickness = 2
            };
            Canvas.SetLeft(dot, x - 4);
            Canvas.SetTop(dot, y - 4);
            DailyChartCanvas.Children.Add(dot);
        }

        private void AddLabel(string text, double x, double y, IBrush brush, double fontSize, FontWeight weight)
        {
            var label = new TextBlock { Text = text, FontSize = fontSize, FontWeight = weight, Foreground = brush };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            DailyChartCanvas.Children.Add(label);
        }

        private void DrawStateDistribution()
        {
            StateDistributionPanel.Children.Clear();
            StateDistributionPanel.Children.Add(new TextBlock
            {
                Text = "🔘 집중 상태 분포",
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.Parse("#64748B")),
                Margin = new Thickness(0, 0, 0, 15)
            });

            if (!_session.DailyStats.ContainsKey(_selectedDateString)) return;

            var stat = _session.DailyStats[_selectedDateString];
            stat.StateCounts ??= new int[5];

            int total = stat.TotalWorkMinutes;
            if (total == 0) return;

            string[] names = { "Deep Focus", "Focused", "Engaged", "Distracted", "Idle" };
            string[] colors = { "#3B82F6", "#10B981", "#0EA5E9", "#F59E0B", "#94A3B8" };
            int[] mapIdx = { 4, 3, 2, 1, 0 };

            for (int i = 0; i < 5; i++)
            {
                int count = stat.StateCounts[mapIdx[i]];
                double pct = Math.Round((double)count / total * 100);
                string timeStr = count >= 60 ? $"{count / 60}시간 {count % 60}분" : $"{count}분";

                var row = new Grid
                {
                    Margin = new Thickness(0, 0, 0, 12),
                    ColumnDefinitions = new ColumnDefinitions("20,80,*,70,35")
                };

                row.Children.Add(new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(Color.Parse(colors[i])),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var nameTb = new TextBlock
                {
                    Text = names[i],
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#334155")),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(nameTb, 1);
                row.Children.Add(nameTb);

                var fgBar = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = pct * 1.5,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.Parse(colors[i]))
                };
                var bgBar = new Border
                {
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.Parse("#F1F5F9")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 15, 0),
                    Child = fgBar
                };
                Grid.SetColumn(bgBar, 2);
                row.Children.Add(bgBar);

                var timeTb = new TextBlock
                {
                    Text = timeStr,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#334155")),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(timeTb, 3);
                row.Children.Add(timeTb);

                var pctTb = new TextBlock
                {
                    Text = $"{pct}%",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(pctTb, 4);
                row.Children.Add(pctTb);

                StateDistributionPanel.Children.Add(row);
            }
        }

        // ────────────────────────────────────────────────────────────
        private void BtnManualStandby_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_session is MonitoringService service)
            {
                service.ToggleManualStandby();
                UpdateRealTimeTab(null, EventArgs.Empty);
            }
        }

        private void BtnFastMode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_session is MonitoringService service)
            {
                service.ToggleFastMode();
                UpdateRealTimeTab(null, EventArgs.Empty);
            }
        }

        // 창을 닫아도 앱은 트레이에 남습니다. (원본과 동일)
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        public void ForceClose()
        {
            AllowClose();
            _uiTimer.Stop();
            StopCharacterAnimation();
            Close();
        }

        // 앱 종료 절차가 시작됐음을 알립니다. 이후로는 닫기를 막지 않습니다.
        // (이걸 켜지 않고 Shutdown하면 OnClosing의 취소 때문에 종료가 막힐 수 있습니다.)
        public void AllowClose() => _allowClose = true;
    }
}
