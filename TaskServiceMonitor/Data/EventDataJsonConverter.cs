using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TaskServiceMonitor.Data;

/// <summary>
/// Chuyển <c>WindowsMonitorEvent.Data</c> (toàn bộ field thô trong &lt;EventData&gt;)
/// thành cột <c>jsonb</c> của PostgreSQL và ngược lại.
///
/// Tách riêng khỏi DbContext để test được mà không cần DB thật.
/// </summary>
public static class EventDataJsonConverter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false
    };

    // Nhan nullable vi ValueComparer cua EF truyen vao tham so co the null.
    public static string Serialize(IReadOnlyDictionary<string, string>? value)
        => JsonSerializer.Serialize(value ?? Empty, Options);

    public static IReadOnlyDictionary<string, string> Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(
                JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options)
                    ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);

    public static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> Converter =
        new(value => Serialize(value), json => Deserialize(json));

    /// <summary>
    /// BẮT BUỘC phải có. Dictionary là kiểu tham chiếu có thể thay đổi được; thiếu
    /// comparer thì EF không phát hiện được thay đổi bên trong và có thể so sánh sai.
    /// </summary>
    public static readonly ValueComparer<IReadOnlyDictionary<string, string>> Comparer =
        new(
            (left, right) => Serialize(left) == Serialize(right),
            value => Serialize(value).GetHashCode(StringComparison.Ordinal),
            value => Deserialize(Serialize(value)));

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();
}
