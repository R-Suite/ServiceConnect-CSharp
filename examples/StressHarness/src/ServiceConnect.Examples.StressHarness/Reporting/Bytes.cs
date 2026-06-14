using System.Globalization;

namespace ServiceConnect.Examples.StressHarness.Reporting;

public static class Bytes
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024L * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_048_576.0:F1} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_073_741_824.0:F2} GB"),
    };
}
