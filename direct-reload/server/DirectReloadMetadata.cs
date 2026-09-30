using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace RZDirectReload.Server;

/// <summary>
/// Mod metadata read by SPT's mod loader.
/// Standard SPT 4.0.x layout: this record must live in the mod assembly.
/// </summary>
public record DirectReloadMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.rz99.directreload";
    public override string Name { get; init; } = "Direct Reload & Painless Meds";
    public override string Author { get; init; } = "RZ-99";
    public override List<string>? Contributors { get; init; } = [];
    public override Version Version { get; init; } = new("1.2.0", false);
    public override Range SptVersion { get; init; } = new("~4.0.13", false);
    public override List<string>? Incompatibilities { get; init; } = [];
    public override Dictionary<string, Range>? ModDependencies { get; init; } = [];
    public override string? Url { get; init; } = null;
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}
