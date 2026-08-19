using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using Microsoft.Extensions.Options;
using TaskServiceMonitor.Configuration;

namespace TaskServiceMonitor.Monitoring;

/// <summary>Ném ra khi tên file trỏ ra ngoài thư mục cho phép hoặc sai định dạng.</summary>
public sealed class UnsafeSavedLogNameException(string message) : Exception(message);

public sealed record SavedLogInfo(string FileName, long SizeBytes, DateTime CreatedUtc);

/// <param name="MessagesEmbedded">
/// false = chỉ xuất được phần event thuần. File vẫn dùng tốt trên MÁY NÀY (provider
/// còn cài) nhưng mang sang máy khác sẽ mất Description.
/// </param>
public sealed record SavedLogExportResult(SavedLogInfo File, bool MessagesEmbedded);

/// <summary>
/// Tương đương "Save All Events As…" / "Open Saved Log…" của Event Viewer.
///
/// Rào chống path traversal ở tầng server: tên file đến thẳng từ URL trình
/// duyệt vào một app chạy quyền Administrator, không ép về một thư mục cố định thì
/// <c>..\..\Windows\System32\...</c> là đường dẫn hợp lệ để đọc hoặc ghi đè.
/// </summary>
public sealed class SavedLogStore
{
    private readonly ILogger<SavedLogStore> _logger;

    public string RootDirectory { get; }

    public SavedLogStore(IOptions<EventLogOptions> options, ILogger<SavedLogStore> logger)
    {
        _logger = logger;
        RootDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, options.Value.SavedLogDirectory));
    }

    /// <summary>
    /// Ba lớp chặn chồng lên nhau, mỗi lớp bịt một kiểu lách: cắt thành phần thư mục,
    /// ép đuôi <c>.evtx</c>, rồi so đường dẫn tuyệt đối với thư mục gốc.
    /// </summary>
    public string Resolve(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new UnsafeSavedLogNameException("Thieu ten file.");
        }

        var bare = Path.GetFileName(fileName.Trim());

        if (string.IsNullOrWhiteSpace(bare) || bare != fileName.Trim())
        {
            throw new UnsafeSavedLogNameException(
                $"Ten file '{fileName}' khong hop le - chi nhan ten tran, khong nhan duong dan.");
        }

        if (!bare.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeSavedLogNameException($"Ten file '{fileName}' phai co duoi '.evtx'.");
        }

        var full = Path.GetFullPath(Path.Combine(RootDirectory, bare));

        if (!full.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeSavedLogNameException(
                $"Ten file '{fileName}' tro ra ngoai thu muc '{RootDirectory}'.");
        }

        return full;
    }

    /// <summary>Số record tối đa cho một lần "lưu event đang chọn".</summary>
    public const int MaxSelectedRecords = 200;

    /// <summary>
    /// XPath lọc đúng những bản ghi đang chọn. <c>EventRecordID</c> là số thứ tự DUY
    /// NHẤT trong một channel nên đây là cách chọn chính xác từng event.
    ///
    /// Có chặn trên: XPath quá dài sẽ bị Windows từ chối (giới hạn ~20 biểu thức cho
    /// XPath, nhưng chuỗi `or` dài vẫn chạy được tới vài trăm) — chặn ở
    /// <see cref="MaxSelectedRecords"/> để lỗi rơi vào chỗ báo được cho người dùng,
    /// thay vì bung ra từ tầng Win32.
    /// </summary>
    internal static string BuildRecordIdXPath(IReadOnlyCollection<long> recordIds)
    {
        if (recordIds is null || recordIds.Count == 0)
        {
            throw new ArgumentException("Chua chon event nao de luu.", nameof(recordIds));
        }

        if (recordIds.Count > MaxSelectedRecords)
        {
            throw new ArgumentException(
                $"Chon toi da {MaxSelectedRecords} event mot lan (dang chon {recordIds.Count}).",
                nameof(recordIds));
        }

        var conditions = string.Join(" or ", recordIds.Select(id => $"EventRecordID={id}"));
        return $"*[System[({conditions})]]";
    }

    /// <summary>Lưu đúng những event đang chọn ra <c>.evtx</c> — "Save Selected Events…".</summary>
    public SavedLogExportResult ExportSelected(
        string channel, IReadOnlyCollection<long> recordIds, string? requestedName) =>
        ExportCore(channel, BuildRecordIdXPath(recordIds), requestedName);

    public SavedLogExportResult Export(string channel, int? eventId, string? requestedName) =>
        ExportCore(channel, eventId is int id ? $"*[System[(EventID={id})]]" : "*", requestedName);

    private SavedLogExportResult ExportCore(string channel, string xpath, string? requestedName)
    {
        Directory.CreateDirectory(RootDirectory);

        var target = Resolve(BuildFileName(channel, requestedName));

        // API nem loi neu file dich da ton tai (khong tu ghi de).
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        var messagesEmbedded = true;

        try
        {
            // Uu tien ban "AndMessages": no nhung chuoi da render vao file nen mo lai
            // tren may khac van con Description.
            EventLogSession.GlobalSession.ExportLogAndMessages(
                channel, PathType.LogName, xpath, target,
                tolerateQueryErrors: true,
                CultureInfo.CurrentCulture);
        }
        catch (EventLogException ex) when (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            // DA GAP THAT tren may dev (Windows 11 ARM64): ExportLogAndMessages LUON nem
            // "The directory name is invalid." du culture nao, trong khi ExportLog thuan
            // chay ngon. API nay gom hai buoc - xuat event, roi nhung chuoi mo ta; buoc
            // mot da xong va file .evtx hop le (dieu kien 'when' kiem tra dung dieu do),
            // chi buoc hai hong. Nem loi ra se vut bo mot file dung.
            messagesEmbedded = false;
            _logger.LogWarning(
                "Khong nhung duoc chuoi mo ta vao '{File}' ({Message}). File .evtx van hop le; " +
                "doc lai tren may nay van co Description, mang sang may khac thi mat.",
                Path.GetFileName(target), ex.Message);
        }

        var info = new FileInfo(target);
        _logger.LogInformation(
            "Da luu channel '{Channel}' ra '{File}' ({Size} byte, nhung mo ta: {Embedded})",
            channel, info.Name, info.Length, messagesEmbedded);

        return new SavedLogExportResult(
            new SavedLogInfo(info.Name, info.Length, info.CreationTimeUtc),
            messagesEmbedded);
    }

    public IReadOnlyList<SavedLogInfo> List()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return [];
        }

        return new DirectoryInfo(RootDirectory)
            .GetFiles("*.evtx")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new SavedLogInfo(f.Name, f.Length, f.CreationTimeUtc))
            .ToList();
    }

    public bool Delete(string? fileName)
    {
        var target = Resolve(fileName);
        if (!File.Exists(target))
        {
            return false;
        }

        File.Delete(target);
        _logger.LogInformation("Da xoa file log '{File}'", Path.GetFileName(target));
        return true;
    }

    /// <summary>Bỏ ký tự cấm (dấu <c>/</c> trong tên channel) và gắn dấu thời gian.</summary>
    private static string BuildFileName(string channel, string? requestedName)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var bare = Path.GetFileName(requestedName.Trim());
            return bare.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase) ? bare : bare + ".evtx";
        }

        var safeChannel = string.Concat(
            channel.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

        return $"{safeChannel}-{DateTime.Now:yyyyMMdd-HHmmss}.evtx";
    }
}
