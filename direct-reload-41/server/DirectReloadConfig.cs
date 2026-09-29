using System.Text.Json.Serialization;

namespace RZDirectReload.Server;

/// <summary>
/// Runtime configuration for Direct Reload &amp; Painless Meds (server side).
/// Read from config.json inside the mod folder.
/// </summary>
public class DirectReloadConfig
{
    [JsonPropertyName("surgery")]
    public SurgerySettings Surgery { get; set; } = new();

    [JsonPropertyName("medUseTime")]
    public MedUseTimeSettings MedUseTime { get; set; } = new();

    [JsonPropertyName("drugBuffs")]
    public DrugBuffSettings DrugBuffs { get; set; } = new();
}

public class DrugBuffSettings
{
    /// <summary>
    /// 启用"药物正面效果持续时间"调整。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 持续时间倍率（2.0 = 翻倍）。
    /// 影响：
    ///  ① 所有医疗品的治疗效果持续时间（止痛 / 防辐射 / 防震荡等 effects_damage.duration）
    ///  ② 注射器 / 药膏的正面增益持续时间（生命回复 / 技能增幅 / 体力等白名单类型）
    /// </summary>
    [JsonPropertyName("multiplier")]
    public double Multiplier { get; set; } = 2.0;
}

public class SurgerySettings
{
    /// <summary>
    /// Enable the "no max-HP loss on surgical repair" feature.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Percent of max HP kept after surgery (100 = no loss at all, 25-45/60-72 = vanilla-ish).
    /// This maps directly onto the template field used by the game code:
    /// ActiveHealthController.RestoreBodyPart(limb, healthPenalty) where healthPenalty = keep ratio.
    /// </summary>
    [JsonPropertyName("keepMaxHealthPercent")]
    public double KeepMaxHealthPercent { get; set; } = 100;
}

public class MedUseTimeSettings
{
    /// <summary>
    /// Enable med item use-time scaling.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Use time multiplier applied to every medical item (0.5 = half time).
    /// </summary>
    [JsonPropertyName("multiplier")]
    public double Multiplier { get; set; } = 0.5;

    /// <summary>
    /// Floor for use time in seconds (guards against animation glitches).
    /// </summary>
    [JsonPropertyName("minSeconds")]
    public double MinSeconds { get; set; } = 0.8;

    /// <summary>
    /// Optional separate multiplier for surgical kits (null = use global multiplier).
    /// </summary>
    [JsonPropertyName("surgicalKitMultiplier")]
    public double? SurgicalKitMultiplier { get; set; } = null;
}
