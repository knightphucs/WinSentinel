using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Data;

/// <summary>
/// Lưu cảnh báo xuống DB. Đăng ký <b>scoped</b> vì phụ thuộc
/// <see cref="MonitorDbContext"/>.
///
/// Cùng khuôn với <see cref="EventStorageService"/>: chống trùng bằng unique index ở
/// tầng DB rồi bắt mã lỗi <c>23505</c>, chứ không kiểm tra trước bằng
/// <c>SELECT ... EXISTS</c> — kiểm tra trước vẫn có khe đua giữa hai lần chạy.
/// </summary>
public sealed class AlertStorageService(
    MonitorDbContext db,
    ILogger<AlertStorageService> logger)
{
    private const string UniqueViolation = "23505";

    /// <summary>
    /// <c>true</c> nếu ghi mới, <c>false</c> nếu cảnh báo này đã tồn tại (cùng event
    /// gốc + cùng rule). Nhờ vậy chạy lại <c>--rebuild-alerts</c> nhiều lần cũng
    /// không nhân đôi.
    /// </summary>
    public async Task<bool> SaveAsync(Alert alert, CancellationToken ct = default)
    {
        db.Alerts.Add(alert);

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
            db.Entry(alert).State = EntityState.Detached;

            logger.LogDebug(
                "Bo qua canh bao trung: rule={RuleId} event={SourceEventId}",
                alert.RuleId, alert.SourceEventId);

            return false;
        }
    }
}
