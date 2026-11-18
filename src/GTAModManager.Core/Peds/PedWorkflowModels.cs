namespace GTAModManager.Core.Peds;

public sealed record PedInstallRequest(string ArchivePath, IReadOnlyList<PedSelection> Selections);

public sealed record PedInstallOutcome(
    string ArchivePath,
    bool Succeeded,
    string Message,
    string? TransactionId = null
);

public sealed record PedPackagePresentation(
    string Title,
    string SourceArchive,
    string Readme,
    PedReadmeDetails Details,
    IReadOnlyList<string> ImagePaths,
    string MediaArchivePath
);
