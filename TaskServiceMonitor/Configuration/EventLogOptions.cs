namespace TaskServiceMonitor.Configuration;

/// <summary>
/// Cấu hình đọc từ section "EventLog" trong appsettings.json.
/// </summary>
public sealed record EventLogOptions
{
    public const string SectionName = "EventLog";

    public static readonly string[] DefaultChannels = ["System"];

    /// <summary>
    /// Channel cần subscribe (mỗi channel một EventLogWatcher riêng). Khi có Windows
    /// Event Forwarding: đổi thành <c>["ForwardedEvents"]</c>.
    /// </summary>
    /// <remarks>
    /// CỐ Ý để mặc định rỗng: ConfigurationBinder khi bind mảng sẽ NỐI THÊM chứ không
    /// ghi đè — để mặc định ["System"] thì config ["System","Security"] sẽ ra
    /// ["System","System","Security"]. Mặc định áp ở <see cref="EffectiveChannels"/>.
    /// </remarks>
    public string[] Channels { get; init; } = [];

    public string[] EffectiveChannels =>
        Channels.Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .DefaultIfEmpty(DefaultChannels[0])
                .ToArray();

    /// <summary>true = đọc luôn event đã có sẵn trong log khi khởi động.</summary>
    public bool ReadExistingEvents { get; init; }

    public string SampleDirectory { get; init; } = "samples";

    /// <summary>
    /// Thư mục chứa file <c>.evtx</c> của "Saved Events". Cũng là rào an toàn: mọi
    /// thao tác đọc/xoá/tải đều bị <c>SavedLogStore</c> ép về đúng thư mục này.
    /// </summary>
    public string SavedLogDirectory { get; init; } = "saved-logs";

    /// <summary>Cần giới hạn vì 7036 bắn rất dày, không chặn sẽ ngập đĩa.</summary>
    public int MaxSamplesPerEventId { get; init; } = 5;
}
