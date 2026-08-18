using Microsoft.AspNetCore.SignalR;

namespace TaskServiceMonitor.Realtime;

/// <summary>
/// Hub cho dashboard kết nối vào. Không có method nào vì luồng dữ liệu chỉ đi
/// một chiều server → client; client không gọi ngược lại gì cả.
/// </summary>
public sealed class MonitorHub : Hub
{
    public const string Route = "/monitorHub";

    /// <summary>Tên message gửi cho client. Client lắng nghe đúng chuỗi này.</summary>
    public const string ReceiveEventMessage = "ReceiveEvent";

    /// <summary>
    /// Cảnh báo mới (bước 11). Tách khỏi <see cref="ReceiveEventMessage"/> vì hai thứ
    /// khác nhau về bản chất: event là dòng nhật ký, cảnh báo là kết luận — và một
    /// event có thể sinh ra nhiều cảnh báo.
    /// </summary>
    public const string ReceiveAlertMessage = "ReceiveAlert";
}
