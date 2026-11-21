using Schale.Data;
using Schale.Data.GameModel;
using Schale.Excel;
using Schale.FlatData;
using BlueArchiveAPI.Services;

namespace Schale_Server.Services;

/// <summary>
/// Achievements that describe what the account HAS ("own 30 students", "a weapon at level 10", "cafe rank 4"...) rather than an
/// action. Instead of hooking every place that can change that state, they are recomputed from the database whenever the
/// mission screen syncs, so they fill in as soon as the condition holds.
/// Counters with no stored history (level-ups, skill-ups...) are derived from the current totals, which only grow.
/// </summary>
public class MissionStateEvaluator
{
    private readonly ExcelTableService _excel;

    public MissionStateEvaluator(ExcelTableService excel)
    {
        _excel = excel;
    }

    public void Sync(SchaleDataContext db, AccountDBServer account)
    {
        var id = account.ServerId;
        var chars = db.Characters.Where(x => x.AccountServerId == id).ToList();
        var weapons = db.Weapons.Where(x => x.AccountServerId == id).ToList();
        Dictionary<long, long>? items = null;
        HashSet<long>? scenarios = null, weekCleared = null, schoolCleared = null;
        List<AcademyLocationDBServer>? locations = null;
        List<CafeDBServer>? cafes = null;
        List<MultiFloorRaidDBServer>? multiFloor = null;

        static long P(IReadOnlyList<long> p, int i) => p.Count > i ? p[i] : 0;
        static int Skill(CharacterDBServer c, long min) =>
            new[] { c.ExSkillLevel, c.PublicSkillLevel, c.PassiveSkillLevel, c.ExtraPassiveSkillLevel }.Any(l => l >= min) ? 1 : 0;
        static int Potential(CharacterDBServer c, int key) => c.PotentialStats?.GetValueOrDefault(key) ?? 0;

        CafeDBServer? Cafe(int n) => (cafes ??= db.Cafes.Where(x => x.AccountServerId == id).ToList())
            .OrderBy(c => c.CafeId).Skip(n).FirstOrDefault();
        long Comfort(CafeDBServer? cafe)
        {
            if (cafe == null) return 0;
            var bonus = _excel.GetTable<FurnitureExcelT>().ToDictionary(f => f.Id, f => f.ComfortBonus);
            return db.Furnitures
                .Where(f => f.AccountServerId == id && f.CafeDBId == cafe.CafeDBId && f.Location != FurnitureLocation.Inventory)
                .AsEnumerable()
                .Sum(f => bonus.GetValueOrDefault(f.UniqueId));
        }
        List<MultiFloorRaidDBServer> MultiFloor() => multiFloor ??= db.MultiFloorRaids.Where(x => x.AccountServerId == id).ToList();
        List<AcademyLocationDBServer> Locations() => locations ??= db.AcademyLocations.Where(x => x.AccountServerId == id).ToList();

        Func<IReadOnlyList<long>, long>? Value(MissionCompleteConditionType type) => type switch
        {
            MissionCompleteConditionType.Reset_AccountLevelUp => _ => account.Level,
            MissionCompleteConditionType.Reset_GetCharacterCount => _ => chars.Count,
            MissionCompleteConditionType.Reset_SpecificStarCharacterCount => p => chars.Count(c => c.StarGrade >= P(p, 0)),
            MissionCompleteConditionType.Reset_CharacterAtSpecificSkillLevelCount => p => chars.Sum(c => Skill(c, P(p, 0))),
            MissionCompleteConditionType.Reset_GetFavorLevel => p => chars.Count(c => c.FavorRank >= P(p, 0)),
            MissionCompleteConditionType.Reset_PotentialMaxHPAtSpecificLevel => p => chars.Any(c => Potential(c, 1) >= P(p, 0)) ? 1 : 0,
            MissionCompleteConditionType.Reset_PotentialAttackPowerAtSpecificLevel => p => chars.Any(c => Potential(c, 2) >= P(p, 0)) ? 1 : 0,
            MissionCompleteConditionType.Reset_PotentialHealPowerAtSpecificLevel => p => chars.Any(c => Potential(c, 3) >= P(p, 0)) ? 1 : 0,
            MissionCompleteConditionType.Achieve_CharacterLevelUpCount => _ => chars.Sum(c => (long)Math.Max(0, c.Level - 1)),
            MissionCompleteConditionType.Achieve_CharacterSkillLevelUpCount => _ => chars.Sum(c => (long)Math.Max(0, c.ExSkillLevel + c.PublicSkillLevel + c.PassiveSkillLevel + c.ExtraPassiveSkillLevel - 4)),
            MissionCompleteConditionType.Achieve_CharacterPotentialUpCount => _ => chars.Sum(c => (long)Potential(c, 1) + Potential(c, 2) + Potential(c, 3)),
            MissionCompleteConditionType.Achieve_GetWeaponCount => _ => weapons.Count,
            MissionCompleteConditionType.Reset_WeaponAtSpecificLevelUpCount => p => weapons.Count(w => w.Level >= P(p, 0)),
            MissionCompleteConditionType.Reset_WeaponAtSpecificTranscendenceCount => p => weapons.Count(w => w.StarGrade >= P(p, 0)),
            MissionCompleteConditionType.Achieve_WeaponLevelUpCount => _ => weapons.Sum(w => (long)Math.Max(0, w.Level - 1)),
            MissionCompleteConditionType.Achieve_WeaponTranscendenceCount => _ => weapons.Sum(w => (long)Math.Max(0, w.StarGrade - 1)),
            MissionCompleteConditionType.Reset_GetMemoryLobbyCount => _ => db.MemoryLobbies.Count(x => x.AccountServerId == id),
            MissionCompleteConditionType.Reset_GetSpecificItemCount => p =>
                (items ??= db.Items.Where(x => x.AccountServerId == id).ToDictionary(x => x.UniqueId, x => x.StackCount)).GetValueOrDefault(P(p, 0)),
            MissionCompleteConditionType.Reset_ClearSpecificScenario => p =>
                (scenarios ??= db.ScenarioHistories.Where(x => x.AccountServerId == id).Select(x => x.ScenarioUniqueId).ToHashSet()).Contains(P(p, 0)) ? 1 : 0,
            MissionCompleteConditionType.Reset_ClearSpecificChaserDungeonCount or MissionCompleteConditionType.Reset_ClearSpecificFindGiftAndBloodDungeonCount => p =>
                (weekCleared ??= db.WeekDungeonStageHistories.Where(x => x.AccountServerId == id && x.IsCleardEver).Select(x => x.StageUniqueId).ToHashSet()).Contains(P(p, 0)) ? 1 : 0,
            MissionCompleteConditionType.Reset_ClearSpecificSchoolDungeonCount => p =>
                (schoolCleared ??= db.SchoolDungeonStageHistories.Where(x => x.AccountServerId == id && x.IsClearedEver).Select(x => x.StageUniqueId).ToHashSet()).Contains(P(p, 0)) ? 1 : 0,
            MissionCompleteConditionType.Reset_AcademyLocationAtSpecificRank => p => Locations().Count(l => l.Rank >= P(p, 0)),
            MissionCompleteConditionType.Reset_AcademyLocationRankSum => _ => Locations().Sum(l => l.Rank),
            MissionCompleteConditionType.Reset_GetCafeRank => _ => Cafe(0)?.CafeRank ?? 0,
            MissionCompleteConditionType.Reset_GetCafe2ndRank => _ => Cafe(1)?.CafeRank ?? 0,
            MissionCompleteConditionType.Achieve_GetComfortPoint => _ => Comfort(Cafe(0)),
            MissionCompleteConditionType.Achieve_GetComfort2ndPoint => _ => Comfort(Cafe(1)),
            MissionCompleteConditionType.Reset_JoinMultiFloorRaidCount => _ => MultiFloor().Count > 0 ? 1 : 0,
            // [boss, difficulty]: cleared at least up to that floor
            MissionCompleteConditionType.Reset_ClearMultiFloorRaidStage => p => MultiFloor().Any(m => m.ClearedDifficulty >= P(p, 1)) ? 1 : 0,
            _ => null,
        };

        var now = account.GameSettings.ServerDateTime();
        var progresses = db.MissionProgresses.Where(x => x.AccountServerId == id).ToList()
            .GroupBy(x => x.MissionUniqueId).ToDictionary(g => g.Key, g => g.First());

        foreach (var mission in _excel.GetTable<MissionExcelT>().Where(m => m.Category == MissionCategory.Achievement))
        {
            var eval = Value(mission.CompleteConditionType);
            if (eval == null) continue;
            progresses.TryGetValue(mission.Id, out var row);
            if (row is { Complete: true }) continue;

            IReadOnlyList<long> parameters = mission.CompleteConditionParameter ?? [];
            var value = eval(parameters);
            if (value <= 0) continue;
            var key = parameters.Count > 0 ? parameters[0] : 0L;

            if (row == null)
            {
                row = new MissionProgressDBServer
                {
                    AccountServerId = id,
                    MissionUniqueId = mission.Id,
                    StartTime = now,
                    ProgressParameters = new Dictionary<long, long>(),
                    Complete = false
                };
                db.MissionProgresses.Add(row);
                progresses[mission.Id] = row;
            }

            var current = row.ProgressParameters?.GetValueOrDefault(key) ?? 0;
            if (current >= value) continue;
            // replaced, not mutated, so the JSON-converted column is seen as changed
            row.ProgressParameters = new Dictionary<long, long>(row.ProgressParameters ?? new()) { [key] = value };
            row.Complete = value >= mission.CompleteConditionCount;
        }
    }
}
