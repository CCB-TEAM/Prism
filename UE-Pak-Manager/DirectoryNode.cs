namespace UE_Pak_Manager;

public sealed class DirectoryNode
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public List<DirectoryNode> Children { get; } = [];
}
