namespace GmailTo;

/// <summary>
/// Holds the original mailto: link of the last automatic forward, so it can be
/// re-sent from a different profile if the rule turns out to be wrong.
///
/// Kept in its own file rather than inside config.json for one reason: it can
/// then be removed with a single delete that cannot half-succeed. It contains
/// the draft body, so deleting it promptly is the actual protection here.
/// Encrypting it would not help much: anything running as this user can read
/// the credential store just as easily, and a draft that exists for minutes is
/// a smaller exposure than one kept indefinitely.
/// </summary>
internal static class RetryStore
{
    /// <summary>Nothing should linger this long unexplained.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static string FilePath => Path.Combine(AppConfig.DirectoryPath, "last-forward.uri");

    public static void Save(string mailtoUri)
    {
        Directory.CreateDirectory(AppConfig.DirectoryPath);
        File.WriteAllText(FilePath, mailtoUri);
    }

    /// <summary>The stored link, or null if there is none or it cannot be read.</summary>
    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            string text = File.ReadAllText(FilePath).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            // An unreadable payload just means no retry is offered.
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // Best effort. The staleness sweep will catch it later.
        }
    }

    /// <summary>
    /// Drops a payload nobody ever came back for. Without this, a draft could
    /// sit on disk indefinitely if the explanation was never viewed and no
    /// later message superseded it.
    /// </summary>
    public static void SweepIfStale()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(FilePath) > MaxAge) File.Delete(FilePath);
        }
        catch
        {
            // Best effort.
        }
    }
}
