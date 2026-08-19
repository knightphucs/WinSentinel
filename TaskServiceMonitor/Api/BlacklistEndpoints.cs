using TaskServiceMonitor.Detection;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Api;

/// <summary>Thân request khi thêm một dấu hiệu bằng tay.</summary>
public sealed record AddBlacklistRequest(
    string? Kind, string? Value, string? Severity, string? Reason);

/// <summary>
/// API quản lý blacklist (bước 14). Cùng khuôn với <see cref="AlertEndpoints"/>:
/// static extension class, Minimal API, không controller.
/// </summary>
public static class BlacklistEndpoints
{
    public static void MapBlacklistEndpoints(this WebApplication app)
    {
        app.MapGet("/api/blacklist", GetAll);
        app.MapPost("/api/blacklist", Add);
        app.MapPost("/api/blacklist/{id:guid}/toggle", Toggle);
        app.MapDelete("/api/blacklist/{id:guid}", Delete);
    }

    private static async Task<IResult> GetAll(BlacklistRegistry registry, CancellationToken ct)
    {
        var rows = await registry.ListAsync(ct);

        return Results.Ok(new
        {
            total = rows.Count,
            enabled = rows.Count(r => r.Enabled),
            autoLearned = rows.Count(r => r.Source == BlacklistSource.AutoLearned),
            entries = rows
        });
    }

    /// <summary>
    /// POST /api/blacklist — thêm tay. Giá trị được chuẩn hoá bằng ĐÚNG hàm mà
    /// <see cref="BlacklistMatcher"/> dùng lúc so; không dùng chung thì dòng nhập bằng
    /// chữ hoa sẽ không bao giờ khớp và trông như tính năng hỏng.
    /// </summary>
    private static async Task<IResult> Add(
        BlacklistRegistry registry, AddBlacklistRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Value))
        {
            return Results.BadRequest(new { error = "Thieu 'value'." });
        }

        if (!Enum.TryParse<BlacklistKind>(req.Kind, ignoreCase: true, out var kind))
        {
            return Results.BadRequest(new
            {
                error = $"'kind' khong hop le. Nhan mot trong: {string.Join(", ", Enum.GetNames<BlacklistKind>())}."
            });
        }

        // Mac dinh High: muc dich cua blacklist la "gap lai la bao dong ngay".
        if (!Enum.TryParse<RiskLevel>(req.Severity, ignoreCase: true, out var severity))
        {
            severity = RiskLevel.High;
        }

        var normalized = BlacklistMatcher.Normalize(req.Value);

        if (normalized.Length == 0)
        {
            return Results.BadRequest(new { error = "'value' rong sau khi chuan hoa." });
        }

        var entry = new BlacklistEntry
        {
            Kind = kind,
            Value = normalized,
            Severity = severity,
            Source = BlacklistSource.Manual,
            Enabled = true,
            Reason = string.IsNullOrWhiteSpace(req.Reason) ? "Người dùng thêm tay" : req.Reason,
            CreatedAt = DateTime.UtcNow
        };

        var added = await registry.AddAsync(entry, ct);

        return added is null
            ? Results.Conflict(new { error = $"Da co dong '{normalized}' cho loai {kind}." })
            : Results.Ok(added);
    }

    /// <summary>
    /// Bật/tắt một dòng. CỐ Ý có riêng thao tác này thay vì bắt phải xoá: dòng tự học
    /// có thể là dương tính giả, mà tắt đi để theo dõi thêm vẫn tốt hơn xoá mất dấu vết
    /// về việc app đã từng học nó.
    /// </summary>
    private static async Task<IResult> Toggle(
        BlacklistRegistry registry, Guid id, bool enabled, CancellationToken ct)
    {
        var ok = await registry.SetEnabledAsync(id, enabled, ct);

        return ok
            ? Results.Ok(new { id, enabled })
            : Results.NotFound(new { error = "Khong tim thay dong blacklist nay." });
    }

    private static async Task<IResult> Delete(
        BlacklistRegistry registry, Guid id, CancellationToken ct)
    {
        var ok = await registry.DeleteAsync(id, ct);

        return ok
            ? Results.Ok(new { id, deleted = true })
            : Results.NotFound(new { error = "Khong tim thay dong blacklist nay." });
    }
}
