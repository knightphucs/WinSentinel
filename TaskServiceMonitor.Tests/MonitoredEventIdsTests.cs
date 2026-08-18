using TaskServiceMonitor.Monitoring;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Test riêng cho <see cref="MonitoredEventIds.BuildXPathFilter"/> — nhất là tham số
/// <c>afterRecordId</c> (cơ chế "resume sau restart" bằng XPath filter EventRecordID,
/// xem EventWatcherService). Không đụng DB/WinAPI thật nên chạy được trên mọi máy.
/// </summary>
public class MonitoredEventIdsTests
{
    /// <summary>
    /// Nhóm ID của channel System, đúng thứ tự khai báo trong
    /// <see cref="MonitoredEventIds.ByChannel"/>. Bước 11 thêm 7031/7024/7000/7009 —
    /// nhóm "service dừng bất thường" mà mentor yêu cầu (7034 không bao giờ phát trên
    /// máy dev, xem docs/hanh-vi-mapping.md mục 3.2).
    /// </summary>
    private const string SystemIds =
        "EventID=7045 or EventID=7040 or EventID=7036 or EventID=7034 or " +
        "EventID=7031 or EventID=7024 or EventID=7000 or EventID=7009";

    [Fact]
    public void BuildXPathFilter_KhongCoCursor_GiuNguyenFormatCu()
    {
        var xpath = MonitoredEventIds.BuildXPathFilter("System");

        Assert.Equal($"*[System[({SystemIds})]]", xpath);
    }

    [Fact]
    public void BuildXPathFilter_CoCursor_ThemDieuKienEventRecordId()
    {
        var xpath = MonitoredEventIds.BuildXPathFilter("System", 12345);

        Assert.Equal($"*[System[({SystemIds}) and (EventRecordID>12345)]]", xpath);
    }

    /// <summary>
    /// 4657 phải nằm trong filter của channel Security. Đây là đường DUY NHẤT có log
    /// Windows thật cho việc đổi binPath / đổi tài khoản service — SCM không phát event
    /// nào cho hai hành vi đó.
    /// </summary>
    [Fact]
    public void BuildXPathFilter_Security_CoCa4657ChoRegistry()
    {
        var xpath = MonitoredEventIds.BuildXPathFilter("Security");

        Assert.Contains("EventID=4657", xpath);
        Assert.Contains("EventID=4698", xpath);
    }

    [Fact]
    public void BuildXPathFilter_ChannelChuaKhaiBao_RoiVeAll_VanApDungCursor()
    {
        var xpath = MonitoredEventIds.BuildXPathFilter("Application", 5);

        Assert.Contains("and (EventRecordID>5)", xpath);
        Assert.StartsWith("*[System[(EventID=", xpath);
    }
}
