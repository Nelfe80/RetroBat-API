namespace RetroBat.Domain.Models;

public class HiscoreEntry
{
    public string Rank { get; set; } = string.Empty;
    public string Score { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class HiscoreExtractionResult
{
    public string QueryId { get; set; } = string.Empty;
    public string QueryMd5 { get; set; } = string.Empty;
    public string RomName { get; set; } = string.Empty;
    public string RomPath { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string Game { get; set; } = string.Empty;
    public string Status { get; set; } = "not_found";
    public string Message { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public List<HiscoreEntry> Scores { get; set; } = new();
    /// <summary>Pseudo of the player currently at the cabinet (session/venue &gt; home
    /// account &gt; null when anonymous), so a leaderboard can point out "your best line".</summary>
    public string? Me { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
