namespace UE_Pak_Manager;

public sealed class PakFileItem
{
    public required string Path { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public string Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? string.Empty;
}
