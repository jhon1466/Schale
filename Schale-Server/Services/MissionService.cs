using AutoMapper;
using BlueArchiveAPI.Services;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Data.ModelMapping;
using Schale.Excel;
using Schale.FlatData;
using Schale.MX.GameLogic.DBModel;

namespace Schale_Server.Services;

public class MissionService
{
    private readonly ExcelTableService _excelService;
    private readonly IMapper _mapper;

    public MissionService(ExcelTableService excelService, IMapper mapper)
    {
        _excelService = excelService;
        _mapper = mapper;
    }

    public List<MissionProgressDB> UpdateMissionProgress(
        SchaleDataContext context,
        AccountDBServer account,
        MissionCompleteConditionType conditionType,
        long amount = 1,
        long? parameter = null)
    {
        var missionExcels = _excelService.GetTable<MissionExcelT>();
        var battlePassMissionExcels = _excelService.GetTable<BattlePassMissionExcelT>();
        var updatedMissions = new List<MissionProgressDB>();

        var relevantMissions = missionExcels
            .Where(m => m.CompleteConditionType == conditionType)
            .Where(m => m.Category == MissionCategory.Daily ||
                       m.Category == MissionCategory.Weekly ||
                       m.Category == MissionCategory.Achievement ||
                       // Challenge is the per-stage "clear it in N turns" mission. Official sends these in MissionProgressDBs like any other (30150 on the campaign clear), and Mission_List serves them from the same storage. Every Reset_CompleteCampaignStageMinimumTurn row is Challenge.
                       m.Category == MissionCategory.Challenge)
            .Select(m => new { m.Id, m.CompleteConditionParameter, m.CompleteConditionCount, IsBattlePass = false, m.ResetType })
            .ToList();

        // BattlePass missions share the MissionProgresses storage (BattlePass_MissionList serves them from it), but they must never reach the returned list: handlers attach it to client responses as MissionProgressDBs,
        // and the client's mission screen cannot resolve a BattlePassMissionExcel id (2000001+) against MissionExcel - unresolvable ids break the screen.
        // Progress is still persisted for both kinds below.
        var relevantBpMissions = battlePassMissionExcels
            .Where(m => m.CompleteConditionType == conditionType)
            .Select(m => new { m.Id, m.CompleteConditionParameter, m.CompleteConditionCount, IsBattlePass = true, ResetType = MissionResetType.None })
            .ToList();

        relevantMissions.AddRange(relevantBpMissions);

        var lowerIsBetter = IsLowerBetter(conditionType);
        var now = account.GameSettings.ServerDateTime();

        foreach (var mission in relevantMissions)
        {
            // A mission that names its subject only moves for that subject.
            // Letting a caller with no parameter tick every parameterised row of the condition type means one campaign clear satisfies all 259 "clear stage X" missions at once.
            var declared = mission.CompleteConditionParameter;
            var hasDeclared = declared is { Count: > 0 };
            if (hasDeclared && (!parameter.HasValue || !declared!.Contains(parameter.Value)))
                continue;

            // Official keys ProgressParameters by the parameter the mission matched on, not by 0: its campaign clear reports {"1161101": 1} for the stage-specific mission and {"0": 16} for the ones with no parameter.
            // The client reads the count out by that key.
            var key = hasDeclared ? parameter!.Value : 0L;

            var existingMission = context.MissionProgresses
                .FirstOrDefault(m => m.AccountServerId == account.ServerId &&
                                   m.MissionUniqueId == mission.Id);

            // Daily/weekly rows are reused across days: a row started before the current window is stale progress, start it over
            // (otherwise a task done yesterday stays "complete" and never counts again).
            if (existingMission != null && IsStale(mission.ResetType, existingMission.StartTime, now))
            {
                existingMission.StartTime = now;
                existingMission.ProgressParameters = new Dictionary<long, long>();
                existingMission.Complete = false;
            }

            if (existingMission != null && existingMission.Complete)
                continue;

            if (existingMission == null)
            {
                existingMission = new MissionProgressDBServer
                {
                    AccountServerId = account.ServerId,
                    MissionUniqueId = mission.Id,
                    StartTime = account.GameSettings.ServerDateTime(),
                    ProgressParameters = new Dictionary<long, long>(),
                    Complete = false
                };
                context.MissionProgresses.Add(existingMission);
            }

            existingMission.ProgressParameters ??= new Dictionary<long, long>();
            var current = existingMission.ProgressParameters.GetValueOrDefault(key);

            if (lowerIsBetter)
            {
                // Here `amount` is a result to beat, not a tally: the turn count the stage was cleared in. Keeping the best run is what makes the mission completable at all - accumulating would walk the number away from the target on every replay.
                existingMission.ProgressParameters[key] = current == 0 ? amount : Math.Min(current, amount);
                existingMission.Complete = existingMission.ProgressParameters[key] <= mission.CompleteConditionCount;
            }
            else
            {
                existingMission.ProgressParameters[key] = current + amount;
                existingMission.Complete = existingMission.ProgressParameters[key] >= mission.CompleteConditionCount;
            }

            if (!mission.IsBattlePass)
                updatedMissions.Add(existingMission.ToMap(_mapper));
        }

        return updatedMissions;
    }

