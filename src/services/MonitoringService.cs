using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Keymon.Platform;

namespace Keymon
{
    // 1초마다 깨어나 수집 → 분석 → UI 반영을 조율하는 앱의 심장입니다.
    //
    // 원본 keymon 대비 달라진 곳:
    //   1) System.Windows.Threading.DispatcherTimer → Avalonia.Threading.DispatcherTimer
    //   2) Microsoft.Win32.SystemEvents 직접 구독 → IPlatformServices.StartSessionWatch
    //   3) ★신규: 시계 드리프트 감지 (절전 복귀를 OS 이벤트 없이도 잡아냅니다)
    // 분석 로직과 상태 전이는 원본 그대로입니다.
    public class MonitoringService : ISessionData
    {
        private readonly MetricCollector _collector;
        private readonly AnalysisEngine _engine;
        private readonly TrayIconManager _tray;
        private readonly PersistenceService _persistence;
        private readonly IPlatformServices _platform = PlatformServices.Current;

        private DispatcherTimer? _timer;
        private int _tickCounter;
        private int _totalSessionTicks = 0;
        private DateTime _inactiveStartTime;
        private readonly List<int> _historyScores = new();
        private readonly List<int> _historyStates = new();
        private readonly List<int> _historyFatigue = new();

        // 시계 드리프트 감지용: 마지막 틱이 언제였는지 기억해 둡니다.
        private DateTime _lastTickTime = DateTime.MinValue;

        // 1초 타이머가 이 시간보다 오래 비었으면 "그 사이 시스템이 잠들었다"로 판단합니다.
        // 일반적인 타이머 지연(수십~수백 ms)과 확실히 구분되는 값입니다.
        private static readonly TimeSpan SleepGapThreshold = TimeSpan.FromSeconds(5);

        private readonly Dictionary<string, DailyStat> _dailyStats = new();
        public Dictionary<string, DailyStat> DailyStats => new Dictionary<string, DailyStat>(_dailyStats);

        private MetricSnapshot _lastSnapshot = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

        public bool IsManualStandby { get; private set; } = false;
        public bool IsFastMode { get; private set; } = false;

        public double? OverlayLeft { get; private set; }
        public double? OverlayTop { get; private set; }

        public MonitoringService(MetricCollector collector, AnalysisEngine engine, TrayIconManager tray, PersistenceService persistence)
        {
            _collector = collector;
            _engine = engine;
            _tray = tray;
            _persistence = persistence;

            _persistence.Load(_engine, _collector, this);

            if (_engine.IsFirstAnalysisComplete || _historyScores.Count > 0)
            {
                _engine.IsFirstAnalysisComplete = true;
                _tickCounter = IsFastMode ? 10 : 60;
            }
        }

