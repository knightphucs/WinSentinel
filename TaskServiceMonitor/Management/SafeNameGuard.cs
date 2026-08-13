namespace TaskServiceMonitor.Management;

/// <summary>Ném ra khi thao tác ghi nhắm vào đối tượng nằm ngoài phạm vi cho phép.</summary>
public sealed class UnsafeTargetException(string message) : Exception(message);

/// <summary>
/// Nơi DUY NHẤT quyết định tên task/service nào được phép tạo/sửa/xoá.
///
/// Vì sao cần: web UI này chạy quyền Administrator (bắt buộc để đọc channel Security).
/// Không có rào thì một cú bấm nhầm trên trình duyệt có thể xoá mất service hệ thống
/// và làm hỏng máy. Chặn ở tầng server chứ không chỉ ẩn nút trên giao diện.
///
/// Đọc thì không giới hạn — chỉ chặn đường ghi.
/// </summary>
public sealed class SafeNameGuard(string writablePrefix)
{
    public string WritablePrefix { get; } = string.IsNullOrWhiteSpace(writablePrefix)
        ? throw new ArgumentException("Tien to an toan khong duoc de rong.", nameof(writablePrefix))
        : writablePrefix;

    /// <summary>Tên này có được phép ghi không (không ném exception).</summary>
    public bool IsWritable(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // Task Scheduler dung duong dan dang '\Folder\Ten'. Bo dau '\' dau de
        // '\WinSentinelDemo' va 'WinSentinelDemo' duoc doi xu nhu nhau.
        var candidate = name.TrimStart('\\');

        // Chan meo dung '..' de nhay ra khoi pham vi, vi du 'WinSentinel\..\Spooler'.
        if (candidate.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        // Khong cho tao trong thu muc con: chi chap nhan ten phang o goc.
        if (candidate.Contains('\\', StringComparison.Ordinal) ||
            candidate.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        return candidate.StartsWith(WritablePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Bắt buộc tên phải hợp lệ, không thì ném <see cref="UnsafeTargetException"/>.</summary>
    public void EnsureWritable(string? name)
    {
        if (IsWritable(name))
        {
            return;
        }

        throw new UnsafeTargetException(
            $"Khong duoc phep thao tac ghi len '{name}'. " +
            $"Chi cho phep doi tuong co ten bat dau bang '{WritablePrefix}' va nam o thu muc goc. " +
            "Rao nay de mot cu bam nham khong xoa mat task/service that cua he thong. " +
            "Doi tien to trong appsettings.json muc 'Management:WritablePrefix' neu can.");
    }
}
