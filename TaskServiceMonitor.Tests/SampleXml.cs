using System.Reflection;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Nạp mẫu XML thật đã nhúng vào assembly (thư mục Fixtures/).
/// </summary>
internal static class SampleXml
{
    private static readonly Assembly Assembly = typeof(SampleXml).Assembly;

    public static string Load(string fileName)
    {
        // Ten resource co dang "TaskServiceMonitor.Tests.Fixtures.<fileName>".
        var resourceName = Assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith("." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Khong tim thay fixture '{fileName}'. Cac resource dang co: " +
                string.Join(", ", Assembly.GetManifestResourceNames()));

        using var stream = Assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
