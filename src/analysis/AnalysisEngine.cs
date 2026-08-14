using System;
using System.Collections.Generic;
using System.Linq;

namespace Keymon
{
    // 1분 데이터
    public class MinuteStat
    {
        public DateTime Timestamp { get; set; }
        public int FocusScore { get; set; }
        public double FatigueScore { get; set; }
        public int FatigueState { get; set; }
    }

    // 사용자의 입력 패턴을 통계적(Z-Score, EMA)으로 분석하여 
    // 실시간 집중도(Focus)와 생체 모방형 피로도(Fatigue)를 산출하는 핵심 엔진입니다.
    public class AnalysisEngine
    {
        // ---------------------------------------------------------
        // 1. 시스템 상수 (글로벌 베이스라인)
        // ---------------------------------------------------------
        // 사용자의 초기 데이터가 없을 때 기준으로 삼는 전역 평균값
        private const double GlobalAvgKpm = 200.0;
        private const double GlobalAvgEr = 0.05;
        private const double GlobalAvgDt = 100.0;
        private const double GlobalAvgFt = 400.0;
        private const double GlobalAvgMj = 15.0;
        private const double Alpha = 0.1; // 지수이동평균(EMA) 반영 비율 (최근 데이터 가중치)

        // ---------------------------------------------------------
        // 2. 개인화 학습 지표 (EMA & Variance)
        // ---------------------------------------------------------
        // 사용자의 평소 작업 스타일을 학습한 지수이동평균(EMA) 및 분산(Variance) 데이터
        public double PersonalEmaKpm { get; set; }
        public double PersonalEmaEr { get; set; }
        public double PersonalEmaDt { get; set; }
        public double PersonalEmaFt { get; set; }
        public double PersonalEmaMj { get; set; }
        public double PersonalVarKpm { get; set; }
        public double PersonalVarEr { get; set; }
        public double PersonalVarDt { get; set; }
        public double PersonalVarFt { get; set; }
        public double PersonalVarMj { get; set; }
        public int TotalAccumulatedKeys { get; set; }

        // 사용자가 자리에서 이탈하지 않고 논리적으로 연속 작업한 시간 (분 단위)
        public int ContinuousWorkMinutes { get; set; }

        // 학술 근거 2 적용: 몰입(Flow) 상태 관성을 위한 내부 카운터 (3분 이상 유지 시 Zone 진입) 및 집중이 깨졌을 때 강등을 유예하는 2분 카운터
        private int _deepFocusStreak = 0;
        private int _distractionStreak = 0;

        private int _minuteCounter = 0;
        private int _previousState = 2;
        public bool IsStandby { get; private set; } = false;
        private int _idleMinutes = 0;
        private bool _isRecovering = false;

        // ---------------------------------------------------------
        // 3. 외부 노출용 분석 결과 (모니터링/UI 계층 바인딩용)
        // ---------------------------------------------------------
        public int FocusScore { get;  set; }        // 최종 산출된 집중도 점수 (0~100)
        public int StressScore { get; private set; }       // 오타율 및 거친 마우스 조작 기반 뇌 과부하 수치 (0~100)
        public double FatigueScore { get; set; }  // 누적된 인지적/신체적 피로도 수치 (0~100)
        public int FocusState { get;  set; }        // 현재 집중 상태 (0:휴식, 1:산만, 2:안정, 3:집중, 4:완벽한 몰입)
        public int FatigueState { get; private set; }      // 현재 피로도 경고 상태 (1:안전, 2:주의, 3:위험)
        public string StateReason { get;  set; } = "데이터 분석 중..."; // 상태 판별 근거 메시지 (UI 출력용)
        public bool IsFirstAnalysisComplete { get; set; } = false;

        // 테스트 및 디버깅용 피로도 누적 배속 (기본값 1.0 = 리얼타임)
        public double FatigueTimeScale { get; set; } = 1.0;

        public event Action<MinuteStat>? OnMinuteAnalyzed;

        public void WakeUp()
        {
            IsStandby = false;
            _idleMinutes = 0;
            StateReason = "⚡ 작업 재개 감지! 수집을 다시 시작합니다.";
        }

