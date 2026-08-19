using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskServiceMonitor.Data;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Detection;

/// <summary>
/// Giữ blacklist trong bộ nhớ và đồng bộ với DB.
///
/// <b>Vì sao cache</b>: <see cref="BlacklistMatcher"/> chạy trên MỌI event, ngay
/// trong đường ghi. Một lượt truy vấn DB cho mỗi event chỉ để lấy một danh sách gần
/// như không đổi là lãng phí — và tệ hơn, nó buộc phần so khớp phải async, kéo theo
/// cả <c>RuleCatalog</c> vốn đang là hàm thuần.
///
/// Đăng ký <b>singleton</b> và dùng <see cref="IServiceScopeFactory"/> để chạm DB —
/// cùng khuôn với các <c>BackgroundService</c> trong dự án, vì <c>DbContext</c> là
/// scoped còn lớp này thì không.
/// </summary>
internal sealed class BlacklistRegistry(
    IServiceScopeFactory scopeFactory,
    ILogger<BlacklistRegistry> logger)
{
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Snapshot bất biến. Thay bằng cách GÁN CẢ MẢNG MỚI chứ không sửa tại chỗ, nên
    /// bên đọc không cần khoá: chúng thấy hoặc bản cũ hoặc bản mới, không bao giờ
    /// thấy bản đang sửa dở.
    /// </summary>
    private volatile IReadOnlyList<BlacklistEntry> _snapshot = [];

    /// <summary>Các dòng đang bật, dùng cho <see cref="BlacklistMatcher.Match"/>.</summary>
    internal IReadOnlyList<BlacklistEntry> Active => _snapshot;

    internal async Task ReloadAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();

            _snapshot = await db.Blacklist.AsNoTracking()
                .Where(x => x.Enabled)
                .ToListAsync(ct);

            logger.LogInformation("Da nap {Count} dong blacklist dang bat.", _snapshot.Count);
        }
        catch (Exception ex)
        {
            // Blacklist hong khong duoc lam chet duong ghi event - cac rule tinh van
            // chay binh thuong. Giu snapshot cu.
            logger.LogError(ex, "Khong nap duoc blacklist, giu nguyen ban dang co.");
        }
    }

    /// <summary>
    /// Thêm một dấu hiệu tự học. Trả về dòng vừa tạo, hoặc <c>null</c> nếu giá trị đó
    /// đã có (kể cả đang bị tắt — đã tắt tay thì không được tự bật lại).
    /// </summary>
    internal async Task<BlacklistEntry?> LearnAsync(LearnCandidate candidate, CancellationToken ct = default)
    {
        var entry = new BlacklistEntry
        {
            Kind = candidate.Kind,
            Value = candidate.Value,
            Severity = candidate.Severity,
            Source = BlacklistSource.AutoLearned,
            Enabled = true,
            Reason = candidate.Reason,
            LearnedFromRuleId = candidate.RuleId,
            LearnedFromObjectName = candidate.ObjectName,
            CreatedAt = DateTime.UtcNow
        };

        var added = await AddAsync(entry, ct);

        if (added is not null)
        {
            logger.LogInformation(
                "Da hoc dau hieu moi vao blacklist: {Kind} '{Value}' (tu rule {RuleId})",
                candidate.Kind, candidate.Value, candidate.RuleId);
        }

        return added;
    }

    /// <summary>
    /// Ghi một dòng mới rồi nạp lại cache. <c>null</c> nếu trùng
    /// <c>(Kind, Value)</c> — unique index ở tầng DB chặn, không kiểm tra trước bằng
    /// <c>EXISTS</c> (cùng lý do với <c>AlertStorageService</c>: kiểm tra trước vẫn có
    /// khe đua).
    /// </summary>
    internal async Task<BlacklistEntry?> AddAsync(BlacklistEntry entry, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();

        db.Blacklist.Add(entry);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: UniqueViolation
        })
        {
            return null;
        }

        await ReloadAsync(ct);
        return entry;
    }

    /// <summary>
    /// Cộng số lần khớp. Dùng <c>ExecuteUpdate</c> để cộng dồn NGAY TRONG SQL —
    /// đọc-sửa-ghi sẽ mất số đếm khi hai event khớp cùng lúc, và
    /// <see cref="BlacklistEntry"/> là record init-only nên không sửa tại chỗ được.
    ///
    /// CỐ Ý không nạp lại cache sau khi cộng: <c>HitCount</c> không ảnh hưởng tới việc
    /// so khớp, nạp lại chỉ tốn một vòng truy vấn cho mỗi event khớp.
    /// </summary>
    internal async Task RecordHitsAsync(IEnumerable<Guid> entryIds, CancellationToken ct = default)
    {
        var ids = entryIds.Distinct().ToArray();

        if (ids.Length == 0)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();
            var now = DateTime.UtcNow;

            await db.Blacklist
                .Where(x => ids.Contains(x.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.HitCount, x => x.HitCount + 1)
                          .SetProperty(x => x.LastHitAt, now),
                    ct);
        }
        catch (Exception ex)
        {
            // Dem hut mot lan khong dang de mat ca canh bao.
            logger.LogWarning(ex, "Khong cap nhat duoc so lan khop cua blacklist.");
        }
    }

    internal async Task<IReadOnlyList<BlacklistEntry>> ListAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();

        return await db.Blacklist.AsNoTracking()
            .OrderByDescending(x => x.HitCount)
            .ThenByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
    }

    internal async Task<bool> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();

        var changed = await db.Blacklist
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, enabled), ct);

        if (changed > 0)
        {
            await ReloadAsync(ct);
        }

        return changed > 0;
    }

    internal async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();

        var deleted = await db.Blacklist.Where(x => x.Id == id).ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            await ReloadAsync(ct);
        }

        return deleted > 0;
    }
}
