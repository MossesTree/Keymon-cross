using System;
using System.Collections.Generic;
using Keymon.Platform;
using SharpHook;
using SharpHook.Data;

namespace Keymon
{
    // MetricCollector의 역할:
    //   - 키보드/마우스 이벤트를 실시간으로 받아서 60초 롤링 큐에 저장합니다.
    //   - 앱(창) 전환을 감지하여 컨텍스트 스위치 큐에 저장합니다.
    //   - GetSnapshot()을 통해 현재 지표를 MetricSnapshot으로 묶어 외부에 제공합니다.
    //
    // 원본 keymon 대비 달라진 곳은 두 군데뿐입니다.
    //   1) user32.dll P/Invoke → IPlatformServices.TryGetActiveWindowId
    //   2) 하드코딩된 스크롤 임계값 2500 → IPlatformServices.ScrollThreshold
    // 나머지 수집/판정 로직은 한 줄도 바꾸지 않았습니다.
    public class MetricCollector
    {
        private readonly IPlatformServices _platform = PlatformServices.Current;

        private int _currentConsecutiveBackspaces = 0;
        private int _maxConsecutiveBackspaces = 0;
        private KeyCode _lastKeyCode = KeyCode.Vc0;
        private int _repeatCount = 0;

        // Queue<DateTime>은 선입선출(FIFO) 자료구조입니다.
        // 이벤트 발생 시각을 순서대로 저장하고, 60초가 지난 항목은 앞에서 제거합니다.
        // 즉, Count가 곧 '최근 60초 내 발생 횟수'가 됩니다.
        private readonly Queue<DateTime> _keyTimes = new();
        private readonly Queue<DateTime> _mouseTimes = new();
        private readonly Queue<DateTime> _backspaceTimes = new();
        private readonly Queue<DateTime> _contextSwitchTimes = new();
        private readonly Queue<DateTime> _jerkTimes = new();
        private readonly Queue<DateTime> _scrollTimes = new();

        // 마우스 방향 급전환(Jerk) 감지를 위한 임시 큐입니다.
        // 2초 내에 3회 이상 급전환이 있을 때만 _jerkTimes에 기록합니다.
        private readonly Queue<DateTime> _mouseTurnTimes = new();

        // 현재 눌린 키와 그 누른 시각을 저장해 Dwell Time(키 누름 지속 시간)을 계산합니다.
        private readonly Dictionary<KeyCode, DateTime> _pressedKeys = new();

        // 이번 분기(60초) 동안의 Dwell Time / Flight Time 누적값
        private double _minuteTotalDt;
        private int _minuteCountDt;
        private double _minuteTotalFt;
        private int _minuteCountFt;

        private DateTime _lastKeyReleaseTime = DateTime.MinValue;

        // 마우스 Jerk 계산을 위한 이전 위치/각도/시간 저장
        private int _lastMouseX = -1;
        private int _lastMouseY = -1;
        private double _lastMouseAngle = -1000;
        private DateTime _lastMouseTime = DateTime.MinValue;

        private int _scrollAccumulator = 0;

        // 창(앱) 전환 감지를 위한 이전 최전면 식별자
        //   Windows: 창 핸들(HWND)  /  macOS: 최전면 앱의 pid
        private long _lastWindowId = 0;

        // AnalysisEngine의 currentGamma 계산에 사용되는 총 누적 키 입력 수
        public int TotalAccumulatedKeys { get; set; }

        // persistence용 총 카운터 (분석에는 사용되지 않고 userData.json에 저장만 됨)
        public int TotalKeyCount { get; set; }
        public int TotalMouseCount { get; set; }
        public int TotalScrollCount { get; set; }
        public int TotalBackspaceCount { get; set; }

        // InputHookManager의 이벤트에 이 클래스의 핸들러를 연결합니다.
        public void Subscribe(InputHookManager hookManager)
        {
            hookManager.KeyPressed += OnKeyPressed;
            hookManager.KeyReleased += OnKeyReleased;
            hookManager.MousePressed += OnMousePressed;
            hookManager.MouseMoved += OnMouseMoved;
            hookManager.MouseWheel += OnMouseWheel;
        }

        // MonitoringService가 매 1초마다 호출합니다.
        // 오래된 데이터 정리 + 창 전환 감지를 수행합니다.
        public void Tick(DateTime now)
        {
            CleanOldData(now);
            MonitorWindowSwitch(now);
        }

        // 현재 수집된 지표들을 불변 스냅샷으로 패키징하여 반환합니다.
        public MetricSnapshot GetSnapshot()
        {
            double avgDt = _minuteCountDt > 0 ? _minuteTotalDt / _minuteCountDt : 0;
            double avgFt = _minuteCountFt > 0 ? _minuteTotalFt / _minuteCountFt : 0;

            return new MetricSnapshot(
                Kpm: _keyTimes.Count,
                Mpm: _mouseTimes.Count,
                BackspaceCount: _backspaceTimes.Count,
                JerkCount: _jerkTimes.Count,
                ContextSwitchCount: _contextSwitchTimes.Count,
                AvgDwellTime: avgDt,
                AvgFlightTime: avgFt,
                MaxConsecutiveBackspaces: _maxConsecutiveBackspaces,
                ScrollCount: _scrollTimes.Count
            );
        }

