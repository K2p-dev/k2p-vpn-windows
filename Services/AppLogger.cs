using System.Text;
using System.Text.RegularExpressions;

namespace K2pVpnWindows.Services;

public sealed record LogEntry(DateTime Timestamp, string Category, string Message)
{
    public string Formatted => $"[{Timestamp:HH:mm:ss.fff}] [{Category}] {Message}";
}

public static partial class AppLogger
{
    private const int MaxEntries = 1000;
    private static readonly object LockObj = new();
    private static readonly Queue<LogEntry> Buffer = new(MaxEntries);

    public static event Action<LogEntry>? LogAdded;

    [GeneratedRegex(@"(""(?:id|uuid|publicKey|public_key|auth|password|token)""\s*:\s*"")[^""]*("")", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex(@"(Bearer\s+)[A-Za-z0-9\-_\.]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    public static void Log(string category, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var sanitized = MaskSecrets(message.TrimEnd());
        var entry = new LogEntry(DateTime.Now, category.ToUpperInvariant(), sanitized);
        lock (LockObj)
        {
            if (Buffer.Count >= MaxEntries)
                Buffer.Dequeue();
            Buffer.Enqueue(entry);
        }
        LogAdded?.Invoke(entry);
    }

    public static IReadOnlyList<LogEntry> GetSnapshot()
    {
        lock (LockObj)
        {
            return Buffer.ToArray();
        }
    }

    public static void Clear()
    {
        lock (LockObj)
        {
            Buffer.Clear();
        }
    }

    public static string DumpText(string? categoryFilter = null)
    {
        var sb = new StringBuilder();
        foreach (var entry in GetSnapshot())
        {
            if (!string.IsNullOrEmpty(categoryFilter) &&
                !categoryFilter.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
                !entry.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            sb.AppendLine(entry.Formatted);
        }
        return sb.ToString();
    }

    public static string MaskSecrets(string content)
    {
        var masked = JsonSecretRegex().Replace(content, "$1***$2");
        return BearerTokenRegex().Replace(masked, "$1***");
    }
}
