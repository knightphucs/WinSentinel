using TaskServiceMonitor.Monitoring;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Test cho đúng lỗi đã gặp thật: Log Summary báo "đã subscribe nhưng chưa có event"
/// dù event đã vào DB.
///
/// Nguyên nhân: <c>TrySubscribe</c> bật watcher rồi mới gọi <c>MarkSubscribed</c>, mà
/// hàm đó là phép GHI ĐÈ với <c>EventsReceived: 0</c>. Khi có cursor thì
/// <c>readExistingEvents = true</c> nên Windows bắn loạt event đọc bù ngay tại dòng
/// <c>Enabled = true</c> — số đếm vừa tăng bị xoá sạch.
/// </summary>
public class ChannelStatusRegistryTests
{
    [Fact]
    public void MarkSubscribed_SauKhiDaNhanEvent_KhongDuocXoaSoDem()
    {
        var registry = new ChannelStatusRegistry();

        // Mo phong dung thu tu gay loi: event doc bu ve TRUOC khi MarkSubscribed chay.
        registry.MarkEventReceived("System", recordId: 100);
        registry.MarkEventReceived("System", recordId: 101);
        registry.MarkSubscribed("System", resumeFromRecordId: 99, catchUpTargetRecordId: 150);

        var status = registry.All().Single();

        Assert.Equal(2, status.EventsReceived);
        Assert.NotNull(status.LastEventUtc);
        Assert.Equal(101, status.LastRecordId);

        // Cursor van phai duoc ghi nhan.
        Assert.Equal(99, status.ResumeFromRecordId);
        Assert.True(status.Subscribed);
    }

    [Fact]
    public void MarkSubscribed_LanDau_BatDauTuKhong()
    {
        var registry = new ChannelStatusRegistry();

        registry.MarkSubscribed("Security", resumeFromRecordId: null, catchUpTargetRecordId: null);

        var status = registry.All().Single();

        Assert.True(status.Subscribed);
        Assert.Equal(0, status.EventsReceived);
        Assert.Null(status.LastEventUtc);
        Assert.Null(status.ResumeFromRecordId);
        Assert.Null(status.LastRecordId);
    }

    [Fact]
    public void MarkEventReceived_DemDocBuTheoCatchUpTarget()
    {
        var registry = new ChannelStatusRegistry();
        registry.MarkSubscribed("System", resumeFromRecordId: 100, catchUpTargetRecordId: 110);

        registry.MarkEventReceived("System", recordId: 105); // <= 110 -> doc bu
        registry.MarkEventReceived("System", recordId: 110); // <= 110 -> doc bu
        registry.MarkEventReceived("System", recordId: 111); // > 110  -> realtime that

        var status = registry.All().Single();

        Assert.Equal(3, status.EventsReceived);
        Assert.Equal(2, status.CaughtUpCount);
        Assert.Equal(111, status.LastRecordId);
    }

    [Fact]
    public void MarkEventReceived_XoaLoiDocCu()
    {
        var registry = new ChannelStatusRegistry();
        registry.MarkSubscribed("System");
        registry.MarkReadError("System", "The handle is invalid.");

        registry.MarkEventReceived("System", recordId: 5);

        // Doc duoc event nghia la loi cu khong con dung nua.
        Assert.Null(registry.All().Single().Error);
    }

    [Fact]
    public void MarkSubscribeFailed_XoaHetTrangThaiCu()
    {
        var registry = new ChannelStatusRegistry();
        registry.MarkEventReceived("System", recordId: 1);

        registry.MarkSubscribeFailed("System", "Khong du quyen");

        var status = registry.All().Single();

        Assert.False(status.Subscribed);
        Assert.Equal("Khong du quyen", status.Error);
        Assert.Equal(0, status.EventsReceived);
    }
}
