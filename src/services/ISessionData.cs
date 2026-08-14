using System.Collections.Generic;

namespace Keymon
{
    // interface(인터페이스)는 클래스가 반드시 구현해야 하는 '계약서'입니다.
    // 구체적인 구현 코드는 없고, "이런 속성/메서드를 제공해야 한다"는 약속만 정의합니다.
    //
    // 사용 목적: DashboardWindow가 App 클래스를 직접 참조하는 대신,
    // 이 인터페이스만 알면 됩니다. 이 덕분에:
    //   - UI 개발자는 App 내부 구조를 몰라도 됩니다.
    //   - MonitoringService가 이 인터페이스를 구현하여 데이터를 제공합니다.
    //   - 나중에 데이터 제공 방식이 바뀌어도 DashboardWindow 코드는 수정 불필요.
    //
    // { get; } 은 읽기 전용 속성을 의미합니다. UI는 데이터를 읽기만 하고, 직접 수정은 불가능합니다.
    public interface ISessionData
    {
        // 첫 60초 분석이 완료되었는지 여부 (false면 아직 학습 중)
        bool IsFirstAnalysisComplete { get; }

        // 다음 심층 분석까지 남은 초 (60 - 현재 틱 카운터)
        int RemainingSeconds { get; }

        // AnalysisEngine이 계산한 집중도 점수 (0~100)
        int FocusScore { get; }

        // AnalysisEngine이 계산한 스트레스 점수 (0~100)
        int StressScore { get; }

        // AnalysisEngine이 계산한 누적 피로도 (0~100)
        double FatigueScore { get; }

        // 최근 60초 키 입력 수
        int CurrentKpm { get; }

        // 최근 60초 마우스 클릭 수
        int CurrentMpm { get; }

        // 최근 60초 총 행동 수 (KPM + MPM)
        int CurrentApm { get; }

        // 최근 60초 백스페이스 횟수
        int BackspaceCount { get; }

        // 최근 60초 마우스 급격한 꺾임 횟수
        int JerkCount { get; }

        // 최근 60초 창 전환 횟수
        int ContextSwitchCount { get; }

        // 현재 집중 상태 (0=Idle, 1=Distracted, 2=Engaged, 3=Focused, 4=Deep Focus)
        int FocusState { get; }

        //현재 피로도 경고 상태(1=안전, 2=주의, 3=위험) - UI 컬러 및 경고창 제어용
        int FatigueState { get; }

        // 현재 상태의 판단 근거 텍스트
        string StateReason { get; }

        // 최근 10분의 집중도 점수 기록 (차트 표시용)
        // List<int>는 int 값들의 동적 배열입니다. new List<int>()로 복사본을 반환해 원본 보호.
        List<int> HistoryScores { get; }

        // MonitoringService에서 기록한 _historyFatigue를 전달받기 위해 필요합니다.
        List<int> HistoryFatigue { get; }

        bool IsStandby { get; }

        bool IsManualStandby { get; }

        bool IsFastMode { get; }

        Dictionary<string, DailyStat> DailyStats { get; }

        double? OverlayLeft { get; }

        double? OverlayTop { get; }

        void UpdateOverlayPosition(double left, double top);
    }
}