        public void Start()
        {
            // 절전/화면 잠금 알림은 OS별 구현이 담당합니다.
            // 콜백이 UI 스레드가 아닌 곳에서 올 수 있으므로 여기서 마샬링합니다.
            _platform.StartSessionWatch(
                onInactive: () => Dispatcher.UIThread.Post(HandleSystemInactive),
                onActive: () => Dispatcher.UIThread.Post(HandleSystemActive));

            _lastTickTime = DateTime.Now;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public void Stop()
        {
            _timer?.Stop();
            _platform.StopSessionWatch();
            _persistence.Save(_engine, _collector, this);
        }

        public void ToggleManualStandby()
        {
            IsManualStandby = !IsManualStandby;
            if (IsManualStandby)
            {
                _collector.ResetTimingAccumulators();
                _tickCounter = 0;
            }
            else
            {
                _engine.WakeUp();
                _tickCounter = 0;
            }
        }

        public void ToggleFastMode()
        {
            IsFastMode = !IsFastMode;
            _tickCounter = 0;
        }

        public void UpdateOverlayPosition(double left, double top)
        {
            OverlayLeft = left;
            OverlayTop = top;
        }

        private void HandleSystemInactive()
        {
            if (_inactiveStartTime != default) return; // 이미 비활성 상태

            _timer?.Stop();
            _inactiveStartTime = DateTime.Now;
            _persistence.Save(_engine, _collector, this);
        }

        private void HandleSystemActive()
        {
            if (_inactiveStartTime != default)
            {
                TimeSpan sleepDuration = DateTime.Now - _inactiveStartTime;
                _collector.OffsetTime(sleepDuration);
                _inactiveStartTime = default;
            }

            _lastTickTime = DateTime.Now;
            _timer?.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            DateTime now = DateTime.Now;

            // ── 시계 드리프트 감지 ──────────────────────────────────
            // 노트북 뚜껑을 닫았다 여는 상황에서, OS의 절전 알림을 받지 못하더라도
            // "1초 타이머인데 N초가 지나 있다"는 사실만으로 절전 구간을 알아낼 수 있습니다.
            // macOS에서 Windows의 PowerModeChanged를 대신하는 장치입니다.
            if (_lastTickTime != DateTime.MinValue)
            {
                TimeSpan gap = now - _lastTickTime;
                if (gap > SleepGapThreshold)
                {
                    // 실제로 흘렀어야 할 1초를 빼고, '잠들어 있던 시간'만큼만 밀어 줍니다.
                    _collector.OffsetTime(gap - TimeSpan.FromSeconds(1));
                    Log.Info($"절전 복귀 감지: {gap.TotalSeconds:F0}초 공백을 보정했습니다.");
                }
            }
            _lastTickTime = now;

            _collector.Tick(now);

            if (IsManualStandby)
            {
                UpdateTrayTooltip();
                return;
            }

            _lastSnapshot = _collector.GetSnapshot();
            int currentApm = _lastSnapshot.Kpm + _lastSnapshot.Mpm + _lastSnapshot.ScrollCount;

            _totalSessionTicks++;
            bool isWarmUpPeriod = _totalSessionTicks <= 300;

            int targetInterval = IsFastMode ? 10 : 60;

            if (_engine.IsStandby && !isWarmUpPeriod)
            {
                if (currentApm > 0)
                {
                    _engine.WakeUp();
                    _tickCounter = 0;
                    _collector.ResetTimingAccumulators();
                }
                else
                {
                    _tickCounter++;
                    if (_tickCounter >= targetInterval)
                    {
                        UpdateDailyStat(isStandby: true);
                        _tickCounter = 0;
                    }
                    UpdateTrayTooltip();
                    return;
                }
            }

            _tickCounter++;
            bool isSafeToDrop = _engine.IsFirstAnalysisComplete && !isWarmUpPeriod;

            _engine.UpdateRealtimeStatus(
                _lastSnapshot.Kpm,
                _lastSnapshot.Mpm + _lastSnapshot.ScrollCount,
                _lastSnapshot.ContextSwitchCount,
                isSafeToDrop
            );

            if (_tickCounter >= targetInterval)
            {
                _engine.TotalAccumulatedKeys = _collector.TotalAccumulatedKeys;
                double avgDt = _lastSnapshot.AvgDwellTime > 0 ? _lastSnapshot.AvgDwellTime : _engine.PersonalEmaDt;
                double avgFt = _lastSnapshot.AvgFlightTime > 0 ? _lastSnapshot.AvgFlightTime : _engine.PersonalEmaFt;

                _engine.PerformDeepAnalysis(
                    _lastSnapshot.Kpm,
                    _lastSnapshot.Mpm + _lastSnapshot.ScrollCount,
                    _lastSnapshot.BackspaceCount,
                    _lastSnapshot.MaxConsecutiveBackspaces,
                    _lastSnapshot.JerkCount,
                    _lastSnapshot.ContextSwitchCount,
                    avgDt,
                    avgFt
                );

                _engine.IsFirstAnalysisComplete = true;

                UpdateHistory();
                UpdateDailyStat(isStandby: false);
                _collector.ResetTimingAccumulators();
                _tickCounter = 0;
            }

            UpdateTrayTooltip();
        }

        private void UpdateDailyStat(bool isStandby = false)
        {
            try
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                int hour = DateTime.Now.Hour;

                if (!_dailyStats.ContainsKey(today))
                    _dailyStats[today] = new DailyStat { DateString = today };

                var stat = _dailyStats[today];
                stat.StateCounts ??= new int[5];
                stat.HourlyMinutes ??= new int[24];
                stat.HourlyActiveMinutes ??= new int[24];

                stat.TotalMinutes++;
                stat.HourlyMinutes[hour]++;

                if (isStandby)
                {
                    stat.StateCounts[0]++;
                }
                else
                {
                    stat.TotalActiveMinutes++;
                    stat.TotalFocusSum += _engine.FocusScore;
                    stat.TotalFatigueSum += (int)_engine.FatigueScore;

                    int stateIdx = Math.Clamp(_engine.FocusState, 0, 4);
                    stat.StateCounts[stateIdx]++;

                    stat.HourlyFocusSum[hour] += _engine.FocusScore;
                    stat.HourlyFatigueSum[hour] += (int)_engine.FatigueScore;
                    stat.HourlyActiveMinutes[hour]++;
                }
            }
            catch (Exception ex) { Log.Warn($"일별 통계 갱신 실패: {ex.Message}"); }
        }

