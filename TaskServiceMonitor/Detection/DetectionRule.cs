using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Detection;

/// <summary>
/// Kết quả khi một rule khớp. <c>null</c> = không khớp.
///
/// <see cref="Severity"/> nằm ở đây chứ không phải ở <see cref="DetectionRule"/> vì
/// nhiều rule có mức ĐỘNG: <c>SERVICE_ACCOUNT_CHANGED</c> là Medium bình thường
/// nhưng lên High khi đổi sang LocalSystem.
/// </summary>
internal sealed record RuleHit
{
    public required RiskLevel Severity { get; init; }

    /// <summary>Câu bằng chứng trích từ chính dữ liệu event — xem <see cref="Alert.Evidence"/>.</summary>
    public required string Evidence { get; init; }

    public string? Recommendation { get; init; }
}

/// <summary>
/// Một rule phát hiện. Toàn bộ phần <see cref="Evaluate"/> là HÀM THUẦN trên
/// <see cref="WindowsMonitorEvent"/> — không đụng DB, không đụng WinAPI — nên test
/// được trực tiếp trên mẫu XML thật mà không cần Windows.
///
/// Rule cần tra lịch sử (task tạo rồi xoá ngay, lệnh bị đổi so với lần trước) KHÔNG
/// nằm ở đây mà ở <c>CorrelationRules</c>, vì chúng cần <c>MonitorDbContext</c>.
/// </summary>
internal sealed record DetectionRule
{
    /// <summary>Mã cố định, ví dụ <c>TASK_WRITABLE_DIR</c>. Không đổi sau khi đã phát hành.</summary>
    public required string Id { get; init; }

    /// <summary>Tên tiếng Việt hiển thị cho người đọc.</summary>
    public required string Name { get; init; }

    /// <summary>Mức thường gặp nhất của rule — chỉ dùng để hiển thị trong bảng rule.</summary>
    public required RiskLevel TypicalSeverity { get; init; }

    public required MonitoredObjectType ObjectType { get; init; }

    /// <summary>Giải thích vì sao hành vi này đáng quan tâm.</summary>
    public required string Description { get; init; }

    /// <summary>Event ID sinh ra rule này, để đối chiếu với bảng phân rã của mentor.</summary>
    public required int[] RelatedEventIds { get; init; }

    public required Func<WindowsMonitorEvent, RuleHit?> Evaluate { get; init; }
}

/// <summary>
/// Bản mô tả rule không kèm hàm — dùng cho <c>GET /api/alerts/rules</c> để bảng phân
/// rã hành vi hiện được ngay trong giao diện, không chỉ nằm trong file markdown.
/// </summary>
public sealed record DetectionRuleDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required RiskLevel TypicalSeverity { get; init; }
    public required MonitoredObjectType ObjectType { get; init; }
    public required string Description { get; init; }
    public required int[] RelatedEventIds { get; init; }
}
