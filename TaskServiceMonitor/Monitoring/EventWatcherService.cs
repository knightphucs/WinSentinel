using System.Diagnostics.Eventing.Reader;
using Microsoft.Extensions.Options;
using TaskServiceMonitor.Configuration;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Subscribe realtime Windows Event Log, đẩy từng event qua
/// <see cref="WindowsEventParser"/> rồi log kết quả đã parse.
/// Vẫn lưu XML thô vào samples/ để đối chiếu và để dashboard xem lại sau này.
/// Mỗi channel trong cấu hình dùng một EventLogWatcher riêng, vì một watcher
/// chỉ subscribe được đúng một channel.
/// </summary>
public sealed class EventWatcherService : BackgroundService
{
    private readonly ILogger<EventWatcherService> _logger;
    private readonly RawXmlSampleWriter _sampleWriter;
    private readonly WindowsEventParser _parser;
    private readonly RiskScorer _riskScorer;
    private readonly EventQueue _queue;
    private readonly EventLogOptions _options;
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>Giữ kèm tên channel + handler để log lỗi nêu đúng channel và gỡ được handler khi dừng.</summary>
    private sealed record Subscription(
        string Channel,
        EventLogWatcher Watcher,
        EventHandler<EventRecordWrittenEventArgs> Handler);

    public EventWatcherService(
        IOptions<EventLogOptions> options,
        RawXmlSampleWriter sampleWriter,
        WindowsEventParser parser,
        RiskScorer riskScorer,
        EventQueue queue,
        ILogger<EventWatcherService> logger)
    {
        _options = options.Value;
        _sampleWriter = sampleWriter;
        _parser = parser;
        _riskScorer = riskScorer;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var xpath = MonitoredEventIds.BuildXPathFilter();
        var channels = _options.EffectiveChannels;

        _logger.LogInformation("Event ID theo doi ({Count}): {EventIds}",
            MonitoredEventIds.All.Length, string.Join(", ", MonitoredEventIds.All));
        _logger.LogInformation("XPath filter: {XPath}", xpath);
        _logger.LogInformation("Channels se subscribe: {Channels}", string.Join(", ", channels));

        foreach (var channel in channels)
        {
            TrySubscribe(channel, xpath);
        }

        if (_subscriptions.Count == 0)
        {
            _logger.LogError(
                "Khong subscribe duoc BAT KY channel nao. App van chay nhung se khong nhan duoc event nao. " +
                "Xem cac loi ben tren de biet cach xu ly.");
        }
        else
        {
            _logger.LogInformation(
                "Dang lang nghe {Count}/{Total} channel. Sinh thu mot event (vi du: schtasks /create ...) de kiem tra.",
                _subscriptions.Count, channels.Length);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Bình thường khi app tắt.
        }
        finally
        {
            DisposeWatchers();

            // Bao cho consumer biet khong con event nao nua de no thoat vong lap.
            _queue.Complete();
        }
    }

