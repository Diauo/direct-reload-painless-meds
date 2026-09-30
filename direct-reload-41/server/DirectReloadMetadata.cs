using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace RZDirectReload.Server;

/// <summary>
/// Mod metadata read by SPT's mod loader (SPT 4.1 layout: IModMetadata).
/// </summary>
public record DirectReloadMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.rz99.directreload";
    public string Name { get; init; } = "Direct Reload & Painless Meds";
    public string Author { get; init; } = "RZ-99";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("1.2.0");
    public Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}
