using System.Threading.Channels;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Hàng đợi trong bộ nhớ nối luồng NHẬN event với luồng GHI DB.
///
/// Lý do phải có: <c>EventLogWatcher.EventRecordWritten</c> là callback ĐỒNG BỘ do
/// Windows gọi. Nếu chặn nó để chờ ghi DB xong thì khi event dồn dập, luồng nhận
/// event sẽ nghẽn và có thể bỏ lỡ event. Ở đây handler chỉ đẩy vào hàng đợi rồi
/// trả về ngay; một BackgroundService khác đọc ra và ghi DB.
/// </summary>
public sealed class EventQueue
{
    private readonly Channel<WindowsMonitorEvent> _channel;
    private readonly ILogger<EventQueue> _logger;
    private int _droppedCount;

    public EventQueue(ILogger<EventQueue> logger, int capacity = 1000)
    {
        _logger = logger;

        // Chan tren de mot su co ghi DG keo dai khong an het RAM.
        _channel = Channel.CreateBounded<WindowsMonitorEvent>(
            new BoundedChannelOptions(capacity)
            {
                // Day tu nhieu watcher (mot cai moi channel), doc boi dung mot consumer.
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite
            });
    }

    public ChannelReader<WindowsMonitorEvent> Reader => _channel.Reader;

    /// <summary>
    /// Đẩy event vào hàng đợi, không bao giờ chặn luồng gọi.
    /// Trả về <c>false</c> khi hàng đợi đầy — khi đó event bị rớt và được log rõ ràng.
    /// </summary>
    public bool TryEnqueue(WindowsMonitorEvent evt)
    {
        if (_channel.Writer.TryWrite(evt))
        {
            return true;
        }

        // Rot event la mat du lieu giam sat - phai keu to, khong duoc im lang.
        var dropped = Interlocked.Increment(ref _droppedCount);
        _logger.LogWarning(
            "Hang doi day, ROT event {EventId} tu {Host} (tong so da rot: {Dropped}). " +
            "Ghi DB dang cham hoac DB khong ket noi duoc.",
            evt.EventId, evt.Hostname, dropped);

        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();
}
