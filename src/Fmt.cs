namespace SteamLZXTray;

internal static class Fmt
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>Human-readable byte count, e.g. "288.76 GB".</summary>
    public static string Bytes(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {Units[unit]}";
    }
}
