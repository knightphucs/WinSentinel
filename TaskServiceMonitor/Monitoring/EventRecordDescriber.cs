using System.Diagnostics.Eventing.Reader;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Lấy các field KHÔNG nằm trong XML: câu Description, tên Task Category / Opcode /
/// Keywords. Chúng là kết quả render message DLL của provider
/// (<c>EvtFormatMessage</c>) nên chỉ đọc được khi <see cref="EventRecord"/> còn sống
/// — tách riêng ra đây để <see cref="WindowsEventParser"/> giữ được tính thuần XML
/// (test bằng file mẫu).
/// </summary>
public static class EventRecordDescriber
{
    /// <summary>
    /// Không ghi đè giá trị parser đã lấy từ <c>&lt;RenderingInfo&gt;</c> — giá trị đó
    /// đến từ máy nguồn nên đáng tin hơn máy collector đang đoán lại.
    /// </summary>
    public static WindowsMonitorEvent Apply(WindowsMonitorEvent parsed, EventRecord record)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(record);

        return parsed with
        {
            Description = parsed.Description ?? Try(() => record.FormatDescription()),
            LevelDisplayName = parsed.LevelDisplayName ?? Try(() => record.LevelDisplayName),
            TaskCategoryName = parsed.TaskCategoryName
                               ?? Try(() => record.TaskDisplayName)
                               // Task = 0 nghia la khong phan loai; Event Viewer hien "None".
                               ?? (parsed.TaskCategoryId == 0 ? "None" : null),
            OpcodeName = parsed.OpcodeName ?? Try(() => record.OpcodeDisplayName),
            Keywords = ReadKeywords(record) ?? parsed.Keywords
        };
    }

    private static string? ReadKeywords(EventRecord record)
    {
        var names = Try(() => record.KeywordsDisplayNames?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray());

        return names is { Length: > 0 } ? string.Join(", ", names) : null;
    }

    /// <summary>
    /// Bọc TỪNG field riêng, không bọc chung cả cụm: provider thiếu message DLL là
    /// chuyện bình thường (phần mềm đã gỡ, hoặc event forward từ máy khác), bọc chung
    /// thì một field hỏng nuốt luôn ba field còn lại đọc được.
    /// </summary>
    private static T? Try<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (EventLogException)
        {
            // Khong log: mot channel nhu Application co hang tram event kieu nay.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