        // 60초 심층 분석 후 MonitoringService가 호출합니다.
        public void ResetTimingAccumulators()
        {
            _minuteTotalDt = 0;
            _minuteCountDt = 0;
            _minuteTotalFt = 0;
            _minuteCountFt = 0;

            _maxConsecutiveBackspaces = 0;
            _currentConsecutiveBackspaces = 0;
        }

        // SharpHook 백그라운드 스레드에서 호출됩니다 (키가 눌릴 때마다).
        private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
        {
            DateTime now = DateTime.Now;
            var keyCode = e.Data.KeyCode;

            lock (_keyTimes)
            {
                // 키 반복 입력(키 누른 채 유지) 방지: 이미 눌린 키면 무시
                if (_pressedKeys.ContainsKey(keyCode)) return;

                if (_lastKeyCode == keyCode)
                {
                    _repeatCount++;
                    // 똑같은 키 5번 이상 누르면 큐에 안 넣고 무시!
                    if (_repeatCount >= 5) return;
                }
                else
                {
                    _lastKeyCode = keyCode;
                    _repeatCount = 0;
                }

                TotalAccumulatedKeys++;
                TotalKeyCount++;

                if (keyCode == KeyCode.VcBackspace)
                {
                    TotalBackspaceCount++;
                    _currentConsecutiveBackspaces++;
                    _maxConsecutiveBackspaces = Math.Max(_maxConsecutiveBackspaces, _currentConsecutiveBackspaces);
                    lock (_backspaceTimes) { _backspaceTimes.Enqueue(now); }
                }
                else
                {
                    _currentConsecutiveBackspaces = 0; // 흐름 끊김
                }

                _keyTimes.Enqueue(now);

                // Flight Time: 이전 키를 뗀 시각부터 이번 키를 누른 시각까지의 간격
                if (_lastKeyReleaseTime != DateTime.MinValue)
                {
                    double ft = (now - _lastKeyReleaseTime).TotalMilliseconds;
                    if (ft < 2000) { _minuteTotalFt += ft; _minuteCountFt++; }
                }

                _pressedKeys[keyCode] = now;
            }
        }

        // 키가 떼어질 때마다 호출됩니다.
        private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
        {
            DateTime now = DateTime.Now;
            var keyCode = e.Data.KeyCode;

            lock (_keyTimes)
            {
                _lastKeyReleaseTime = now;

                if (_pressedKeys.TryGetValue(keyCode, out DateTime pressTime))
                {
                    // Dwell Time: 키를 누른 시각부터 뗀 시각까지의 지속 시간
                    double dt = (now - pressTime).TotalMilliseconds;
                    if (dt < 1000) { _minuteTotalDt += dt; _minuteCountDt++; }
                    _pressedKeys.Remove(keyCode);
                }
            }
        }

        // 마우스 버튼이 클릭될 때마다 호출됩니다.
        private void OnMousePressed(object? sender, MouseHookEventArgs e)
        {
            TotalMouseCount++;
            lock (_mouseTimes) { _mouseTimes.Enqueue(DateTime.Now); }
        }

        // 마우스 휠이 회전할 때마다 호출됩니다.
        private void OnMouseWheel(object? sender, MouseWheelHookEventArgs e)
        {
            _scrollAccumulator += e.Data.Rotation;

            // 임계값을 넘을 때마다 1회 스크롤 동작으로 간주합니다.
            // 임계값은 OS마다 다릅니다 (Windows 2500, macOS 10 기본).
            if (Math.Abs(_scrollAccumulator) >= _platform.ScrollThreshold)
            {
                TotalScrollCount++;

                // 클릭(Mpm)이나 키보드(Kpm)처럼 시간을 큐에 담습니다.
                lock (_scrollTimes) { _scrollTimes.Enqueue(DateTime.Now); }

                _scrollAccumulator = 0;
            }
        }

