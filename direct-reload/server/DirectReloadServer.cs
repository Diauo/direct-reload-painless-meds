using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace RZDirectReload.Server;

/// <summary>
/// Direct Reload &amp; Painless Meds — server component.
///
/// Runs after the SPT database has loaded (PostDBModLoader) and edits the in-memory
/// item templates. Nothing on disk is touched; drop the mod folder into user/mods/
/// and it works on any SPT 4.0.x install.
///
/// Features:
///  1) Surgery no max-HP loss  -> sets effects_damage[DestroyedPart].healthPenaltyMin/Max
///     on surgical kits (the value used by ActiveHealthController.RestoreBodyPart).
///  2) Medical use-time scaling -> scales TemplateItem.Properties.MedUseTime.
///  3) Drug buff duration scaling -> effects_damage durations + stimulator positive buffs.
/// </summary>
[Injectable(InjectionType.Scoped, null, int.MaxValue, TypePriority = OnLoadOrder.PostDBModLoader + 10)]
public class DirectReloadServer : IOnLoad
{
    private readonly ISptLogger<DirectReloadServer> _logger;
    private readonly ModHelper _modHelper;
    private readonly DatabaseService _databaseService;

    /// <summary>
    /// Surgical kits that restore blacked-out limbs (CMS / Surv12 / Sanitar kit).
    /// </summary>
    private static readonly MongoId[] SurgicalKitIds =
    [
        new("5d02778e86f774203e7dedbe"), // CMS surgical kit
        new("5d02797c86f774203f38e30a"), // Surv12 field surgical kit
        new("5e99735686f7744bfc4af32c"), // Sanitar's surgical kit
    ];

    public DirectReloadServer(
        ISptLogger<DirectReloadServer> logger,
        ModHelper modHelper,
        DatabaseService databaseService)
    {
        _logger = logger;
        _modHelper = modHelper;
        _databaseService = databaseService;
    }

    public Task OnLoad()
    {
        try
        {
            Execute();
        }
        catch (Exception ex)
        {
            _logger.Error($"[DirectReload] Fatal: {ex}");
        }

        return Task.CompletedTask;
    }

    private void Execute()
    {
        var modPath = _modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = _modHelper.GetJsonDataFromFile<DirectReloadConfig>(modPath, "config.json")
                     ?? new DirectReloadConfig();

        var items = _databaseService.GetItems();

        if (config.Surgery.Enabled)
        {
            ApplySurgeryNoPenalty(items, config.Surgery);
        }
        else
        {
            _logger.Info("[DirectReload] Surgery feature disabled by config, skipping");
        }

        if (config.MedUseTime.Enabled)
        {
            ApplyMedUseTime(items, config.MedUseTime);
        }
        else
        {
            _logger.Info("[DirectReload] MedUseTime feature disabled by config, skipping");
        }

        if (config.DrugBuffs.Enabled)
        {
            ApplyDrugBuffDurations(config.DrugBuffs);
        }
        else
        {
            _logger.Info("[DirectReload] DrugBuffs feature disabled by config, skipping");
        }
    }

    private void ApplySurgeryNoPenalty(Dictionary<MongoId, TemplateItem> items, SurgerySettings cfg)
    {
        var touched = 0;

        foreach (var kitId in SurgicalKitIds)
        {
            if (!items.TryGetValue(kitId, out var item))
            {
                _logger.Warning($"[DirectReload] Surgical kit template not found: {kitId}");
                continue;
            }

            var effectsDamage = item.Properties?.EffectsDamage;
            if (effectsDamage is null)
            {
                _logger.Warning($"[DirectReload] {item.Name} has no effects_damage, skipping");
                continue;
            }

            if (!effectsDamage.TryGetValue(DamageEffectType.DestroyedPart, out var destroyedPart) || destroyedPart is null)
            {
                // Defensive: create the entry if a kit ever ships without it.
                destroyedPart = new EffectsDamageProperties();
                effectsDamage[DamageEffectType.DestroyedPart] = destroyedPart;
            }

            destroyedPart.HealthPenaltyMin = cfg.KeepMaxHealthPercent;
            destroyedPart.HealthPenaltyMax = cfg.KeepMaxHealthPercent;
            touched++;

            _logger.Info($"[DirectReload] {item.Name}: surgery keep-ratio set to {cfg.KeepMaxHealthPercent}%");
        }

        _logger.Info($"[DirectReload] Surgery no-penalty applied to {touched}/{SurgicalKitIds.Length} surgical kits");
    }

