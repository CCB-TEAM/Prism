namespace UE_Pak_Manager;

public sealed class PackFilePreviewItem
{
    public required string Name { get; init; }

    public required string RelativePath { get; init; }

    public required string PakPath { get; init; }

    public required long Size { get; init; }

    public string SizeText => FormatSize(Size);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
