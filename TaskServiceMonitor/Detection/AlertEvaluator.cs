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
    BlacklistRegistry blacklist,
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

            // Hit High tren mot duong dan cu the -> dong dau vao blacklist de lan sau
            // gap lai la bao ngay. Dieu kien hoc rat hep, xem BlacklistLearner.
            await TryLearnAsync(evt, rule.Id, severity, evidence, ct);
        }

        // Blacklist chay RIENG, khong nam trong RuleCatalog.All: no can danh sach lay
        // tu DB nen khong phai ham thuan. Cung ly do voi CorrelationRules.
        await EvaluateBlacklistAsync(evt, saved, detectedAt, ct);

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

    /// <summary>
    /// So event với blacklist rồi sinh cảnh báo <c>BLACKLIST_HIT</c>.
    ///
    /// Một event có thể khớp nhiều dòng blacklist, nhưng CHỈ sinh MỘT cảnh báo (gộp
    /// bằng chứng): unique index <c>IX_Alerts_Dedup</c> là <c>(SourceEventId, RuleId)</c>
    /// nên hai cảnh báo cùng rule trên cùng event sẽ bị chặn ở tầng DB — dòng thứ hai
    /// im lặng biến mất và người xem mất luôn bằng chứng của nó.
    /// </summary>
    private async Task EvaluateBlacklistAsync(
        WindowsMonitorEvent evt, List<Alert> saved, DateTime detectedAt, CancellationToken ct)
    {
        IReadOnlyList<BlacklistMatch> matches;
        try
        {
            matches = BlacklistMatcher.Match(evt, blacklist.Active);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Khong so duoc blacklist cho event {EventId}.", evt.EventId);
            return;
        }

        if (matches.Count == 0)
        {
            return;
        }

        // Muc = cao nhat trong cac dong khop.
        var severity = matches.Max(m => m.Entry.Severity);

        var evidence = string.Join(" | ", matches.Select(m =>
            $"'{m.Entry.Value}' ({DescribeKind(m.Entry.Kind)}, {DescribeSource(m.Entry.Source)}) " +
            $"khớp ở {m.MatchedIn}: {m.MatchedValue}"));

        var alert = Build(
            evt, RuleCatalog.BlacklistHit, "Khớp dấu hiệu trong blacklist",
            severity, evidence,
            "Dấu hiệu này đã bị đóng dấu xấu từ trước — xem tab Blacklist để biết vì sao.",
            detectedAt);

        if (await SaveSafelyAsync(alert, ct))
        {
            saved.Add(alert);
        }

        await blacklist.RecordHitsAsync(matches.Select(m => m.Entry.Id), ct);
    }

    private async Task TryLearnAsync(
        WindowsMonitorEvent evt, string ruleId, RiskLevel severity, string evidence,
        CancellationToken ct)
    {
        try
        {
            var candidate = BlacklistLearner.FromHit(evt, ruleId, severity, evidence);

            if (candidate is not null)
            {
                await blacklist.LearnAsync(candidate, ct);
            }
        }
        catch (Exception ex)
        {
            // Hoc that bai khong duoc lam mat canh bao vua sinh.
            logger.LogWarning(ex, "Khong hoc duoc dau hieu tu rule {RuleId}.", ruleId);
        }
    }

    private static string DescribeKind(BlacklistKind kind) => kind switch
    {
        BlacklistKind.ExecutablePath => "đường dẫn",
        BlacklistKind.FileName => "tên file",
        BlacklistKind.CommandFragment => "chuỗi lệnh",
        BlacklistKind.Account => "tài khoản",
        _ => kind.ToString()
    };

    private static string DescribeSource(BlacklistSource source) => source switch
    {
        BlacklistSource.AutoLearned => "tự học",
        BlacklistSource.Manual => "nhập tay",
        _ => source.ToString()
    };

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