        // ---------------------------------------------------------
        // 5. 실시간 상태 판별 로직 (1초 주기 호출)
        // ---------------------------------------------------------
        // 즉각적인 유휴(Idle) 상태나 산만함을 감지합니다.
        public void UpdateRealtimeStatus(int currentKpm, int currentMpm, int currentCsr, bool isFirstComplete)
        {
            int apm = currentKpm + currentMpm;

            // 물리적 입력이 기준치 미만일 때 (즉각적인 작업 중단 감지)
            if (apm < 15 && isFirstComplete)
            {
                FocusScore = 0;
                if (FocusState < 3)
                {
                    if (FatigueScore > 0) FatigueScore = Math.Max(0, FatigueScore - (0.1 * FatigueTimeScale));

                    // 입력이 없으므로 스트레스 수치 점진적 하락
                    if (StressScore > 0) StressScore = Math.Max(0, StressScore - 5);
                }
            }
        }

        // ---------------------------------------------------------
        // 6. 심층 분석 로직 (60초 주기 호출)
        // ---------------------------------------------------------
        // 학술 근거 1: 감성 컴퓨팅(Affective Computing)의 Time-Window 모델 적용
        // 노이즈(짧은 멈춤)를 필터링하고 통계적 유의성을 확보하기 위해 
        // 1초 주기의 실시간성과 60초 슬라이딩 윈도우(Sliding Window) 누적 방식을 혼합하여 사용합니다.
        // 💡 MetricCollector와의 데이터 동기화를 위해 maxConsecutiveBackspaces 매개변수를 완전히 복구했습니다.
        public void PerformDeepAnalysis(int kpm, int mpm, int backspace, int maxConsecutiveBackspaces, int jerk, int csr, double avgDt, double avgFt)
        {
            // 인간의 물리적 타수 한계(약 800타)를 초과하면 정상적인 몰입이 아닌 시스템/외부 오류로 판단합니다.
            if (kpm > 800 || mpm > 1000)
            {
                FocusScore = 0;
                FocusState = 1;       // 비정상 상태(산만/에러)로 간주
                _deepFocusStreak = 0; // 몰입 관성 스택 강제 초기화
                StateReason = "⚠️ 비정상적인 폭주 입력(매크로/키 눌림 등)이 감지되어 분석을 차단합니다.";
                return; // 가장 중요: 아래의 복잡한 로직을 아예 실행하지 않고 여기서 즉시 함수 종료(Early Return)
            }

            int apm = kpm + mpm;

            if (apm < 15) _idleMinutes++;
            else _idleMinutes = 0;

            if (_idleMinutes >= 5)
            {
                IsStandby = true;
                FocusScore = 0;
                FocusState = 0;
                StateReason = "💤 장기 대기 모드 (데이터 수집 일시정지됨)";
                return;
            }

            double currentER = 0;

            // 의도적 삭제 시 오타 페널티를 면제합니다.
            bool isRewriting = maxConsecutiveBackspaces >= 7;

            if (isRewriting)
            {
                int pureErrors = Math.Max(0, backspace - maxConsecutiveBackspaces);
                currentER = kpm > 0 ? (double)pureErrors / kpm : 0;
            }
            else
            {
                currentER = kpm > 0 ? (double)backspace / kpm : 0;
            }

            if (currentER > 1.0)
            {
                FocusScore = 0; FocusState = 1; _deepFocusStreak = 0; _distractionStreak = 0;
                StateReason = "⚠️ 친 글자보다 지운 글자가 많습니다. 단순 삭제 작업이거나 키 눌림이 의심됩니다.";
                return;
            }

            // [1] 학습 데이터 로드 (학습 전이면 글로벌 평균 사용)
            double prevEmaKpm = PersonalEmaKpm == 0 ? GlobalAvgKpm : PersonalEmaKpm;
            double prevEmaEr = PersonalEmaEr == 0 ? GlobalAvgEr : PersonalEmaEr;
            double prevEmaDt = PersonalEmaDt == 0 ? GlobalAvgDt : PersonalEmaDt;
            double prevEmaFt = PersonalEmaFt == 0 ? GlobalAvgFt : PersonalEmaFt;
            double prevEmaMj = PersonalEmaMj == 0 ? GlobalAvgMj : PersonalEmaMj;

            // 데이터 부족 시 표준편차가 0에 수렴하여 Z-Score가 무한대가 되는 현상(ZeroDivision) 방어
            double stdKpm = Math.Max(Math.Sqrt(PersonalVarKpm), 5.0);
            double stdEr = Math.Max(Math.Sqrt(PersonalVarEr), 0.02);
            double stdDt = Math.Max(Math.Sqrt(PersonalVarDt), 10.0);
            double stdMj = Math.Max(Math.Sqrt(PersonalVarMj), 2.0);

            // [2] Z-Score(표준화 점수) 산출: 사용자의 '평소 패턴' 대비 현재 상태의 편차 계산
            double zKpm = (kpm - prevEmaKpm) / stdKpm;
            double zEr = (currentER - prevEmaEr) / stdEr;
            double zDt = kpm > 0 ? (avgDt - prevEmaDt) / stdDt : 0;
            double zMj = (jerk - prevEmaMj) / stdMj;

            // [3] 뇌 과부하(StressScore) 계산: 오타율, 거친 마우스 움직임, 키 체공 시간의 비정상적 증가를 가중 합산
            double combinedZ = (0.5 * zEr) + (0.3 * zMj) + (0.2 * zDt);
            StressScore = (int)Math.Clamp(Math.Max(0, combinedZ) * 33, 0, 100);

            // [4] 파이프라인 실행: 피로도를 '먼저' 갱신하고, 그 피로도를 바탕으로 상태를 판별합니다.
            // 자아 고갈(Ego Depletion) 이론 적용을 위해 순서가 매우 중요합니다.
            UpdateFatigue(zKpm, apm);
            DetermineState(apm, csr, zKpm, zEr, zMj, isRewriting);


            _minuteCounter++;
            if (_minuteCounter >= 5)
            {
                // 5분마다 상태 확정 및 초기화
                _minuteCounter = 0;
            }

                // [5] 최종 집중도 점수 산출 (페널티 적용)
                double zErPositive = Math.Max(0, zEr);
            double erPenalty = zErPositive > 1.0 ? Math.Pow(zErPositive, 1.5) * 10 : zErPositive * 5; // 오타율 비선형 페널티
            double csrPenalty = Math.Pow(csr, 1.5) * 1.5;
            double speedBonus = Math.Clamp((zKpm * 10) + (mpm * 0.1), -20, 25);

            // 피로도가 높을수록 점수 천장 강제 하향 조정
            double fatiguePenalty = (FatigueScore / 100.0) * 20;
            double rawFocus = 75 + speedBonus - erPenalty - csrPenalty - fatiguePenalty;

            // 아직 완벽한 몰입(FocusState 4)에 도달하지 못했다면, 점수가 90점을 넘지 못하도록 제한합니다.
            if (FocusState < 4 && rawFocus > 89)
            {
                rawFocus = 89; // 89점에서 '예열 중'이라는 느낌을 줌
            }

            // [6] UI 차트 요동침 방지 (EMA 기반 점수 스무딩)
            int targetFocusScore = apm < 15 ? 0 : (int)Math.Clamp(rawFocus, 0, 100);

            if (!IsFirstAnalysisComplete || FocusScore == 0)
            {
                // 초기 진입 및 휴식 상태에서 복귀 시에는 즉각 반응
                FocusScore = targetFocusScore;
            }
            else
            {
                // 이전 점수 30%, 새로운 타겟 점수 70% 비중으로 부드러운 차트 곡선 유도
                FocusScore = (int)((FocusScore * 0.3) + (targetFocusScore * 0.7));
            }

            // [7] 베이스라인 업데이트: 정상적인 작업 흐름(Level 2 이상)일 때만 학습하여 데이터 오염 방지
            if (FocusState >= 2)
            {
                UpdateBaseline(kpm, currentER, avgDt, avgFt, jerk, prevEmaKpm, prevEmaEr, prevEmaDt, prevEmaFt, prevEmaMj);
            }
            IsFirstAnalysisComplete = true;

            MinuteStat newStat = new MinuteStat
            {
                Timestamp = DateTime.Now,
                FocusScore = this.FocusScore,
                FatigueScore = this.FatigueScore,
                FatigueState = this.FatigueState
            };

            OnMinuteAnalyzed?.Invoke(newStat);
        }

