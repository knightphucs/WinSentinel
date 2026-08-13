namespace TaskServiceMonitor.Configuration;

/// <summary>
/// Cấu hình đọc từ section "EventLog" trong appsettings.json.
/// </summary>
public sealed record EventLogOptions
{
    public const string SectionName = "EventLog";

    /// <summary>
    /// Channel dùng khi cấu hình không khai báo gì.
    /// </summary>
    public static readonly string[] DefaultChannels = ["System"];

    /// <summary>
    /// Danh sách channel cần subscribe. Mỗi channel dùng một EventLogWatcher riêng
    /// vì một watcher chỉ subscribe được đúng một channel.
    /// Local test: ["System"] (không cần admin) hoặc ["System", "Security"] (cần admin).
    /// Khi có Windows Event Forwarding: đổi thành ["ForwardedEvents"].
    /// </summary>
    /// <remarks>
    /// CỐ Ý để mặc định rỗng, KHÔNG đặt ["System"] ở đây. ConfigurationBinder của
    /// .NET khi bind mảng sẽ NỐI THÊM vào giá trị sẵn có chứ không ghi đè — để mặc
    /// định ["System"] thì config ["System","Security"] sẽ ra ["System","System","Security"].
    /// Giá trị mặc định được áp ở <see cref="EffectiveChannels"/>.
    /// </remarks>
    public string[] Channels { get; init; } = [];

    /// <summary>
    /// Danh sách channel thực dùng: lấy từ cấu hình, loại trùng (không phân biệt hoa
    /// thường); nếu cấu hình rỗng thì rơi về <see cref="DefaultChannels"/>.
    /// </summary>
    public string[] EffectiveChannels =>
        Channels.Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .DefaultIfEmpty(DefaultChannels[0])
                .ToArray();

    /// <summary>
    /// true = đọc luôn các event đã có sẵn trong log khi khởi động.
    /// Để false cho đúng nghĩa "realtime"; bật tạm true nếu muốn lấy mẫu XML nhanh.
    /// </summary>
    public bool ReadExistingEvents { get; init; }

    /// <summary>Thư mục lưu mẫu XML thô (tương đối so với thư mục chạy app).</summary>
    public string SampleDirectory { get; init; } = "samples";

    /// <summary>
    /// Số mẫu tối đa lưu cho mỗi Event ID. Cần thiết vì 7036 bắn rất dày
    /// (mỗi lần service đổi state), không giới hạn sẽ ngập đĩa và nhiễu console.
    /// </summary>
    public int MaxSamplesPerEventId { get; init; } = 5;
}
