using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Data;

/// <summary>
/// Lưu event đã parse xuống DB. Đăng ký <b>scoped</b> vì phụ thuộc
/// <see cref="MonitorDbContext"/> (cũng scoped).
/// </summary>
public sealed class EventStorageService(
    MonitorDbContext db,
    ILogger<EventStorageService> logger)
{
    /// <summary>Mã lỗi PostgreSQL cho vi phạm ràng buộc unique.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Trả về <c>true</c> nếu đã ghi mới, <c>false</c> nếu event đã tồn tại.
    /// Ném exception nếu lỗi thật (mất kết nối DB...) để lớp gọi biết mà log.
    /// </summary>
    public async Task<bool> SaveAsync(WindowsMonitorEvent evt, CancellationToken ct = default)
    {
        db.Events.Add(evt);

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: UniqueViolation
        })
        {
            // Trung lap la chuyen BINH THUONG: app restart doc lai log cu, hoac WEF
            // gui lai cung mot event. Bo qua chu khong coi la loi.
            db.Entry(evt).State = EntityState.Detached;

            logger.LogDebug(
                "Bo qua event trung: EventId={EventId} host={Host} channel={Channel} recordId={RecordId}",
                evt.EventId, evt.Hostname, evt.Channel, evt.RecordId);

            return false;
        }
    }
}
