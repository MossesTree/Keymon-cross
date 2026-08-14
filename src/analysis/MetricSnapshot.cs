namespace Keymon
{
    // record는 C#의 특별한 클래스 형태로, 데이터를 담는 용도에 최적화되어 있습니다.
    // 일반 class와 달리, record는 생성 후 값을 변경할 수 없는 '불변(immutable)' 객체입니다.
    // 즉, 한 번 만들어진 스냅샷은 절대 수정되지 않아 데이터 흐름이 안전합니다.
    //
    // 사용 목적: MetricCollector가 매 틱(1초)마다 현재 입력 지표들을 이 객체에 담아
    // MonitoringService에 전달합니다. MonitoringService는 이 스냅샷을 보고
    // AnalysisEngine을 호출하거나 UI를 갱신합니다.
    //
    // 이 구조 덕분에 여러 서비스가 동일한 데이터를 참조해도 서로 간섭하지 않습니다.
    public record MetricSnapshot(
        int Kpm,               // 최근 60초 동안의 키 입력 횟수 (Keys Per Minute)
        int Mpm,               // 최근 60초 동안의 마우스 클릭 횟수 (Mouse clicks Per Minute)
        int BackspaceCount,    // 최근 60초 동안의 백스페이스(오타 수정) 횟수
        int JerkCount,         // 최근 60초 동안 감지된 급격한 마우스 방향 전환 횟수
        int ContextSwitchCount,// 최근 60초 동안의 창 전환 횟수
        double AvgDwellTime,   // 이번 분기 평균 키 누름 지속 시간 (ms). 데이터 없으면 0
        double AvgFlightTime,   // 이번 분기 평균 키 간격 시간 (ms). 데이터 없으면 0
        int MaxConsecutiveBackspaces,
        int ScrollCount
    );
}
