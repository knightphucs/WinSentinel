using System.Diagnostics.Eventing.Reader;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskServiceMonitor.Configuration;
using TaskServiceMonitor.Data;
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
    private readonly ChannelStatusRegistry _statusRegistry;
    private readonly IServiceScopeFactory _scopeFactory;
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
        ChannelStatusRegistry statusRegistry,
        IServiceScopeFactory scopeFactory,
        ILogger<EventWatcherService> logger)
    {
        _options = options.Value;
        _sampleWriter = sampleWriter;
        _parser = parser;
        _riskScorer = riskScorer;
        _queue = queue;
        _statusRegistry = statusRegistry;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channels = _options.EffectiveChannels;

        _logger.LogInformation("Event ID theo doi ({Count}): {EventIds}",
            MonitoredEventIds.All.Length, string.Join(", ", MonitoredEventIds.All));
        _logger.LogInformation("Channels se subscribe: {Channels}", string.Join(", ", channels));

        // Doc "da thay RecordId lon nhat toi dau" tu chinh DB (nguon su that duy
        // nhat - khong can bang/file bookmark rieng) truoc khi subscribe, de biet
        // channel nao co the "resume" duoc thay vi luon bat dau tu "now" nhu truoc.
        var lastKnownRecordIds = await LoadLastKnownRecordIdsAsync(stoppingToken);

        // Moi channel loc theo dung nhom ID cua no (xem MonitoredEventIds.ByChannel) -
        // KHONG dung chung mot filter cho moi channel nhu truoc, vi ID cua channel
        // khac (vd TaskScheduler-Operational) la so nho, chung chung, de trung nham
        // voi ID khong lien quan neu loc gop.
        foreach (var channel in channels)
        {
            var (cursor, catchUpTarget) = ResolveCursor(
                channel,
                lastKnownRecordIds.TryGetValue(channel, out var lastKnownRecordId) ? lastKnownRecordId : null);

            var xpath = MonitoredEventIds.BuildXPathFilter(channel, cursor);

            // Co cursor -> phai doc ca event da co san khop filter (chinh la phan
            // "lo mat luc app tat"), khong chi realtime tu bay gio - do la ban chat
            // cua co che "gia lap bookmark" bang XPath thay vi dung class EventBookmark
            // (EventBookmark can BinaryFormatter, da bi go mac dinh tu .NET 8).
            var readExistingEvents = cursor is not null || _options.ReadExistingEvents;

            _logger.LogInformation(
                "XPath filter cho channel '{Channel}': {XPath} (ReadExistingEvents hieu luc = {ReadExisting})",
                channel, xpath, readExistingEvents);

            TrySubscribe(channel, xpath, readExistingEvents, cursor, catchUpTarget);
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

    /// <summary>
    /// Doc RecordId lon nhat da luu cho TUNG CHANNEL (KHONG group theo Hostname):
    /// EventRecordID la so thu tu cua chinh channel do tren may nay, khong thuoc ve
    /// tung may nguon - channel "ForwardedEvents" (WEF) gop nhieu Hostname vao CHUNG
    /// mot chuoi so, group theo Hostname se lam lech/rot cursor.
    /// </summary>
    private async Task<Dictionary<string, long>> LoadLastKnownRecordIdsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();

        // Loc RecordId != null TRUOC khi group: neu mot channel co toan bo dong
        // RecordId null (parser thinh thoang khong doc duoc), Max() tren group do
        // se ra null va vo dictionary value (long, khong nullable) - loc truoc thi
        // channel do don gian KHONG co trong dictionary, roi vao nhanh "khong co cursor".
        var cursors = await db.Events
            .Where(e => e.RecordId != null)
            .GroupBy(e => e.Channel)
            .Select(g => new { Channel = g.Key, LastRecordId = g.Max(e => e.RecordId!.Value) })
            .ToDictionaryAsync(x => x.Channel, x => x.LastRecordId, StringComparer.OrdinalIgnoreCase, ct);

        _logger.LogInformation("Cursor RecordId doc tu DB cho {Count} channel: {Cursors}",
            cursors.Count, string.Join(", ", cursors.Select(kv => $"{kv.Key}={kv.Value}")));

        return cursors;
    }

    /// <summary>
    /// Kiem tra channel co ve da bi CLEAR hoac ROLLBACK (vd "wevtutil cl") trong luc
    /// app tat khong: neu record moi nhat HIEN CO trong log nho hon cursor da luu,
    /// nghia la so thu tu ban ghi da bi danh lai tu dau - dung cursor cu se lam XPath
    /// filter "EventRecordID>N" khong bao gio khop duoc event moi nao nua (vi event
    /// moi danh so lai tu dau, luon nho hon N). Phai bo cursor va subscribe lai tu dau.
    ///
    /// Tra ve kem theo <c>CatchUpTarget</c> - snapshot "RecordId moi nhat hien co" tai
    /// thoi diem nay, dung lam moc de <see cref="ChannelStatusRegistry"/> dem duoc bao
    /// nhieu event nhan duoc sau day la dang DOC BU (hien thanh badge o Log Summary),
    /// chu khong anh huong gi toi filter/subscribe.
    /// </summary>
    private (long? Cursor, long? CatchUpTarget) ResolveCursor(string channel, long? lastKnownRecordId)
    {
        if (lastKnownRecordId is null)
        {
            return (null, null);
        }

        try
        {
            var info = EventLogSession.GlobalSession.GetLogInformation(channel, PathType.LogName);

            if (info.OldestRecordNumber is long oldest && info.RecordCount is long count)
            {
                var newestAvailable = oldest + count - 1;
                if (newestAvailable < lastKnownRecordId)
                {
                    _logger.LogWarning(
                        "[CANH BAO] Channel '{Channel}' co ve da bi CLEAR hoac ROLLBACK trong luc app tat " +
                        "(cursor da luu = {Cursor}, nhung record moi nhat hien co chi la {Newest}). " +
                        "Bo cursor cu, subscribe lai tu dau de tranh loc mat toan bo event moi.",
                        channel, lastKnownRecordId, newestAvailable);
                    return (null, null);
                }

                return (lastKnownRecordId, newestAvailable);
            }
        }
        catch (EventLogNotFoundException ex)
        {
            _logger.LogWarning(ex,
                "Channel '{Channel}' khong ton tai khi kiem tra clear/rollback - TrySubscribe ben duoi se tu bao loi.",
                channel);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex,
                "Khong du quyen doc thong tin log cua channel '{Channel}' de kiem tra clear/rollback - giu cursor cu.",
                channel);
        }
        catch (EventLogException ex)
        {
            _logger.LogWarning(ex,
                "Khong doc duoc thong tin log cua channel '{Channel}' de kiem tra clear/rollback - giu cursor cu.",
                channel);
        }

        // Giu cursor de loc dung, nhung khong co snapshot "moc ket thuc catch-up" (loi
        // quyen/API tra null...) nen khong dem duoc CaughtUpCount - Log Summary se hien
        // cursor nhung khong hien badge "khoi phuc N", chap nhan duoc vi day chi la UI phu.
        return (lastKnownRecordId, null);
    }

    private void TrySubscribe(
        string channel, string xpath, bool readExistingEvents,
        long? resumeFromRecordId, long? catchUpTargetRecordId)
    {
        EventLogWatcher? watcher = null;
        try
        {
            var query = new EventLogQuery(channel, PathType.LogName, xpath);
            watcher = new EventLogWatcher(query, null, readExistingEvents);

            // Handler rieng cho tung channel de thong bao loi neu dung ten channel.
            void Handler(object? sender, EventRecordWrittenEventArgs e)
                => OnEventRecordWritten(channel, e);

            watcher.EventRecordWritten += Handler;

            // PHAI dang ky trang thai TRUOC khi bat watcher: khi co cursor thi
            // readExistingEvents = true, Windows bat dau ban event doc bu ngay tai dong
            // Enabled = true (tren thread khac). Dang ky sau se de mat so dem cua chinh
            // loat event do - dung la trieu chung "da subscribe nhung chua co event".
            _statusRegistry.MarkSubscribed(channel, resumeFromRecordId, catchUpTargetRecordId);

            // Loi quyen/khong tim thay channel thuong bung ra chinh o dong nay.
            watcher.Enabled = true;

            _subscriptions.Add(new Subscription(channel, watcher, Handler));
            _logger.LogInformation("[OK] Da subscribe channel '{Channel}'", channel);
        }
        catch (EventLogNotFoundException ex)
        {
            watcher?.Dispose();
            _statusRegistry.MarkSubscribeFailed(channel, ex.Message);
            _logger.LogError(
                "[LOI] Channel '{Channel}' khong ton tai. Kiem tra ten channel dung bang lenh:\n" +
                "        wevtutil el | findstr /i \"{Channel}\"\n" +
                "        (Chi tiet: {Message})",
                channel, channel, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            watcher?.Dispose();
            _statusRegistry.MarkSubscribeFailed(channel, ex.Message);
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
            _statusRegistry.MarkSubscribeFailed(channel, ex.Message);
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
            _statusRegistry.MarkReadError(channel, e.EventException.Message);
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

            // Truyen 'record' = goi FormatDescription() ngay trong callback dong bo,
            // NGOAI LE CO CHU DICH cua quy uoc "khong lam viec nang o day" (CLAUDE.md):
            // record bi dispose khi thoat handler nen khong con co hoi nao khac, va
            // watcher chi nhan 15 Event ID luu luong thap. Mo rong sang channel luu
            // luong cao thi phai xem lai cho nay.
            var parsed = _parser.Parse(xml, record);
            parsed = parsed with { RiskLevel = _riskScorer.Score(parsed) };

            LogParsedEvent(parsed);
            _statusRegistry.MarkEventReceived(channel, record.RecordId);

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