        // 마우스가 움직일 때마다 호출됩니다 (Jerk 감지 로직).
        private void OnMouseMoved(object? sender, MouseHookEventArgs e)
        {
            DateTime now = DateTime.Now;

            if (_lastMouseX == -1)
            {
                _lastMouseX = e.Data.X;
                _lastMouseY = e.Data.Y;
                _lastMouseTime = now;
                return;
            }

            int dx = e.Data.X - _lastMouseX;
            int dy = e.Data.Y - _lastMouseY;
            double distance = Math.Sqrt(dx * dx + dy * dy);

            if (distance > 100)
            {
                double timeElapsed = (now - _lastMouseTime).TotalSeconds;
                if (timeElapsed > 0 && (distance / timeElapsed) > 1000)
                {
                    double currentAngle = Math.Atan2(dy, dx) * (180 / Math.PI);
                    if (_lastMouseAngle != -1000)
                    {
                        double angleDiff = Math.Abs(currentAngle - _lastMouseAngle);
                        if (angleDiff > 180) angleDiff = 360 - angleDiff;

                        if (angleDiff > 135)
                        {
                            _mouseTurnTimes.Enqueue(now);
                            while (_mouseTurnTimes.Count > 0 && (now - _mouseTurnTimes.Peek()).TotalSeconds > 2)
                                _mouseTurnTimes.Dequeue();
                            if (_mouseTurnTimes.Count >= 3)
                            {
                                lock (_jerkTimes) { _jerkTimes.Enqueue(now); }
                                _mouseTurnTimes.Clear();
                            }
                        }
                    }
                    _lastMouseAngle = currentAngle;
                }
            }

            _lastMouseX = e.Data.X;
            _lastMouseY = e.Data.Y;
            _lastMouseTime = now;
        }

        // 각 큐에서 60초가 지난 오래된 항목을 앞에서부터 제거합니다.
        private void CleanOldData(DateTime now)
        {
            lock (_keyTimes) { while (_keyTimes.Count > 0 && (now - _keyTimes.Peek()).TotalSeconds > 60) _keyTimes.Dequeue(); }
            lock (_mouseTimes) { while (_mouseTimes.Count > 0 && (now - _mouseTimes.Peek()).TotalSeconds > 60) _mouseTimes.Dequeue(); }
            lock (_backspaceTimes) { while (_backspaceTimes.Count > 0 && (now - _backspaceTimes.Peek()).TotalSeconds > 60) _backspaceTimes.Dequeue(); }
            lock (_contextSwitchTimes) { while (_contextSwitchTimes.Count > 0 && (now - _contextSwitchTimes.Peek()).TotalSeconds > 60) _contextSwitchTimes.Dequeue(); }
            lock (_jerkTimes) { while (_jerkTimes.Count > 0 && (now - _jerkTimes.Peek()).TotalSeconds > 60) _jerkTimes.Dequeue(); }
            lock (_scrollTimes) { while (_scrollTimes.Count > 0 && (now - _scrollTimes.Peek()).TotalSeconds > 60) _scrollTimes.Dequeue(); }
        }

        // 최전면 대상이 바뀌었으면 컨텍스트 스위치 1회로 기록합니다.
        // Windows/macOS 판별은 IPlatformServices가 이미 끝냈으므로 여기는 OS를 모릅니다.
        private void MonitorWindowSwitch(DateTime now)
        {
            if (!_platform.TryGetActiveWindowId(out long currentId)) return;

            if (currentId != _lastWindowId && currentId != 0)
            {
                // 앱을 막 켠 첫 틱은 '전환'이 아니므로 기준값만 잡고 넘어갑니다.
                if (_lastWindowId != 0)
                    lock (_contextSwitchTimes) { _contextSwitchTimes.Enqueue(now); }

                _lastWindowId = currentId;
            }
        }

        // 절전에서 깨어났을 때, 잠든 시간만큼 모든 타임스탬프를 미래로 밀어
        // 60초 롤링 윈도가 통째로 비워지는 것을 막습니다.
        public void OffsetTime(TimeSpan offset)
        {
            // 1. 키보드 & Backspace 관련 데이터 밀어주기
            lock (_keyTimes)
            {
                ShiftQueueInPlace(_keyTimes, offset);

                var activeKeys = new List<KeyCode>(_pressedKeys.Keys);
                foreach (var key in activeKeys)
                {
                    _pressedKeys[key] += offset;
                }

                if (_lastKeyReleaseTime != DateTime.MinValue)
                    _lastKeyReleaseTime += offset;
            }

            lock (_backspaceTimes)
            {
                ShiftQueueInPlace(_backspaceTimes, offset);
            }

            // 2. 마우스 & Jerk 관련 데이터 밀어주기
            lock (_mouseTimes)
            {
                ShiftQueueInPlace(_mouseTimes, offset);
            }

            lock (_jerkTimes)
            {
                ShiftQueueInPlace(_jerkTimes, offset);
            }

            lock (_scrollTimes)
            {
                ShiftQueueInPlace(_scrollTimes, offset);
            }

            // (임시 큐도 밀어줍니다)
            ShiftQueueInPlace(_mouseTurnTimes, offset);

            if (_lastMouseTime != DateTime.MinValue)
                _lastMouseTime += offset;

            // 3. 창 전환 데이터 밀어주기
            lock (_contextSwitchTimes)
            {
                ShiftQueueInPlace(_contextSwitchTimes, offset);
            }
        }

        // 큐의 락(lock)을 유지하면서 내부 타임스탬프만 안전하게 갱신합니다.
        private void ShiftQueueInPlace(Queue<DateTime> queue, TimeSpan offset)
        {
            int count = queue.Count;
            for (int i = 0; i < count; i++)
            {
                DateTime oldTime = queue.Dequeue();
                queue.Enqueue(oldTime + offset);
            }
        }
    }
}
