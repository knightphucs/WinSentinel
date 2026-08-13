using Microsoft.AspNetCore.SignalR;
using TaskServiceMonitor.Api;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Realtime;

/// <summary>
/// Đẩy event vừa lưu lên mọi dashboard đang mở.
/// Bọc <see cref="IHubContext{T}"/> lại để phần ghi DB không phải biết gì về SignalR.
/// </summary>
public sealed class EventNotifier(
    IHubContext<MonitorHub> hub,
    ILogger<EventNotifier> logger)
{
    public async Task NotifyAsync(WindowsMonitorEvent evt, CancellationToken ct = default)
    {
        try
        {
            // Gui ban tom tat, khong gui RawXml. Dashboard can raw thi goi
            // GET /api/events/{id} khi nguoi dung bam vao dong do.
            await hub.Clients.All.SendAsync(
                MonitorHub.ReceiveEventMessage, EventSummaryDto.From(evt), ct);
        }
        catch (Exception ex)
        {
            // Day len UI that bai KHONG duoc lam hong viec ghi DB - mat mot dong
            // tren man hinh nhe hon nhieu so voi mat du lieu giam sat.
            logger.LogWarning(ex,
                "Khong day duoc event {EventId} len dashboard qua SignalR (du lieu van da luu DB).",
                evt.EventId);
        }
    }
}
