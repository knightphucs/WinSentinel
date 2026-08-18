using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Trạng thái thật của một channel — <see cref="EventWatcherService"/> ghi vào, API đọc ra.
///
/// <see cref="ResumeFromRecordId"/>/<see cref="CaughtUpCount"/> là phần hiện trực quan cho
/// cơ chế "resume sau restart" bằng EventRecordID cursor (xem CLAUDE.md, mục "Bước 7") -
/// trước đây chỉ thấy được qua console log/DB, giờ lên thẳng panel "Log Summary".
/// </summary>
public sealed record ChannelStatus(
    string Channel,
    bool Subscribed,
    string? Error,
    int EventsReceived,
    DateTime? LastEventUtc,
    long? ResumeFromRecordId,
    int CaughtUpCount)
{
    /// <summary>
    /// RecordId của event MỚI NHẤT nhận được trong phiên chạy này. Khác hẳn
    /// <see cref="ResumeFromRecordId"/> — cái đó là cursor đo lúc khởi động rồi đóng
    /// băng, không bao giờ đổi. Tách hai cột vì cột cũ đặt tên "RecordId cuối" mà
    /// hiển thị cursor, gây hiểu nhầm là "bản ghi cuối cùng đã đọc".
    /// </summary>
    public long? LastRecordId { get; init; }


    /// <summary>
    /// RecordId mới nhất ĐÃ CÓ SẴN trong log lúc app vừa khởi động (snapshot qua
    /// <c>EventLogSession.GetLogInformation</c>) - dùng làm "mốc kết thúc catch-up"
    /// để <see cref="ChannelStatusRegistry.MarkEventReceived"/> biết event nhận được
    /// là đang đọc bù lịch sử hay đã là realtime thật. KHÔNG lộ ra API - chỉ để
    /// registry tự so sánh, nên đánh dấu <see cref="JsonIgnoreAttribute"/>.
    /// </summary>
    [JsonIgnore]
    public long? CatchUpTargetRecordId { get; init; }
}

/// <summary>
/// Ghi lại trạng thái subscribe/đọc THẬT của từng channel, để panel "Log Summary"
/// báo đúng chứ không đoán. Lý do phải tách riêng field <see cref="ChannelStatus.EventsReceived"/>
/// khỏi việc "subscribe thành công": lỗi thiếu quyền đọc KHÔNG throw lúc subscribe,
/// mà nổi lên bất đồng bộ qua <c>EventRecordWrittenEventArgs.EventException</c> mỗi
/// khi có event mới — "Subscribed = true" một mình không đủ để biết channel có THỰC
/// SỰ đang nhận được event hay không.
/// </summary>
public sealed class ChannelStatusRegistry
{
    private readonly ConcurrentDictionary<string, ChannelStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mốc bắt đầu phiên chạy hiện tại. Registry là singleton nên khởi tạo cùng lúc
    /// với app — dùng làm mốc "app bật lại lúc nào" cho trang Khôi phục, thay vì
    /// <c>Process.StartTime</c> (giờ local, và lệch khi chạy qua <c>dotnet run</c>).
    /// </summary>
    public DateTime SessionStartedUtc { get; } = DateTime.UtcNow;

    /// <summary>
    /// <paramref name="resumeFromRecordId"/>: cursor dùng để subscribe lần này (null = lần
    /// đầu, không có lịch sử để resume). <paramref name="catchUpTargetRecordId"/>: snapshot
    /// "RecordId mới nhất hiện có trong log" đo lúc subscribe - mốc để đếm
    /// <see cref="ChannelStatus.CaughtUpCount"/>, chỉ có ý nghĩa khi có cursor.
    /// </summary>
    /// <remarks>
    /// PHẢI là AddOrUpdate giữ nguyên số đếm, KHÔNG được ghi đè bằng
    /// <c>_statuses[channel] = new(...)</c> như trước. Lý do: khi có cursor thì
    /// <c>readExistingEvents = true</c>, nên Windows bắt đầu bắn event đọc bù NGAY khi
    /// <c>watcher.Enabled = true</c> — có thể trước cả khi hàm này chạy xong. Ghi đè
    /// sẽ xoá sạch số event vừa đếm được, khiến Log Summary báo "đã subscribe nhưng
    /// chưa có event" dù event đã vào DB. <c>TrySubscribe</c> cũng đã đổi sang gọi
    /// hàm này TRƯỚC khi bật watcher; hai lớp phòng thủ cho cùng một lỗi.
    /// </remarks>
    public void MarkSubscribed(string channel, long? resumeFromRecordId = null, long? catchUpTargetRecordId = null) =>
        _statuses.AddOrUpdate(channel,
            _ => new ChannelStatus(
                channel, Subscribed: true, Error: null, EventsReceived: 0, LastEventUtc: null,
                ResumeFromRecordId: resumeFromRecordId, CaughtUpCount: 0)
            {
                CatchUpTargetRecordId = catchUpTargetRecordId,
            },
            (_, existing) => existing with
            {
                Subscribed = true,
                Error = null,
                ResumeFromRecordId = resumeFromRecordId,
                CatchUpTargetRecordId = catchUpTargetRecordId,
            });

    public void MarkSubscribeFailed(string channel, string error) =>
        _statuses[channel] = new ChannelStatus(
            channel, Subscribed: false, error, EventsReceived: 0, LastEventUtc: null,
            ResumeFromRecordId: null, CaughtUpCount: 0);

    /// <summary>Subscribe thành công nhưng đọc event lỗi bất đồng bộ (thường do thiếu quyền).</summary>
    public void MarkReadError(string channel, string error) =>
        _statuses.AddOrUpdate(channel,
            _ => new ChannelStatus(
                channel, Subscribed: true, error, EventsReceived: 0, LastEventUtc: null,
                ResumeFromRecordId: null, CaughtUpCount: 0),
            (_, existing) => existing with { Error = error });

    /// <summary>
    /// <paramref name="recordId"/> dùng để biết event vừa nhận có phải đang ĐỌC BÙ lịch sử
    /// hay không: nếu &lt;= <see cref="ChannelStatus.CatchUpTargetRecordId"/> đã snapshot lúc
    /// subscribe thì tính vào <see cref="ChannelStatus.CaughtUpCount"/> (hiện thành badge
    /// "khôi phục N" ở Log Summary), ngược lại là event realtime thật.
    /// </summary>
    public void MarkEventReceived(string channel, long? recordId) =>
        _statuses.AddOrUpdate(channel,
            _ => new ChannelStatus(
                channel, Subscribed: true, Error: null, EventsReceived: 1, LastEventUtc: DateTime.UtcNow,
                ResumeFromRecordId: null, CaughtUpCount: 0)
            {
                LastRecordId = recordId,
            },
            (_, existing) =>
            {
                var caughtUp = existing.CaughtUpCount;
                if (recordId is long rid && existing.CatchUpTargetRecordId is long target && rid <= target)
                {
                    caughtUp++;
                }

                return existing with
                {
                    Error = null, // event moi doc duoc -> loi cu (neu co) khong con dung nua
                    EventsReceived = existing.EventsReceived + 1,
                    LastEventUtc = DateTime.UtcNow,
                    CaughtUpCount = caughtUp,
                    LastRecordId = recordId ?? existing.LastRecordId,
                };
            });

    public IReadOnlyList<ChannelStatus> All() =>
        _statuses.Values.OrderBy(s => s.Channel, StringComparer.OrdinalIgnoreCase).ToList();
}
