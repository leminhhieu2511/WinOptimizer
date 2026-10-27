namespace WinOptimizer.Helpers;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(double bytes)
    {
        int unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:0} {Units[unit]}" : $"{bytes:0.#} {Units[unit]}";
    }

    public static string Duration(TimeSpan span) =>
        span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m"
            : $"{span.Hours}h {span.Minutes}m";
}
