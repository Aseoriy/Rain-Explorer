namespace RainExplorer.Models;

/// <summary>A recently accessed file or folder, persisted for the Home dashboard.</summary>
public sealed class RecentEntry
{
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public DateTime AccessedUtc { get; set; }
}
