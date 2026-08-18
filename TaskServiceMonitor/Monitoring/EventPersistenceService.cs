using Microsoft.Extensions.Options;
using TaskServiceMonitor.Configuration;
using TaskServiceMonitor.Data;
using TaskServiceMonitor.Detection;
using TaskServiceMonitor.Models;
using TaskServiceMonitor.Realtime;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Consumer của <see cref="EventQueue"/>: đọc event ra rồi ghi xuống DB.
///
/// Tách riêng khỏi <see cref="EventWatcherService"/> để việc ghi DB chậm hay lỗi
/// không ảnh hưởng tới luồng nhận event từ Windows.
/// </summary>
public sealed class EventPersistenceService(
    EventQueue queue,
    EventNotifier notifier,
    IServiceScopeFactory scopeFactory,
    IOptions<AlertingOptions> alertingOptions,
    ILogger<EventPersistenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Bat dau ghi event xuong DB.");

        // Rule muc Low la loai "ghi nhan hanh vi" - van luu va van xem duoc o tab
        // Canh bao, chi khong bat banner. Bat banner cho ca nhom do thi banner chay
        // lien tuc va mat het y nghia.
        var broadcastThreshold =
            Enum.TryParse<RiskLevel>(alertingOptions.Value.MinimumBroadcastSeverity, true, out var parsed)
                ? parsed
                : RiskLevel.Medium;

        var saved = 0;
        var duplicates = 0;
        var alerts = 0;

        try
        {
            await foreach (var evt in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // BackgroundService la singleton con DbContext la scoped ->
                    // phai tao scope MOI cho moi lan ghi, khong duoc inject thang DbContext.
                    using var scope = scopeFactory.CreateScope();
                    var storage = scope.ServiceProvider.GetRequiredService<EventStorageService>();

                    if (await storage.SaveAsync(evt, stoppingToken))
                    {
                        saved++;
                        logger.LogDebug("Da luu event {EventId} ({Action}) - tong da luu: {Saved}",
                            evt.EventId, evt.ActionDescription, saved);

                        // Chi day len dashboard khi luu MOI thanh cong. Event trung
                        // (SaveAsync tra false) khong day de UI khong hien dong lap.
                        await notifier.NotifyAsync(evt, stoppingToken);

                        // Cham rule -> sinh canh bao. Dat O DAY, ben trong nhanh
                        // "luu moi thanh cong", vi day la cho DUY NHAT biet chac event
                        // la MOI (da qua dedupe) -> khong can them co che nao de tranh
                        // cham lai cung mot event.
                        //
                        // Lay tu cung scope voi EventStorageService: AlertEvaluator va
                        // CorrelationRules deu la scoped (phu thuoc DbContext).
                        var evaluator = scope.ServiceProvider.GetRequiredService<AlertEvaluator>();

                        foreach (var alert in await evaluator.EvaluateAndSaveAsync(evt, stoppingToken))
                        {
                            alerts++;

                            if (alert.Severity >= broadcastThreshold)
                            {
                                await notifier.NotifyAlertAsync(alert, stoppingToken);
                            }
                        }
                    }
                    else
                    {
                        duplicates++;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Mot event ghi loi KHONG duoc lam chet vong lap - neu khong thi
                    // mot su co DB nhat thoi se lam app ngung luu vinh vien.
                    logger.LogError(ex,
                        "Khong ghi duoc event {EventId} tu {Host} xuong DB. Kiem tra PostgreSQL " +
                        "co dang chay va connection string 'MonitorDb' co dung khong.",
                        evt.EventId, evt.Hostname);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Binh thuong khi app tat.
        }

        logger.LogInformation(
            "Dung ghi DB. Da luu {Saved} event, bo qua {Duplicates} event trung, sinh {Alerts} canh bao.",
            saved, duplicates, alerts);
    }
}