        // ---------------------------------------------------------
        // 7. 생체 모방형 피로도 로직 (울트라디안 리듬 및 비선형 가중치 반영)
        // ---------------------------------------------------------
        private void UpdateFatigue(double zKpm, int apm)
        {
            _isRecovering = apm < 15;

            if (_isRecovering)
            {
                // 짧은 휴식으로도 뇌가 빠르게 회복되는 현실적 메커니즘
                double recoveryAmount = (2.0 + (FatigueScore * 0.1)) * FatigueTimeScale;
                FatigueScore = Math.Max(0, FatigueScore - recoveryAmount);

                // 휴식 시 연속 작업 시간 대폭 차감
                ContinuousWorkMinutes = Math.Max(0, ContinuousWorkMinutes - (int)(5 * FatigueTimeScale));
            }
            else // 작업 중 (가중 누적)
            {
                ContinuousWorkMinutes += (int)(1 * FatigueTimeScale);

                // 학술 근거: 인지 부하 이론을 반영한 비선형 스트레스 가중치 (제곱 사용)
                double stressWeight = Math.Pow((StressScore / 100.0), 2.0) * 4.0;

                // 학술 근거: 평소보다 타수가 비정상적으로 느려지면(체력 저하) 가중치 부여
                double slownessWeight = zKpm < -1.0 ? Math.Abs(zKpm) * 0.8 : 0;

                // 학술 근거: 여키스-도슨 법칙의 과각성(Over-arousal) 반영. 고도 몰입(Zone) 시 뇌 자원 극대화 소모.
                double focusWeight = (FocusState == 4) ? 2.5 : (FocusState == 3 ? 1.0 : 0);

                // 학술 근거 3: Nathaniel Kleitman의 울트라디안 리듬(Ultradian Rhythm) 및 Mackworth의 Time-on-Task 효과 반영
                // 90~120분 인지 한계 이론에 따라 연속 작업 돌파 시 생체 모방형 피로도 가속 페널티를 부여합니다.
                double durationMultiplier = 1.0;
                if (ContinuousWorkMinutes >= 120)
                {
                    // 120분 초과 시 울트라디안 리듬 한계 도달 (1.5배 이상 폭증)
                    durationMultiplier = 1.5 + ((ContinuousWorkMinutes - 120) / 60.0);
                }
                else if (ContinuousWorkMinutes >= 30)
                {
                    // 30분 초과 시 경계심 감소(Vigilance Decrement) 구간 (1.2배 가속)
                    durationMultiplier = 1.2;
                }

                double totalAccumulation = (1.0 + stressWeight + slownessWeight + focusWeight) * durationMultiplier;

                // 최종 누적량에 테스트용 배속 적용
                FatigueScore = Math.Min(100, FatigueScore + (totalAccumulation * FatigueTimeScale));
            }

            if (FatigueScore >= 71) FatigueState = 3;
            else if (FatigueScore >= 31) FatigueState = 2;
            else FatigueState = 1;
        }

