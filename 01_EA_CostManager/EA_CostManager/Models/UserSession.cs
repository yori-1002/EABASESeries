// ============================================================
// Models/UserSession.cs  ★新規（Sprint 2）
// ============================================================
namespace EA_CostManager.Models;

public class UserSession
{
    public int     id            { get; set; }
    public string  mac_address   { get; set; } = string.Empty;
    public string  user_name     { get; set; } = string.Empty;
    public string  software_name { get; set; } = string.Empty;
    public string  started_at    { get; set; } = string.Empty;
    public string  last_alive_at { get; set; } = string.Empty;
    public string? ended_at      { get; set; }
    public string  status        { get; set; } = string.Empty;

    // ─── 表示用ヘルパー ───

    /// <summary>起動経過時間（「3分前に起動」など）</summary>
    public string elapsed_label
    {
        get
        {
            if (!DateTime.TryParse(started_at, out var dt)) return "";
            var span = DateTime.Now - dt;
            if (span.TotalMinutes < 1)  return "たった今";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}分前に起動";
            if (span.TotalHours < 24)   return $"{(int)span.TotalHours}時間前に起動";
            return $"{(int)span.TotalDays}日前に起動";
        }
    }

    /// <summary>自分自身のセッションか</summary>
    public bool is_mine { get; set; }
}
