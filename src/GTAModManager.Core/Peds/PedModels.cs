namespace GTAModManager.Core.Peds;

public enum PedLayout
{
    Normal,
    Streamed,
}

public enum PedGender
{
    Male,
    Female,
}

public sealed record PedResource(string SourcePath, string TargetPath, long Size);

public sealed record PedCandidate(
    string Id,
    string SpawnName,
    string VariantName,
    PedLayout Layout,
    IReadOnlyList<PedResource> Resources,
    IReadOnlyList<string> Warnings
)
{
    public string? RegistrationXml { get; init; }
    public string? PreviewPath { get; init; }
    public bool LayoutAmbiguous { get; init; }
}

public sealed record PedArchiveAnalysis(
    string ArchivePath,
    IReadOnlyList<PedCandidate> Candidates,
    string Readme,
    IReadOnlyList<string> ImagePaths,
    IReadOnlyList<string> Warnings
)
{
    public PedReadmeDetails Details { get; init; } = PedReadmeDetails.Empty;
}

public sealed record PedAuthorLink(string Label, string Value, string? Url, bool Inferred = false);

public sealed record PedReadmeDetails(
    string Title,
    string Authors,
    string Credits,
    string Installation,
    IReadOnlyList<PedAuthorLink> Links
)
{
    public static PedReadmeDetails Empty { get; } = new("", "", "", "", []);
}

public sealed record PedSelection(string CandidateId, string DisplayName, PedGender Gender, PedLayout Layout);

public sealed class PedEntry
{
    public string PackageId { get; set; } = "";
    public string SpawnName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public PedLayout Layout { get; set; }
    public string PedType { get; set; } = "";
    public string RegistrationXml { get; set; } = "";
    public bool Managed { get; set; }
    public string SourceArchive { get; set; } = "";
    public string SourceDlc { get; set; } = "";
    public string MetadataSource { get; set; } = "";
    public List<string> ResourcePaths { get; set; } = [];
    public string? PreviewPath { get; set; }
}

public sealed class PedCatalog
{
    public int Version { get; set; } = 1;
    public List<PedEntry> Entries { get; set; } = [];
}

public sealed record PedDiscovery(IReadOnlyList<PedEntry> Entries, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