    private void ApplyMedUseTime(Dictionary<MongoId, TemplateItem> items, MedUseTimeSettings cfg)
    {
        var count = 0;

        foreach (var item in items.Values)
        {
            var props = item.Properties;
            if (props?.MedUseTime is not double useTime || useTime <= 0)
            {
                continue;
            }

            var multiplier = cfg.Multiplier;
            if (cfg.SurgicalKitMultiplier is double surgicalMultiplier && IsSurgicalKit(item.Id))
            {
                multiplier = surgicalMultiplier;
            }

            var newTime = Math.Max(cfg.MinSeconds, Math.Round(useTime * multiplier, 2));
            props.MedUseTime = newTime;
            count++;
        }

        _logger.Info($"[DirectReload] MedUseTime scaled on {count} items (x{cfg.Multiplier}, floor {cfg.MinSeconds}s)");
    }

    /// <summary>正面 buff 类型白名单（持续时间翻倍的目标）——不含副作用类型</summary>
    private static readonly HashSet<string> PositiveBuffTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HealthRate",   // 生命回复
        "SkillRate",    // 技能增幅
        "MaxStamina",   // 体力上限
        "StaminaRate",  // 体力恢复
        "WeightLimit",  // 负重上限
    };

    /// <summary>
    /// 药物正面效果持续时间调整：
    ///  ① 所有医疗品的"治疗效果持续时间"（effects_damage.duration：止痛 / 防辐射 / 防震荡等）
    ///  ② 注射器 / 药膏的"正面增益持续时间"（白名单 buff 类型：生命回复 / 技能 / 体力等）
    /// </summary>
    private void ApplyDrugBuffDurations(DrugBuffSettings cfg)
    {
        var multiplier = cfg.Multiplier;
        if (multiplier <= 0d || Math.Abs(multiplier - 1d) < 0.001d)
        {
            _logger.Info($"[DirectReload] DrugBuffs multiplier is {multiplier}, nothing to do");
            return;
        }

        // ① 治疗效果持续（effects_damage）——所有医疗品
        var items = _databaseService.GetItems();
        var effectCount = 0;
        foreach (var item in items.Values)
        {
            var effectsDamage = item.Properties?.EffectsDamage;
            if (effectsDamage is null)
            {
                continue;
            }

            foreach (var pair in effectsDamage)
            {
                var effect = pair.Value;
                if (effect?.Duration is double duration && duration > 0d)
                {
                    effect.Duration = Math.Round(duration * multiplier, 2);
                    effectCount++;
                }
            }
        }

        _logger.Info($"[DirectReload] Drug effect durations scaled: {effectCount} entries (x{multiplier})");

        // ② 注射器 / 药膏的正面 buff 持续（globals.Stimulator.Buffs）
        var globals = _databaseService.GetGlobals();
        var stimulator = globals?.Configuration?.Health?.Effects?.Stimulator;
        if (stimulator?.Buffs is null)
        {
            _logger.Warning("[DirectReload] Stimulator buffs not found in globals, skipping");
            return;
        }

        var buffCount = 0;
        foreach (var group in stimulator.Buffs.Values)
        {
            if (group is null)
            {
                continue;
            }

            foreach (var buff in group)
            {
                if (buff is null || buff.Duration <= 0d)
                {
                    continue;
                }

                if (!PositiveBuffTypes.Contains(buff.BuffType))
                {
                    continue; // 副作用（手抖 / 量子隧穿等）不调整
                }

                buff.Duration = Math.Round(buff.Duration * multiplier, 2);
                buffCount++;
            }
        }

        _logger.Info($"[DirectReload] Stimulator positive-buff durations scaled: {buffCount} entries (x{multiplier})");
    }

    private static bool IsSurgicalKit(MongoId id)
    {
        foreach (var kitId in SurgicalKitIds)
        {
            if (kitId.Equals(id))
            {
                return true;
            }
        }

        return false;
    }
}
