namespace TaskServiceMonitor.Models;

/// <summary>
/// Ảnh chụp cấu hình của một service tại một thời điểm — mốc để
/// <c>ServiceConfigWatcher</c> so lệch.
///
/// Vì sao cần bảng này: SCM <b>không phát event</b> khi <c>binPath</c> hay tài khoản
/// khởi chạy của service bị đổi (event 7040 chỉ báo start type). Không có log để
/// nghe thì phải tự chụp trạng thái rồi so — xem docs/hanh-vi-mapping.md mục 3.1.
///
/// Lưu xuống DB chứ không giữ trong bộ nhớ để restart app không mất mốc so sánh,
/// cùng tinh thần với cursor <c>RecordId</c> ở bước 7.
/// </summary>
public sealed record ServiceConfigSnapshot
{
    /// <summary>Máy chụp snapshot. Cùng với <see cref="ServiceName"/> tạo thành khoá chính.</summary>
    public required string Hostname { get; init; }

    /// <summary>Tên ngắn của service (<c>Spooler</c>), không phải tên hiển thị.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Nguyên văn <c>lpBinaryPathName</c> — CẢ dòng lệnh, không chỉ đường dẫn.</summary>
    public string? ImagePath { get; init; }

    /// <summary><c>lpServiceStartName</c>, ví dụ <c>LocalSystem</c>.</summary>
    public string? Account { get; init; }

    /// <summary>Start type đã đổi sang chữ, ví dụ <c>auto start</c>.</summary>
    public string? StartType { get; init; }

    /// <summary>Lúc chụp. Luôn UTC.</summary>
    public required DateTime CapturedAt { get; init; }
}