    private void TrySubscribe(string channel, string xpath)
    {
        EventLogWatcher? watcher = null;
        try
        {
            var query = new EventLogQuery(channel, PathType.LogName, xpath);
            watcher = new EventLogWatcher(query, null, _options.ReadExistingEvents);

            // Handler rieng cho tung channel de thong bao loi neu dung ten channel.
            void Handler(object? sender, EventRecordWrittenEventArgs e)
                => OnEventRecordWritten(channel, e);

            watcher.EventRecordWritten += Handler;

            // Loi quyen/khong tim thay channel thuong bung ra chinh o dong nay.
            watcher.Enabled = true;

            _subscriptions.Add(new Subscription(channel, watcher, Handler));
            _logger.LogInformation("[OK] Da subscribe channel '{Channel}'", channel);
        }
        catch (EventLogNotFoundException ex)
        {
            watcher?.Dispose();
            _logger.LogError(
                "[LOI] Channel '{Channel}' khong ton tai. Kiem tra ten channel dung bang lenh:\n" +
                "        wevtutil el | findstr /i \"{Channel}\"\n" +
                "        (Chi tiet: {Message})",
                channel, channel, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            watcher?.Dispose();
            _logger.LogError(
                "[LOI] Khong du quyen doc channel '{Channel}'. Channel 'Security' bat buoc phai chay quyen Administrator.\n" +
                "        Cach xu ly: dong app, mo PowerShell bang 'Run as administrator', roi chay lai:\n" +
                "            dotnet run --project TaskServiceMonitor\n" +
                "        (Chi tiet: {Message})",
                channel, ex.Message);
        }
        catch (EventLogException ex)
        {
            watcher?.Dispose();
            _logger.LogError(
                "[LOI] Khong subscribe duoc channel '{Channel}'. Kiem tra channel co dang bi disable khong:\n" +
                "        wevtutil gl \"{Channel}\"\n" +
                "        (Chi tiet: {Message})",
                channel, channel, ex.Message);
        }
    }

    private void OnEventRecordWritten(string channel, EventRecordWrittenEventArgs e)
    {
        // Loi phat sinh trong luc subscribe thuong noi len o day theo kieu bat dong bo,
        // chu khong throw ngay luc gan Enabled = true. Vi du dien hinh: subscribe
        // channel 'Security' khi khong chay quyen Administrator -> Enabled = true
        // KHONG throw, nhung den luc doc event thi bao "The handle is invalid".
        if (e.EventException is not null)
        {
            _logger.LogError(
                "[LOI] Channel '{Channel}' subscribe duoc nhung KHONG doc duoc event: {Message}\n" +
                "        Nguyen nhan thuong gap: chua chay bang quyen Administrator (bat buoc voi channel 'Security').\n" +
                "        Cach xu ly: dong app, mo PowerShell bang 'Run as administrator', roi chay lai:\n" +
                "            dotnet run --project TaskServiceMonitor",
                channel, e.EventException.Message);
            return;
        }

        if (e.EventRecord is null)
        {
            return;
        }

        using var record = e.EventRecord;
        try
        {
            var xml = record.ToXml();

            // Van luu mau tho: buoc sau con can doi chieu, va dashboard se cho xem raw XML.
            _sampleWriter.TrySave(record.Id, record.RecordId, xml);

            // Cham diem rui ro ngay sau khi parse, TRUOC khi vao hang doi, de muc
            // rui ro duoc luu cung event va day len dashboard trong cung mot payload.
            var parsed = _parser.Parse(xml);
            parsed = parsed with { RiskLevel = _riskScorer.Score(parsed) };

            LogParsedEvent(parsed);

            // Chi day vao hang doi roi tra ve ngay. Viec ghi DB do
            // EventPersistenceService lam o luong khac - handler nay la callback
            // DONG BO cua Windows, chan no lai se lam nghen luong nhan event.
            _queue.TryEnqueue(parsed);
        }
        catch (Exception ex)
        {
            // Mot event loi khong duoc lam chet ca subscription.
            _logger.LogError(ex, "Loi khi xu ly event {EventId}", record.Id);
        }
    }

    private void LogParsedEvent(WindowsMonitorEvent e)
    {
        _logger.LogInformation(
            "[{Risk}][{Category}] {Action} | {Time:yyyy-MM-dd HH:mm:ss} | doi tuong='{ObjectName}' | user='{User}' | may={Machine} (EventId {EventId}, channel {Channel})",
            e.RiskLevel, e.ObjectType, e.ActionDescription, e.TimeCreated.ToLocalTime(),
            e.ObjectName ?? "-", e.ActorAccount ?? "-", e.Hostname, e.EventId, e.Channel);

        // Chi tiet them, tuy loai.
        if (e.TaskActionType is not null)
        {
            var action = e.TaskActionType switch
            {
                "Exec" => $"Exec: {e.TaskCommand} {e.TaskArguments ?? string.Empty}".TrimEnd(),
                "ComHandler" => $"ComHandler CLSID: {e.TaskComHandlerClassId ?? "-"}",
                _ => e.TaskActionType
            };

            _logger.LogInformation("        hanh dong: {Action} (runAs={RunAs}, runLevel={RunLevel})",
                action, e.TaskRunAsUser ?? "-", e.TaskRunLevel ?? "-");
        }

        if (e.ImagePath is not null)
        {
            _logger.LogInformation("        binary: {ImagePath} (type={ServiceType}, startType={StartType}, account={Account})",
                e.ImagePath, e.ServiceType ?? "-", e.StartType ?? "-", e.ServiceAccount ?? "-");
        }

        if (e.PreviousStartType is not null)
        {
            _logger.LogInformation("        start type: {Old} -> {New}", e.PreviousStartType, e.StartType ?? "-");
        }

        if (!e.IsRecognized)
        {
            _logger.LogWarning(
                "        EventId {EventId} chua co nhanh parse rieng - moi chi co du lieu tho: {Fields}",
                e.EventId, string.Join(", ", e.Data.Keys));
        }
    }

    private void DisposeWatchers()
    {
        foreach (var subscription in _subscriptions)
        {
            try
            {
                subscription.Watcher.Enabled = false;
                subscription.Watcher.EventRecordWritten -= subscription.Handler;
                subscription.Watcher.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Loi khi dong EventLogWatcher cua channel '{Channel}'",
                    subscription.Channel);
            }
        }

        _subscriptions.Clear();
        _logger.LogInformation("Da dong toan bo EventLogWatcher.");
    }
}

