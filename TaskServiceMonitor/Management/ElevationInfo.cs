using System.Security.Principal;

namespace TaskServiceMonitor.Management;

/// <summary>
/// Cho biết tiến trình có đang chạy quyền Administrator không.
///
/// Cần vì hầu hết thao tác ghi (tạo/xoá service, tạo task cho user khác) đòi quyền
/// admin. Báo trước cho UI biết để vô hiệu hoá nút kèm giải thích, thay vì để người
/// dùng bấm rồi nhận lỗi "Access is denied" khó hiểu.
/// </summary>
public static class ElevationInfo
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // Khong xac dinh duoc thi coi nhu khong co quyen - an toan hon.
            return false;
        }
    }

    public static string CurrentUserName()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
        catch
        {
            return "unknown";
        }
    }
}