        private void UpdateHistory()
        {
            _historyScores.Add(_engine.FocusScore);
            _historyStates.Add(_engine.FocusState);
            _historyFatigue.Add((int)_engine.FatigueScore);

            if (_historyScores.Count > 60)
            {
                _historyScores.RemoveAt(0);
                _historyStates.RemoveAt(0);
                _historyFatigue.RemoveAt(0);
            }
        }

        public void RestoreHistory(List<int> scores, List<int> states, List<int> fatigue)
        {
            if (scores != null) { _historyScores.Clear(); _historyScores.AddRange(scores); }
            if (states != null) { _historyStates.Clear(); _historyStates.AddRange(states); }
            if (fatigue != null) { _historyFatigue.Clear(); _historyFatigue.AddRange(fatigue); }
        }

        public (List<int> scores, List<int> states, List<int> fatigue) GetHistoryForSave()
        {
            return (_historyScores, _historyStates, _historyFatigue);
        }

        public void RestoreDailyStats(Dictionary<string, DailyStat> stats)
        {
            if (stats != null)
            {
                _dailyStats.Clear();
                foreach (var kvp in stats) _dailyStats[kvp.Key] = kvp.Value;
            }
        }

        private void UpdateTrayTooltip()
        {
            if (IsManualStandby)
            {
                _tray.IsStandby = true;
                _tray.UpdateTooltip("⏸️ 모니터링 일시 정지 (수동 대기)");
                return;
            }

            _tray.IsStandby = _engine.IsStandby;

            if (_engine.IsStandby)
            {
                _tray.UpdateTooltip("대기 모드 (수집 일시정지)");
                return;
            }

            if (!_engine.IsFirstAnalysisComplete)
            {
                int targetInterval = IsFastMode ? 10 : 60;
                _tray.UpdateTooltip($"패턴 분석 중... ({targetInterval - _tickCounter}초)");
                return;
            }
            string[] stateNames = { "Idle", "Distracted", "Engaged", "Focused", "Deep Focus" };
            string stateText = stateNames[Math.Clamp(_engine.FocusState, 0, 4)];
            _tray.UpdateTooltip($"{stateText} ({_engine.FocusScore}%)\nKPM: {_lastSnapshot.Kpm} | 창 전환: {_lastSnapshot.ContextSwitchCount}회");
            _tray.UpdateAnimationByState(_engine.FocusState);
        }

        public bool IsFirstAnalysisComplete => _engine.IsFirstAnalysisComplete;
        public int RemainingSeconds => (IsFastMode ? 10 : 60) - _tickCounter;
        public int FocusScore => _engine.FocusScore;
        public int StressScore => _engine.StressScore;
        public double FatigueScore => _engine.FatigueScore;
        public int CurrentKpm => _lastSnapshot.Kpm;
        public int CurrentMpm => _lastSnapshot.Mpm;
        public int CurrentApm => _lastSnapshot.Kpm + _lastSnapshot.Mpm + _lastSnapshot.ScrollCount;
        public int BackspaceCount => _lastSnapshot.BackspaceCount;
        public int JerkCount => _lastSnapshot.JerkCount;
        public int ContextSwitchCount => _lastSnapshot.ContextSwitchCount;
        public int FocusState => _engine.FocusState;
        public int FatigueState => _engine.FatigueState;
        public string StateReason => _engine.StateReason;
        public List<int> HistoryScores => new List<int>(_historyScores);
        public List<int> HistoryFatigue => new List<int>(_historyFatigue);
        public bool IsStandby => _engine.IsStandby;
    }
}
