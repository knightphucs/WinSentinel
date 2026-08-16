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
    [Fact]
    public void BuildXPathFilter_KhongCoCursor_GiuNguyenFormatCu()
    {
        var xpath = MonitoredEventIds.BuildXPathFilter("System");

        Assert.Equal("*[System[(EventID=7045 or EventID=7040 or EventID=7036 or EventID=7034)]]", xpath);
    }

    [Fact]
    public void BuildXPathFilter_CoCursor_ThemDieuKienEventRecordId()
    {
        var xpath = MonitoredEventIds.BuildXPathFilter("System", 12345);

        Assert.Equal(
            "*[System[(EventID=7045 or EventID=7040 or EventID=7036 or EventID=7034) and (EventRecordID>12345)]]",
            xpath);
    }

    [Fact]
    public void BuildXPathFilter_ChannelChuaKhaiBao_RoiVeAll_VanApDungCursor()
    {
        var xpath = MonitoredEventIds.BuildXPathFilter("Application", 5);

        Assert.Contains("and (EventRecordID>5)", xpath);
        Assert.StartsWith("*[System[(EventID=", xpath);
    }
}
