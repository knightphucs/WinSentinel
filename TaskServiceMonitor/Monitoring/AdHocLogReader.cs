using System.Diagnostics.Eventing.Reader;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Monitoring;

/// <param name="IsEnabled">
/// false = channel đang tắt (phần lớn channel trong "Applications and Services Logs"
/// mặc định như vậy) — chọn vào sẽ ra bảng rỗng. Bật bằng
/// <c>wevtutil sl "&lt;tên&gt;" /e:true</c>.
/// </param>
public sealed record LogChannelInfo(
    string Name,
    bool IsEnabled,
    long? RecordCount,
    bool IsClassic);

/// <summary>
/// Đọc log THEO YÊU CẦU (pull, <see cref="EventLogReader"/>), không phải realtime
/// subscribe như <see cref="EventLogWatcher"/>. Dùng cho "duyệt log bất kỳ" và mở
/// lại file <c>.evtx</c> đã lưu.
///
/// CỐ Ý tách khỏi pipeline chính: kết quả KHÔNG qua <see cref="EventQueue"/>, không
/// lưu DB, không bắn SignalR — gộp chung thì một channel như "Application" sẽ ngập
/// DB ngay lập tức.
/// </summary>
public sealed class AdHocLogReader(
    WindowsEventParser parser, RiskScorer riskScorer, ILogger<AdHocLogReader> logger)
{
    // Quet metadata vai tram channel ton ca giay, ma danh sach gan nhu khong doi
    // trong mot phien chay.
    private IReadOnlyList<LogChannelInfo>? _channelCache;
    private readonly object _channelCacheLock = new();

    public IReadOnlyList<LogChannelInfo> ListChannels(bool refresh = false)
    {
        lock (_channelCacheLock)
        {
            if (refresh || _channelCache is null)
            {
                _channelCache = EventLogSession.GlobalSession.GetLogNames()
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Select(DescribeChannel)
                    .ToList();
            }

            return _channelCache;
        }
    }

    private static LogChannelInfo DescribeChannel(string name)
    {
        var enabled = true;
        var classic = false;
        long? records = null;

        // Khong doc duoc cau hinh/metadata thi van liet ke ten, chi la khong biet
        // truoc trang thai. Khong log: hang tram channel.
        try
        {
            using var config = new EventLogConfiguration(name);
            enabled = config.IsEnabled;
            classic = config.IsClassicLog;
        }
        catch (EventLogException) { }
        catch (UnauthorizedAccessException) { }

        try
        {
            records = EventLogSession.GlobalSession
                .GetLogInformation(name, PathType.LogName)
                .RecordCount;
        }
        catch (EventLogException) { }
        catch (UnauthorizedAccessException) { }

        return new LogChannelInfo(name, enabled, records, classic);
    }

    /// <summary>
    /// Đọc tối đa <paramref name="count"/> event mới nhất. Chạy đồng bộ (Win32 API)
    /// nên bọc <see cref="Task.Run(Func{object?}, CancellationToken)"/> để không chặn
    /// luồng request của Kestrel.
    /// </summary>
    /// <param name="pathType">
    /// <see cref="PathType.LogName"/> = channel đang chạy; <see cref="PathType.FilePath"/>
    /// = file <c>.evtx</c> đã lưu. Phần đọc và parse giống hệt nhau.
    /// </param>
    public Task<IReadOnlyList<WindowsMonitorEvent>> ReadAsync(
        string path, int? eventId, int count, CancellationToken ct,
        PathType pathType = PathType.LogName,
        DateTime? fromUtc = null, DateTime? toUtc = null)
        => ReadByXPathAsync(
            path, BuildBrowseXPath(eventId, fromUtc, toUtc), count, ct, pathType);

    /// <summary>
    /// Dựng XPath cho "duyệt log": lọc theo Event ID và/hoặc khoảng thời gian.
    /// Hàm THUẦN, không đụng Windows — tách ra để test được trên mọi máy.
    /// </summary>
    /// <remarks>
    /// Vì sao phải lọc thời gian ở SERVER chứ không cắt trên mảng đã trả về: reader
    /// đọc <c>count</c> event MỚI NHẤT rồi dừng. Lọc phía client thì "24 giờ qua" thực
    /// chất là "trong 50 dòng mới nhất, dòng nào thuộc 24 giờ qua" — với channel bận
    /// thì 50 dòng mới nhất có khi chỉ trải trong vài phút, và những gì cần tìm (ví dụ
    /// event lúc app đang tắt) nằm ngoài tầm với, không cách nào chạm tới.
    ///
    /// Định dạng mốc thời gian trong XPath của Windows Event Log BẮT BUỘC là ISO-8601
    /// UTC có hậu tố <c>Z</c> (<c>"o"</c> của .NET với <see cref="DateTimeKind.Utc"/>).
    /// Truyền giờ local vào đây là lệch âm thầm, không có lỗi nào báo.
    /// </remarks>
    internal static string BuildBrowseXPath(int? eventId, DateTime? fromUtc, DateTime? toUtc)
    {
        var conditions = new List<string>();

        if (eventId is int id)
        {
            conditions.Add($"(EventID={id})");
        }

        var time = new List<string>();

        if (fromUtc is DateTime from)
        {
            time.Add($"@SystemTime>='{ToXPathStamp(from)}'");
        }

        if (toUtc is DateTime to)
        {
            time.Add($"@SystemTime<='{ToXPathStamp(to)}'");
        }

        if (time.Count > 0)
        {
            conditions.Add($"TimeCreated[{string.Join(" and ", time)}]");
        }

        return conditions.Count == 0
            ? "*"
            : $"*[System[{string.Join(" and ", conditions)}]]";
    }

    private static string ToXPathStamp(DateTime moment) =>
        DateTime.SpecifyKind(moment, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Đọc theo XPath tự dựng — dùng cho "lưu event đang chọn" (lọc theo
    /// <c>EventRecordID</c>, xem <see cref="SavedLogStore.BuildRecordIdXPath"/>).
    /// </summary>
    public Task<IReadOnlyList<WindowsMonitorEvent>> ReadByXPathAsync(
        string path, string xpath, int count, CancellationToken ct,
        PathType pathType = PathType.LogName)
        => Task.Run(() => Read(path, xpath, count, pathType, ct), ct);

    private IReadOnlyList<WindowsMonitorEvent> Read(
        string path, string xpath, int count, PathType pathType, CancellationToken ct)
    {
        var query = new EventLogQuery(path, pathType, xpath)
        {
            // Moi nhat truoc, giong Event Viewer va /api/events.
            ReverseDirection = true,

            // Duyet log la doc "moi thu" nen de dinh ban ghi hong hoac provider da go
            // cai dat; mac dinh EventLogReader dung han o loi dau tien.
            TolerateQueryErrors = true
        };

        using var reader = new EventLogReader(query);
        var result = new List<WindowsMonitorEvent>(count);

        while (result.Count < count)
        {
            // Task.Run(..., ct) chi huy duoc luc CHUA chay; khong co dong nay thi
            // client dong tab giua chung ma vong lap van chay het.
            ct.ThrowIfCancellationRequested();

            using var record = reader.ReadEvent();
            if (record is null)
            {
                break;
            }

            try
            {
                // Truyen ca 'record': Description va ten Task Category/Opcode chi lay
                // duoc khi record con song (xem EventRecordDescriber).
                var parsed = parser.Parse(record.ToXml(), record);

                // RiskScorer thuan rule-based, khong dung DB/state - goi o day chi de
                // HIEN THI, van tach hoan toan khoi pipeline curated.
                result.Add(parsed with { RiskLevel = riskScorer.Score(parsed) });
            }
            catch (Exception ex)
            {
                // Mot event loi khong duoc lam hong ca ket qua duyet.
                logger.LogWarning(ex, "Loi parse mot event khi duyet '{Path}'", path);
            }
        }

        return result;
    }
}
