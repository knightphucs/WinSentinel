using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Configuration;
using TaskServiceMonitor.Data;
using TaskServiceMonitor.Detection;
using TaskServiceMonitor.Management;
using TaskServiceMonitor.Models;
using TaskServiceMonitor.Realtime;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Vòng poll so cấu hình service để bắt hai hành vi mà Windows KHÔNG phát event nào:
/// <b>đổi <c>binPath</c></b> và <b>đổi tài khoản khởi chạy</b>.
///
/// Vì sao phải poll thay vì nghe log: SCM chỉ phát <c>7040</c> khi <b>start type</b>
/// đổi — không có event nào cho <c>lpBinaryPathName</c> hay <c>lpServiceStartName</c>.
/// Đây là chỗ đáng chú ý về an ninh: đổi binPath của một service sẵn có là kỹ thuật
/// duy trì truy cập không sinh <c>7045</c>, tức là đi qua hoàn toàn im lặng nếu chỉ
/// nghe log. Xem docs/hanh-vi-mapping.md mục 3.1.
///
/// Đây là <b>lưới an toàn</b>, không thay thế đường log thật: bật SACL + audit
/// Registry thì <c>4657</c> bắt được cùng việc đó, tức thời và có kèm
/// <c>OldValue</c>/<c>NewValue</c>. Cố ý chạy cả hai để đường này hỏng thì đường kia
/// vẫn bắt được.
/// </summary>
public sealed class ServiceConfigWatcher(
    ServiceManager services,
    EventNotifier notifier,
    IServiceScopeFactory scopeFactory,
    AlertingOptions options,
    ILogger<ServiceConfigWatcher> logger) : BackgroundService
{
    private static readonly string Host = Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ServiceConfigPollEnabled)
        {
            logger.LogInformation(
                "ServiceConfigWatcher TAT theo cau hinh (Alerting:ServiceConfigPollEnabled=false). " +
                "Doi binPath / doi tai khoan service se KHONG duoc phat hien tru khi da bat audit " +
                "registry 4657.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, options.ServiceConfigPollSeconds));

        logger.LogInformation(
            "Bat dau theo doi cau hinh service, chu ky {Seconds}s.", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Mot vong poll loi KHONG duoc lam chet vong lap - neu khong thi mot su
                // co nhat thoi se lam mat kha nang phat hien vinh vien.
                logger.LogError(ex, "Loi khi so cau hinh service. Se thu lai o vong sau.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Dung theo doi cau hinh service.");
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // ServiceManager.List() da goi QueryServiceConfig cho tung service (1 syscall
        // moi service). CO Y khong dung QueryServiceConfig2 (3 syscall nua moi service) -
        // no chi can cho modal chi tiet, qua dat cho mot vong chay moi phut.
        var current = services.List();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<AlertStorageService>();

        var snapshots = await db.ServiceConfigSnapshots
            .Where(s => s.Hostname == Host)
            .ToDictionaryAsync(s => s.ServiceName, StringComparer.OrdinalIgnoreCase, ct);

        // Lan chay dau tien CHI lap baseline. Khong co buoc nay thi lan bat app dau
        // tien se sinh khoang 200 canh bao gia - moi service deu "moi xuat hien".
        var isBaseline = snapshots.Count == 0;
        var now = DateTime.UtcNow;
        var changes = 0;

        foreach (var service in current)
        {
            if (!snapshots.TryGetValue(service.Name, out var previous))
            {
                // Service moi xuat hien. KHONG canh bao o day: viec cai service da co
                // event 7045 + 4697 lo (rule SERVICE_INSTALLED), canh bao them chi lam
                // trung. Chi ghi nhan lam moc cho lan sau.
                db.ServiceConfigSnapshots.Add(Capture(service, now));
                continue;
            }

            if (isBaseline)
            {
                continue;
            }

            var alerts = DetectChanges(service, previous, now).ToList();

            if (alerts.Count == 0)
            {
                // KHONG dung tay vao dong nay khi cau hinh khong doi.
                //
                // Ban dau o day goi SetValues(...) vo dieu kien de cap nhat CapturedAt.
                // Nhung CapturedAt doi moi vong -> EF danh dau CA ~200 dong la modified
                // -> moi phut ban ~200 cau UPDATE xuong Postgres va lam ngap log, doi lai
                // duy nhat mot cot "lan cuoi nhin thay" ma khong ai doc. Moc so sanh chi
                // can doi khi gia tri THUC SU doi.
                continue;
            }

            foreach (var alert in alerts)
            {
                changes++;

                if (await storage.SaveAsync(alert, ct))
                {
                    await notifier.NotifyAlertAsync(alert, ct);
                }
            }

            // Chi ghi lai moc khi da co thay doi - neu khong lan poll sau se bao lai
            // dung thay doi do mai mai.
            db.Entry(previous).CurrentValues.SetValues(Capture(service, now));
        }

        await db.SaveChangesAsync(ct);

        if (isBaseline)
        {
            logger.LogInformation(
                "Da lap baseline cau hinh cho {Count} service tren {Host}. Tu vong sau moi bat dau so lech.",
                current.Count, Host);
        }
        else if (changes > 0)
        {
            logger.LogWarning("Phat hien {Count} thay doi cau hinh service.", changes);
        }
    }

    /// <summary>
    /// So từng field và sinh cảnh báo tương ứng. Một service có thể đổi nhiều thứ cùng
    /// lúc (vừa đổi binPath vừa đổi tài khoản) → trả về nhiều cảnh báo.
    /// </summary>
    private static IEnumerable<Alert> DetectChanges(
        ServiceInfo current, ServiceConfigSnapshot previous, DateTime now)
    {
        if (!Same(current.ImagePath, previous.ImagePath))
        {
            var writable = SuspiciousIndicators.MatchWritableDirectory(current.ImagePath);

            yield return Build(
                current.Name,
                RuleCatalog.ServiceImagePathChanged,
                "Đường dẫn thực thi của service bị đổi",
                RiskLevel.High,
                $"Service '{current.Name}' đổi binPath: {Show(previous.ImagePath)} → {Show(current.ImagePath)}" +
                (writable is not null ? $" — đường dẫn mới khớp '{writable}'" : string.Empty),
                "SCM không phát event cho thay đổi này — phát hiện bằng cách so cấu hình định kỳ.",
                now);
        }

        if (!Same(current.Account, previous.Account))
        {
            var toHighPrivilege = SuspiciousIndicators.IsHighPrivilegeServiceAccount(current.Account);

            yield return Build(
                current.Name,
                RuleCatalog.ServiceAccountChanged,
                "Tài khoản chạy service bị đổi",
                toHighPrivilege ? RiskLevel.High : RiskLevel.Medium,
                $"Service '{current.Name}' đổi tài khoản: {Show(previous.Account)} → {Show(current.Account)}",
                toHighPrivilege ? "Tài khoản mới có quyền cao nhất trên máy." : null,
                now);
        }

        if (!Same(current.StartType, previous.StartType))
        {
            // Trung phan nao voi event 7040, nhung 7040 co the bi mat (audit tat, app
            // dang tat, log bi xoay vong) - giu lai lam luoi an toan.
            var becameAutoStart =
                SuspiciousIndicators.IsAutoStart(current.StartType) &&
                !SuspiciousIndicators.IsAutoStart(previous.StartType);

            // Giu Medium ke ca khi doi sang auto start - cung ly do da ghi trong
            // RuleCatalog.EvaluateServiceStartTypeChanged: BITS/wuauserv doi qua lai
            // giua demand va auto lien tuc, cham High la tu lam ngap tab Canh bao.
            yield return Build(
                current.Name,
                RuleCatalog.ServiceStartTypeChanged,
                "Start type của service bị đổi",
                RiskLevel.Medium,
                $"Service '{current.Name}' đổi start type: {Show(previous.StartType)} → {Show(current.StartType)}",
                becameAutoStart ? "Service nay tự chạy cùng máy — xác minh thay đổi này là có chủ đích." : null,
                now);
        }
    }

    private static Alert Build(
        string serviceName,
        string ruleId,
        string ruleName,
        RiskLevel severity,
        string evidence,
        string? recommendation,
        DateTime now) => new()
        {
            // Khong co event Windows nao tuong ung - day chinh la ly do lop nay ton tai.
            SourceEventId = null,
            EventId = null,
            RuleId = ruleId,
            RuleName = ruleName,
            Severity = severity,
            ObjectType = MonitoredObjectType.Service,
            DetectedAt = now,
            EventTime = now,
            Hostname = Host,
            ObjectName = serviceName,
            Evidence = evidence,
            Recommendation = recommendation
        };

    private static ServiceConfigSnapshot Capture(ServiceInfo service, DateTime now) => new()
    {
        Hostname = Host,
        ServiceName = service.Name,
        ImagePath = service.ImagePath,
        Account = service.Account,
        StartType = service.StartType,
        CapturedAt = now
    };

    /// <summary>null và chuỗi rỗng coi như nhau — tránh cảnh báo giả khi không đọc được cấu hình.</summary>
    private static bool Same(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string Show(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(không rõ)" : value;
}
