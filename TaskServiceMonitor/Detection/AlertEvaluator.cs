using TaskServiceMonitor.Data;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Detection;

/// <summary>
/// Chạy toàn bộ tầng phát hiện trên một event rồi lưu cảnh báo sinh ra.
///
/// Gộp hai nguồn rule:
/// <list type="bullet">
///   <item><see cref="RuleCatalog"/> — hàm thuần trên một event đơn lẻ.</item>
///   <item><see cref="CorrelationRules"/> — cần tra DB nhiều event.</item>
/// </list>
///
/// Đăng ký <b>scoped</b> (phụ thuộc DbContext gián tiếp). Chỗ gọi là
/// <c>EventPersistenceService</c>, ngay sau khi <c>SaveAsync</c> trả <c>true</c> —
/// đó là chỗ DUY NHẤT biết chắc event vừa lưu là MỚI (đã qua dedupe), nên không cần
/// thêm cơ chế nào để tránh chấm lại cùng một event.
/// </summary>
internal sealed class AlertEvaluator(
    CorrelationRules correlation,
    AlertStorageService storage,
    ILogger<AlertEvaluator> logger)
{
    /// <summary>
    /// Chấm event và lưu cảnh báo. Trả về danh sách cảnh báo THỰC SỰ ghi mới —
    /// cảnh báo trùng bị loại để UI không hiện dòng lặp.
    /// </summary>
    internal async Task<IReadOnlyList<Alert>> EvaluateAndSaveAsync(
        WindowsMonitorEvent evt, CancellationToken ct = default)
    {
        var detectedAt = DateTime.UtcNow;
        List<Alert> saved = [];

        foreach (var (rule, hit) in RuleCatalog.Evaluate(evt))
        {
            var severity = hit.Severity;
            var evidence = hit.Evidence;

            // SERVICE_CRASH lên High khi service vừa bị cài/sửa ngay trước đó: crash
            // ngay sau khi binary bị thay khác hẳn crash ngẫu nhiên.
            if (rule.Id == RuleCatalog.ServiceCrash)
            {
                var changedAt = await correlation.FindRecentServiceChangeAsync(evt, ct);

                if (changedAt is not null)
                {
                    severity = RiskLevel.High;
                    evidence += $" — service này vừa bị cài/sửa lúc {changedAt:yyyy-MM-dd HH:mm:ss}Z";
                }
            }

            var alert = Build(evt, rule.Id, rule.Name, severity, evidence, hit.Recommendation, detectedAt);

            if (await SaveSafelyAsync(alert, ct))
            {
                saved.Add(alert);
            }
        }

        // Rule tương quan chạy sau: chúng cần event hiện tại đã nằm trong DB.
        IReadOnlyList<(string RuleId, string RuleName, RuleHit Hit)> correlationHits;
        try
        {
            correlationHits = await correlation.EvaluateAsync(evt, ct);
        }
        catch (Exception ex)
        {
            // Rule tương quan hỏng không được làm mất các cảnh báo đã sinh ở trên.
            logger.LogWarning(ex,
                "Khong chay duoc rule tuong quan cho event {EventId} ({ObjectName}).",
                evt.EventId, evt.ObjectName);

            return saved;
        }

        foreach (var (ruleId, ruleName, hit) in correlationHits)
        {
            var alert = Build(evt, ruleId, ruleName, hit.Severity, hit.Evidence, hit.Recommendation, detectedAt);

            if (await SaveSafelyAsync(alert, ct))
            {
                saved.Add(alert);
            }
        }

        return saved;
    }

    private async Task<bool> SaveSafelyAsync(Alert alert, CancellationToken ct)
    {
        try
        {
            return await storage.SaveAsync(alert, ct);
        }
        catch (Exception ex)
        {
            // Cảnh báo là tầng phái sinh - hỏng thì mất cảnh báo, KHÔNG được kéo theo
            // đường ghi event. Cùng triết lý với EventNotifier.
            logger.LogError(ex,
                "Khong luu duoc canh bao {RuleId} cho event {SourceEventId}.",
                alert.RuleId, alert.SourceEventId);

            return false;
        }
    }

    private static Alert Build(
        WindowsMonitorEvent evt,
        string ruleId,
        string ruleName,
        RiskLevel severity,
        string evidence,
        string? recommendation,
        DateTime detectedAt) => new()
        {
            SourceEventId = evt.Id,
            RuleId = ruleId,
            RuleName = ruleName,
            Severity = severity,
            ObjectType = evt.ObjectType,
            DetectedAt = detectedAt,
            EventTime = evt.TimeCreated,
            Hostname = evt.Hostname,
            ObjectName = evt.ObjectName,
            EventId = evt.EventId,
            Evidence = evidence,
            Recommendation = recommendation
        };
}
