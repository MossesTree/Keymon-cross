using System.Collections.Generic;
using System.Linq;

namespace Keymon
{
    public class DailyStat
    {
        public string DateString { get; set; } = "";
        public int TotalFocusSum { get; set; }
        public int TotalFatigueSum { get; set; }

        // 💡 분리됨: 전체 켜둔 시간 vs 실제 마우스/키보드를 건드린 시간
        public int TotalMinutes { get; set; }
        public int TotalActiveMinutes { get; set; }

        public int[] StateCounts { get; set; } = new int[5]; // 0:Idle, 1:Distracted, 2:Engaged, 3:Focused, 4:DeepFocus

        public int[] HourlyFocusSum { get; set; } = new int[24];
        public int[] HourlyFatigueSum { get; set; } = new int[24];
        public int[] HourlyMinutes { get; set; } = new int[24];
        public int[] HourlyActiveMinutes { get; set; } = new int[24];

        // 💡 핵심: 평균은 전체 시간이 아니라 '실제 작업한 시간'으로만 계산합니다!
        public int AvgFocus => TotalActiveMinutes > 0 ? TotalFocusSum / TotalActiveMinutes : 0;
        public int AvgFatigue => TotalActiveMinutes > 0 ? TotalFatigueSum / TotalActiveMinutes : 0;

        public int WorkMinutes => TotalActiveMinutes;
        public int IdleMinutes => StateCounts[0];

        public int TotalWorkMinutes => StateCounts[0] + StateCounts[1] + StateCounts[2] + StateCounts[3] + StateCounts[4];
    }
}