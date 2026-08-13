using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TaskServiceMonitor.Configuration;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Ghi XML thô của event ra file để làm mẫu cho bước 2 (viết WindowsEventParser).
/// Giới hạn số mẫu mỗi Event ID để không ngập đĩa vì các event bắn dày như 7036.
/// Singleton + thread-safe: handler của nhiều EventLogWatcher có thể gọi đồng thời.
/// </summary>
public sealed class RawXmlSampleWriter
{
    private readonly ILogger<RawXmlSampleWriter> _logger;
    private readonly EventLogOptions _options;
    private readonly string _sampleDirectory;
    private readonly ConcurrentDictionary<int, int> _countByEventId = new();
    private int _directoryCreated;

    public RawXmlSampleWriter(
        IOptions<EventLogOptions> options,
        IHostEnvironment environment,
        ILogger<RawXmlSampleWriter> logger)
    {
        _logger = logger;
        _options = options.Value;

        // Neo vao ContentRootPath chu KHONG phai thu muc lam viec hien tai: chay bang
        // `dotnet run` thi CWD la thu muc project, chay thang file .exe thi CWD la
        // thu muc bin -> mau XML se nam o hai noi khac nhau, rat de nham.
        _sampleDirectory = Path.IsPathRooted(_options.SampleDirectory)
            ? _options.SampleDirectory
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, _options.SampleDirectory));
    }

    public string SampleDirectory => _sampleDirectory;

    /// <summary>
    /// Lưu một mẫu XML nếu Event ID đó chưa đủ số mẫu tối đa.
    /// </summary>
    public void TrySave(int eventId, long? recordId, string xml)
    {
        // Tăng trước rồi kiểm tra, để nhiều thread không cùng vượt hạn mức.
        var count = _countByEventId.AddOrUpdate(eventId, 1, static (_, current) => current + 1);
        if (count > _options.MaxSamplesPerEventId)
        {
            return;
        }

        try
        {
            EnsureDirectory();

            var fileName = $"{eventId}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{recordId?.ToString() ?? "na"}.xml";
            var fullPath = Path.Combine(_sampleDirectory, fileName);
            File.WriteAllText(fullPath, xml);

            _logger.LogInformation("Da luu mau XML: {FileName} ({Count}/{Max})",
                fileName, count, _options.MaxSamplesPerEventId);

            if (count == _options.MaxSamplesPerEventId)
            {
                _logger.LogInformation(
                    "Da du {Max} mau cho EventId {EventId} - cac event {EventId} sau se khong luu file nua (van in ra console).",
                    _options.MaxSamplesPerEventId, eventId, eventId);
            }
        }
        catch (Exception ex)
        {
            // Ghi file lỗi không được phép làm chết luồng nhận event.
            _logger.LogError(ex, "Khong ghi duoc mau XML cho EventId {EventId} vao {Directory}",
                eventId, _sampleDirectory);
        }
    }

    private void EnsureDirectory()
    {
        // CreateDirectory idempotent va re - goi moi lan de tranh race giua cac thread
        // (neu chi dua vao co _directoryCreated, thread thu 2 co the ghi file truoc khi
        // thread thu 1 kip tao thu muc).
        Directory.CreateDirectory(_sampleDirectory);

        if (Interlocked.Exchange(ref _directoryCreated, 1) == 0)
        {
            _logger.LogInformation("Thu muc luu mau XML: {Directory}", _sampleDirectory);
        }
    }
}