    // Same windows as MissionHandler.CurrentWindowClaims: the game day rolls at 04:00 and weeks start on Monday.
    internal static (DateTime Day, DateTime Week) WindowStarts(DateTime now)
    {
        var day = (now.TimeOfDay < TimeSpan.FromHours(4) ? now.AddDays(-1) : now).Date.AddHours(4);
        return (day, day.AddDays(-(((int)day.DayOfWeek + 6) % 7)));
    }

    private static bool IsStale(MissionResetType resetType, DateTime startTime, DateTime now)
    {
        var (day, week) = WindowStarts(now);
        return resetType switch
        {
            MissionResetType.Daily => startTime < day,
            MissionResetType.Weekly => startTime < week,
            _ => false,
        };
    }

    /// <summary>Login-driven tasks: daily login, logins-per-week (once per game day) and "log in between HHMM and HHMM".</summary>
    public List<MissionProgressDB> TickLogin(SchaleDataContext context, AccountDBServer account)
    {
        var now = account.GameSettings.ServerDateTime();
        var (day, _) = WindowStarts(now);
        var dailyLoginIds = _excelService.GetTable<MissionExcelT>()
            .Where(m => m.CompleteConditionType == MissionCompleteConditionType.Reset_DailyLogin)
            .Select(m => m.Id).ToHashSet();
        var alreadyToday = context.MissionProgresses.Any(m => m.AccountServerId == account.ServerId
            && dailyLoginIds.Contains(m.MissionUniqueId) && m.Complete && m.StartTime >= day);

        var result = UpdateMissionProgress(context, account, MissionCompleteConditionType.Reset_DailyLogin, 1);
        if (!alreadyToday)
            result.AddRange(UpdateMissionProgress(context, account, MissionCompleteConditionType.Reset_DailyLoginCount, 1));

        // Parameters are a [from, to] window as HHMM on a 04:00-based day (2800 = 04:00 next morning).
        var hhmm = now.Hour * 100 + now.Minute;
        if (hhmm < 400) hhmm += 2400;
        foreach (var m in _excelService.GetTable<MissionExcelT>().Where(m => m.CompleteConditionType == MissionCompleteConditionType.Reset_LoginAtSpecificTime))
        {
            var p = m.CompleteConditionParameter;
            if (p is { Count: >= 2 } && hhmm >= p[0] && hhmm < p[1])
                result.AddRange(UpdateMissionProgress(context, account, MissionCompleteConditionType.Reset_LoginAtSpecificTime, 1, p[0]));
        }
        return result;
    }

    // Conditions whose progress is a personal best rather than a running total - the target is a ceiling to get under, so a smaller number is further along.
    // Official's capture shows the per-stage turn mission sitting at {"1161101": 5} against a count of 4 and NOT complete.
    private static bool IsLowerBetter(MissionCompleteConditionType conditionType)
        => conditionType is MissionCompleteConditionType.Reset_CompleteCampaignStageMinimumTurn
            or MissionCompleteConditionType.Reset_EventCompleteCampaignStageMinimumTurn;
}