        // ---------------------------------------------------------
        // 8. 5단계 몰입 상태 판별 로직 (Flow 이론 및 자아 고갈 이론 반영)
        // ---------------------------------------------------------
        // Z-Score 및 절대 기준을 종합하여 현재 사용자의 상태를 판별합니다.
        private void DetermineState(int apm, int csr, double zKpm, double zEr, double zMj, bool isRewriting)
        {
            // 1. 현재 60초 데이터만으로 본 순수 '목표 상태(Target State)' 판별
            int targetState = 2;
            string targetReason = "";

            if (apm < 15)
            {
                if (csr >= 6) { targetState = 1; targetReason = $"입력 저조 및 창 전환 {csr}회 발생으로 방황 중."; }
                else { targetState = 0; targetReason = "작업 흐름 정지 상태."; }
            }
            else if (csr >= 10 || zEr > 1.0 || zMj > 1.0)
            {
                targetState = 1;
                if (csr >= 10) targetReason = "잦은 창 전환으로 인한 산만함.";
                else if (zEr > 1.0) targetReason = "비정상적인 오타율 급증(과부하).";
                else targetReason = "거친 마우스 움직임 감지.";
            }
            // 학술 근거 2: 칙센트미하이의 Flow 이론 (상태 진입 관성 부여)
            // 몰입은 순간적으로 도달하는 것이 아니라 '유지되는 상태'임을 논리적으로 구현합니다.
            else if ((zKpm > 1.5 || apm >= 80) && zEr <= 0 && csr <= 2 && apm >= 50)
            {
                targetState = 4;
            }
            else if ((zKpm > 0.5 || apm >= 40) && csr <= 5 && apm >= 30)
            {
                targetState = 3;
                targetReason = isRewriting ? "문장 재작성(퇴고) 등 안정적이고 집중된 작업 흐름입니다." : "안정적이고 빠른 작업 페이스 유지 중.";
            }
            else
            {
                targetState = 2; // 페널티도 보너스도 없는 평소 베이스라인 일치 상태
                targetReason = "평소 패턴과 일치하는 안정적인 상태.";
            }

            int rawTargetState = targetState;
            targetState = GetSmoothedState(targetState);

            if (rawTargetState != targetState)
            {
                switch (targetState)
                {
                    case 0: targetReason = "최근 패턴 분석 결과, 작업 흐름 정지(대기)로 판별됩니다."; break;
                    case 1: targetReason = "최근 5분간의 흐름을 분석한 결과, 주의력 분산(산만함)이 유지되고 있습니다."; break;
                    case 2: targetReason = "이전의 안정적인 페이스와 작업 흐름 관성이 유지되고 있습니다."; break;
                    case 3: targetReason = "이전의 좋은 집중 상태 관성이 계속 유지되고 있습니다."; break;
                    case 4: targetReason = "깊은 몰입 상태(Zone)의 흐름이 끊기지 않고 지속 중입니다."; break;
                }
            }

            
                // 2. 관성(Hysteresis) 필터 적용: 상향 관성 & 하향 관성
                if (targetState >= 3)
                {
                    _distractionStreak = 0; // 집중을 되찾았으므로 하향 관성 초기화

                    if (targetState == 4)
                    {
                        _deepFocusStreak++; // 60초 분석 시마다 조건 달성 카운트 누적

                        if (_deepFocusStreak >= 3) // 3분(3회) 연속 조건을 달성해야 비로소 Deep Focus 로 인정
                        {
                            FocusState = 4; // 평소 대비 속도 극대화, 오타 0, 딴짓 없음 (Zone 상태)
                            StateReason = "완벽한 몰입(Flow) 상태 유지 중!";
                        }
                        else
                        {
                            FocusState = 3;
                            StateReason = $"고도의 집중 상태 진입 중... ({_deepFocusStreak}/3)";
                        }
                    }
                    else
                    {
                        _deepFocusStreak = 0;
                        FocusState = 3; // 긍정적인 가속 상태
                        StateReason = targetReason;
                    }
                }
                else
                {
                    // 이전에 몰입(FocusState 3 이상) 중이었다면, 유예 기간을 부여합니다.
                    if (FocusState >= 3)
                    {
                        _distractionStreak++;
                        if (_distractionStreak >= 2)
                        {
                            FocusState = targetState;
                            StateReason = targetReason;
                            _deepFocusStreak = 0; // 몰입 깨짐
                            _distractionStreak = 0;
                        }
                        else
                        {
                            FocusState = 3;
                            StateReason = $"⚠️ 집중력 하락 감지. 흐름을 잃지 않도록 주의하세요! (경고 {_distractionStreak}/2)";
                        }
                    }
                    else
                    {
                        FocusState = targetState;
                        StateReason = targetReason;
                        _deepFocusStreak = 0; // 방해 요소로 인해 몰입 깨짐, 혹은 작업 흐름 정지 상태
                        _distractionStreak = 0;
                    }
                }
            

            if (_previousState > FocusState && FocusState < 3)
            {
                StateReason = "⚠️ 작업 집중도가 하락하고 있습니다. 흐름을 다시 점검해 보세요.";
            }

            _previousState = FocusState;

            // 학술 근거 4: 바우마이스터의 자아 고갈(Ego Depletion) 이론 방어벽 (Hard Capping)
            // 인지적 피로도가 임계치를 넘었을 경우, 가짜 집중을 차단합니다.
            if (FatigueState == 3)
            {
                if (FocusState >= 3)
                {
                    // 물리적 타수가 아무리 빨라도 피로도가 위험 수준이면 인지적 상태를 2단계로 강제 격하
                    FocusState = 2;
                    _deepFocusStreak = 0;
                    _distractionStreak = 0;
                    StateReason = "극심한 피로로 인해 집중도가 강제 격하되었습니다.";
                }
                else
                {
                    StateReason = _isRecovering
                        ? "휴식 중입니다. 🌿"
                        : "[위험] 즉시 휴식이 필요합니다! 🚨";
                }
            }
            else if (FatigueState == 2)
            {
                if (_isRecovering)
                {
                    StateReason += " (피로 회복 중 🌿)";
                }
                else
                {
                    StateReason += " (피로 주의 ⚠️)";
                }
            }
            else if (FatigueState == 1)
            {
                if (_isRecovering && FatigueScore >= 5)
                {
                    StateReason = "잠깐의 휴식으로 에너지를 충전 중입니다. 🔋";
                }
            }
        }

