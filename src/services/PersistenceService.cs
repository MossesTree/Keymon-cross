using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keymon.Platform;

namespace Keymon
{
    // 학습된 개인 기준선(EMA/분산)과 일별 통계를 JSON 한 파일로 보관합니다.
    //
    // 원본 keymon은 실행 파일 옆(BaseDirectory)에 userData.json을 저장했습니다.
    // macOS에서는 앱이 .app 번들 안에서 실행되고, 번들 내부는 코드 서명 대상이라
    // 쓰기가 막히거나 서명이 깨질 수 있습니다. 그래서 OS 표준 사용자 데이터 폴더로 옮겼습니다.
    //   Windows : %APPDATA%\Keymon\userData.json
    //   macOS   : ~/Library/Application Support/Keymon/userData.json
    // 기존 위치에 파일이 있으면 첫 실행 때 자동으로 옮겨 옵니다(데이터 유실 없음).
    public class PersistenceService
    {
        private static readonly string FilePath = ResolveDataPath();

        private static string ResolveDataPath()
        {
            string dir;

            if (OperatingSystem.IsMacOS())
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Keymon");
            }
            else
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Keymon");
            }

            try
            {
                Directory.CreateDirectory(dir);

                string target = Path.Combine(dir, "userData.json");

                // 원본 keymon 방식(실행 파일 옆)으로 저장된 데이터가 있으면 한 번만 옮겨 옵니다.
                string legacy = Path.Combine(AppContext.BaseDirectory, "userData.json");
                if (!File.Exists(target) && File.Exists(legacy))
                {
                    File.Copy(legacy, target);
                    Log.Info($"기존 데이터를 새 위치로 이전했습니다: {target}");
                }

                return target;
            }
            catch (Exception ex)
            {
                Log.Warn($"데이터 폴더를 준비하지 못해 실행 파일 옆에 저장합니다: {ex.Message}");
                return Path.Combine(AppContext.BaseDirectory, "userData.json");
            }
        }

        public void Load(AnalysisEngine engine, MetricCollector collector, MonitoringService service)
        {
            try
            {
                if (!File.Exists(FilePath)) return;

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var data = JsonSerializer.Deserialize<UserData>(File.ReadAllText(FilePath), options);
                if (data == null) return;

                collector.TotalKeyCount = data.KeyCount;
                collector.TotalMouseCount = data.MouseCount;
                collector.TotalBackspaceCount = data.BackspaceCount;
                collector.TotalAccumulatedKeys = data.TotalAccumulatedKeys;

                engine.PersonalEmaKpm = data.PersonalEmaKpm;
                engine.PersonalEmaEr = data.PersonalEmaEr;
                engine.PersonalVarKpm = data.PersonalVarKpm;
                engine.PersonalVarEr = data.PersonalVarEr;
                engine.PersonalEmaDt = data.PersonalEmaDt;
                engine.PersonalEmaFt = data.PersonalEmaFt;
                engine.PersonalVarDt = data.PersonalVarDt;
                engine.PersonalVarFt = data.PersonalVarFt;
                engine.PersonalEmaMj = data.PersonalEmaMj;
                engine.PersonalVarMj = data.PersonalVarMj;
                engine.TotalAccumulatedKeys = data.TotalAccumulatedKeys;

                engine.FatigueScore = data.FatigueScore;
                engine.ContinuousWorkMinutes = data.ContinuousWorkMinutes;

                // 앱이 꺼져 있던 동안(밤새, 며칠 등)에도 피로도가 자연 감소하도록,
                // 마지막 저장 시각과 지금 사이의 실제 경과 시간만큼 회복을 적용합니다.
                if (data.LastSavedAt.HasValue)
                {
                    engine.ApplyOfflineRecovery(DateTime.Now - data.LastSavedAt.Value);
                }

                engine.FocusScore = data.FocusScore;
                engine.FocusState = data.FocusState;
                if (!string.IsNullOrEmpty(data.StateReason)) engine.StateReason = data.StateReason;

                engine.IsFirstAnalysisComplete = data.IsFirstAnalysisComplete || data.HistoryScores.Count > 0;

                service.RestoreHistory(data.HistoryScores, data.HistoryStates, data.HistoryFatigue);

                if (data.DailyStats != null)
                {
                    foreach (var stat in data.DailyStats.Values)
                    {
                        stat.HourlyActiveMinutes ??= new int[24];
                        stat.HourlyFocusSum ??= new int[24];
                        stat.HourlyFatigueSum ??= new int[24];
                        stat.HourlyMinutes ??= new int[24];
                        stat.StateCounts ??= new int[5];
                    }
                    service.RestoreDailyStats(data.DailyStats);
                }

                if (data.OverlayLeft.HasValue && data.OverlayTop.HasValue)
                {
                    service.UpdateOverlayPosition(data.OverlayLeft.Value, data.OverlayTop.Value);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[데이터 로드 실패] {ex.Message}");
            }
        }

        public void Save(AnalysisEngine engine, MetricCollector collector, MonitoringService service)
        {
            try
            {
                var history = service.GetHistoryForSave();

                var data = new UserData
                {
                    KeyCount = collector.TotalKeyCount,
                    MouseCount = collector.TotalMouseCount,
                    BackspaceCount = collector.TotalBackspaceCount,
                    TotalAccumulatedKeys = collector.TotalAccumulatedKeys,
                    PersonalEmaKpm = engine.PersonalEmaKpm,
                    PersonalEmaEr = engine.PersonalEmaEr,
                    PersonalVarKpm = engine.PersonalVarKpm,
                    PersonalVarEr = engine.PersonalVarEr,
                    PersonalEmaDt = engine.PersonalEmaDt,
                    PersonalEmaFt = engine.PersonalEmaFt,
                    PersonalVarDt = engine.PersonalVarDt,
                    PersonalVarFt = engine.PersonalVarFt,
                    PersonalEmaMj = engine.PersonalEmaMj,
                    PersonalVarMj = engine.PersonalVarMj,
                    FatigueScore = engine.FatigueScore,
                    ContinuousWorkMinutes = engine.ContinuousWorkMinutes,
                    LastSavedAt = DateTime.Now,

                    FocusScore = engine.FocusScore,
                    FocusState = engine.FocusState,
                    StateReason = engine.StateReason,
                    IsFirstAnalysisComplete = engine.IsFirstAnalysisComplete,
                    HistoryScores = history.scores,
                    HistoryStates = history.states,
                    HistoryFatigue = history.fatigue,
                    DailyStats = service.DailyStats,

                    OverlayLeft = service.OverlayLeft,
                    OverlayTop = service.OverlayTop
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(data, options));
            }
            catch (Exception ex)
            {
                Log.Warn($"[데이터 저장 실패] {ex.Message}");
            }
        }

        private class UserData
        {
            public int KeyCount { get; set; }
            public int MouseCount { get; set; }
            public int BackspaceCount { get; set; }
            public int TotalAccumulatedKeys { get; set; }
            public double PersonalEmaKpm { get; set; }
            public double PersonalEmaEr { get; set; }
            public double PersonalVarKpm { get; set; }
            public double PersonalVarEr { get; set; }
            public double PersonalEmaDt { get; set; }
            public double PersonalEmaFt { get; set; }
            public double PersonalVarDt { get; set; }
            public double PersonalVarFt { get; set; }
            public double PersonalEmaMj { get; set; }
            public double PersonalVarMj { get; set; }
            public double FatigueScore { get; set; }
            public int ContinuousWorkMinutes { get; set; }
            public DateTime? LastSavedAt { get; set; }

            public int FocusScore { get; set; }
            public int FocusState { get; set; }
            public string StateReason { get; set; } = "";
            public bool IsFirstAnalysisComplete { get; set; }
            public List<int> HistoryScores { get; set; } = new();
            public List<int> HistoryStates { get; set; } = new();
            public List<int> HistoryFatigue { get; set; } = new();
            public Dictionary<string, DailyStat> DailyStats { get; set; } = new();

            public double? OverlayLeft { get; set; }
            public double? OverlayTop { get; set; }
        }
    }
}
