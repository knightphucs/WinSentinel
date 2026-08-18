namespace TaskServiceMonitor.Configuration;

/// <summary>
/// Cấu hình đọc từ section "Alerting" trong appsettings.json.
/// </summary>
public sealed record AlertingOptions
{
    public const string SectionName = "Alerting";

    /// <summary>
    /// Cửa sổ thời gian cho rule tương quan <c>TASK_CREATE_THEN_DELETE</c>: task được
    /// tạo rồi bị xoá trong vòng bấy nhiêu phút thì coi là "chạy một lần rồi dọn dấu vết".
    /// </summary>
    public int CorrelationWindowMinutes { get; init; } = 10;

    /// <summary>
    /// Số lần một task đã từng bị xoá trước đó mà vượt quá thì <c>TASK_CREATE_THEN_DELETE</c>
    /// coi đó là **thói quen của phần mềm**, không phải sự cố, và thôi cảnh báo.
    ///
    /// ĐO TRÊN DỮ LIỆU THẬT (15.059 event): không có ngưỡng này thì rule sinh 4.419
    /// cảnh báo High, trong đó <b>4.415 đến từ đúng hai task</b> của driver âm thanh
    /// Nahimic (<c>NahimicTask32</c>/<c>NahimicTask64</c>) vốn tự tạo rồi tự xoá liên
    /// tục. Một cái tự dọn dấu vết thật thì chỉ làm vài lần, không làm hàng nghìn lần.
    /// </summary>
    public int CreateDeleteRepeatThreshold { get; init; } = 3;

    /// <summary>
    /// Cửa sổ để nâng <c>SERVICE_CRASH</c> lên High khi service vừa bị cài hoặc sửa
    /// ngay trước lúc crash.
    /// </summary>
    public int ServiceChangeLookbackHours { get; init; } = 24;

    /// <summary>
    /// Bật <c>ServiceConfigWatcher</c> — vòng poll so cấu hình service để bắt việc đổi
    /// binPath / đổi tài khoản, thứ mà SCM KHÔNG phát event
    /// (xem docs/hanh-vi-mapping.md mục 3.1).
    /// </summary>
    public bool ServiceConfigPollEnabled { get; init; } = true;

    /// <summary>
    /// Chu kỳ poll cấu hình service, tính bằng giây. Mỗi vòng tốn khoảng một lời gọi
    /// <c>QueryServiceConfig</c> cho mỗi service (~200 lời gọi trên máy Windows thường).
    /// </summary>
    public int ServiceConfigPollSeconds { get; init; } = 60;

    /// <summary>
    /// Mức tối thiểu để đẩy cảnh báo lên trình duyệt theo thời gian thực. Cảnh báo
    /// dưới mức này vẫn được lưu và vẫn xem được ở tab Cảnh báo, chỉ không bật banner.
    /// Mặc định Medium: rule mức Low là loại "ghi nhận hành vi", bật banner cho chúng
    /// thì banner chạy liên tục và mất hết ý nghĩa.
    /// </summary>
    public string MinimumBroadcastSeverity { get; init; } = "Medium";
}