        // ---------------------------------------------------------
        // 9. 개인화 데이터 학습 로직 (EMA 및 분산 업데이트)
        // ---------------------------------------------------------
        private void UpdateBaseline(int kpm, double er, double dt, double ft, int jerk, double pKpm, double pEr, double pDt, double pFt, double pMj)
        {
            // 방어 로직 (Outlier Capping) 
            // - 물리적 한계를 초과하는 노이즈(고양이 난입, 무거운 물체 눌림 등)가 
            //   개인화 모델을 오염시키는 것을 방지합니다.
            kpm = Math.Min(kpm, 600);   // 아무리 빨라도 분당 600타를 초과하는 데이터는 잘라냄
            jerk = Math.Min(jerk, 100); // 마우스 튐이 비정상적으로 높을 경우 제한

            // EMA(지수이동평균) 업데이트: 과거 데이터(90%) + 현재 데이터(10%)
            PersonalEmaKpm = (Alpha * kpm) + ((1 - Alpha) * pKpm);
            PersonalEmaEr = (Alpha * er) + ((1 - Alpha) * pEr);
            PersonalEmaDt = (Alpha * dt) + ((1 - Alpha) * pDt);
            PersonalEmaFt = (Alpha * ft) + ((1 - Alpha) * pFt);
            PersonalEmaMj = (Alpha * jerk) + ((1 - Alpha) * pMj);

            // Z-Score 산출용 분산(Variance) 업데이트
            PersonalVarKpm = (1 - Alpha) * (PersonalVarKpm + Alpha * Math.Pow(kpm - pKpm, 2));
            PersonalVarEr = (1 - Alpha) * (PersonalVarEr + Alpha * Math.Pow(er - pEr, 2));
            PersonalVarDt = (1 - Alpha) * (PersonalVarDt + Alpha * Math.Pow(dt - pDt, 2));
            PersonalVarFt = (1 - Alpha) * (PersonalVarFt + Alpha * Math.Pow(ft - pFt, 2));
            PersonalVarMj = (1 - Alpha) * (PersonalVarMj + Alpha * Math.Pow(jerk - pMj, 2));
        }

        private readonly Queue<int> _stateBuffer = new();

        // 5분치(5개) 데이터 중 최빈값을 뽑는 로직
        private int GetSmoothedState(int rawTargetState)
        {
            _stateBuffer.Enqueue(rawTargetState);
            if (_stateBuffer.Count > 5) _stateBuffer.Dequeue();

            // 빈도수 카운트 -> 개수가 같으면 마지막에 들어온 순서(최신)를 우선으로
            return _stateBuffer.GroupBy(s => s)
                               .OrderByDescending(g => g.Count())
                               .ThenByDescending(g => _stateBuffer.ToList().LastIndexOf(g.Key))
                               .First().Key;
        }
    }
}