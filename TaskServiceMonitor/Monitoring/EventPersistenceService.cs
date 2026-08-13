using TaskServiceMonitor.Data;
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
    ILogger<EventPersistenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Bat dau ghi event xuong DB.");

        var saved = 0;
        var duplicates = 0;

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

        logger.LogInformation("Dung ghi DB. Da luu {Saved} event, bo qua {Duplicates} event trung.",
            saved, duplicates);
    }
}
