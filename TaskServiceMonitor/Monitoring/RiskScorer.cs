using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Chấm mức rủi ro cho event theo rule cố định (rule-based, không học máy).
/// Gọi ngay sau khi parse và TRƯỚC khi lưu DB, để mức rủi ro được lưu cùng event.
/// </summary>
public sealed class RiskScorer
{
    /// <summary>
    /// Thư mục mà binary của service hợp lệ gần như không bao giờ nằm trong.
    /// Service trỏ vào đây là dấu hiệu điển hình của malware persistence.
    /// </summary>
    private static readonly string[] SuspiciousPathFragments =
    [
        @"\Temp\",
        @"\AppData\"
    ];

    /// <summary>
    /// Tham số dòng lệnh hay gặp ở PowerShell bị lạm dụng: chạy ẩn cửa sổ và
    /// truyền lệnh đã mã hoá base64 để né việc bị đọc nội dung.
    /// </summary>
    /// <remarks>
    /// Lưu ý: "-enc" là chuỗi con của "-EncodedCommand", và về lý thuyết có thể
    /// khớp nhầm với thứ khác bắt đầu bằng "-enc" (ví dụ "-encoding"). Đã quét
    /// toàn bộ mẫu XML thật hiện có: không có dương tính giả. Giữ đúng rule đề bài.
    /// </remarks>
    private static readonly string[] SuspiciousCommandFragments =
    [
        "-enc",
        "-EncodedCommand",
        "-w hidden"
    ];

    public RiskLevel Score(WindowsMonitorEvent evt)
    {
        if (IsHighRisk(evt))
        {
            return RiskLevel.High;
        }

        // 4702 = task bi SUA. Nguy hiem hon tao moi vi ke tan cong co the chiem
        // mot task he thong san co, vua co san quyen vua it bi de y.
        if (evt.EventId == 4702)
        {
            return RiskLevel.Medium;
        }

        return RiskLevel.Low;
    }

    private static bool IsHighRisk(WindowsMonitorEvent evt)
    {
        if (evt.ObjectType == MonitoredObjectType.Service &&
            evt.ImagePath is not null &&
            SuspiciousPathFragments.Any(fragment =>
                evt.ImagePath.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Quet ca RawXml chu khong chi TaskCommand: tham so dang ngo co the nam o
        // Arguments, o ImagePath cua service, hoac o cho khac tuy Event ID.
        return SuspiciousCommandFragments.Any(fragment =>
            evt.RawXml.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
