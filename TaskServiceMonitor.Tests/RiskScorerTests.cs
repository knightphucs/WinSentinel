using TaskServiceMonitor.Models;
using TaskServiceMonitor.Monitoring;
using Xunit;

namespace TaskServiceMonitor.Tests;

public class RiskScorerTests
{
    private readonly RiskScorer _scorer = new();
    private readonly WindowsEventParser _parser = new();

    private WindowsMonitorEvent Parse(string fixture) => _parser.Parse(SampleXml.Load(fixture));

    // ---------------------------------------------------------------- Tren mau that

    /// <summary>
    /// 4702 = task bi SUA -> Medium. Day la event nhay cam nhat trong nhom task
    /// vi ke tan cong co the chiem mot task he thong san co.
    /// </summary>
    [Fact]
    public void Score_4702_TraVeMedium()
    {
        Assert.Equal(RiskLevel.Medium, _scorer.Score(Parse("4702_task_updated_comhandler.xml")));
    }

    [Theory]
    [InlineData("4698_task_created_exec.xml")]
    [InlineData("4699_task_deleted.xml")]
    [InlineData("4700_task_enabled.xml")]
    [InlineData("7045_service_installed_system.xml")]
    [InlineData("7040_service_starttype_changed.xml")]
    [InlineData("4697_service_installed_security.xml")]
    public void Score_EventBinhThuong_TraVeLow(string fixture)
    {
        Assert.Equal(RiskLevel.Low, _scorer.Score(Parse(fixture)));
    }

    /// <summary>
    /// Khoa lai ket qua da kiem chung bang tay: KHONG mau that nao trong 17 mau
    /// bi cham nham thanh High. Neu ai noi long rule den muc gay duong tinh gia
    /// tren du lieu binh thuong, test nay se do.
    /// </summary>
    [Theory]
    [InlineData("4698_task_created_exec.xml")]
    [InlineData("4701_task_disabled_comhandler.xml")]
    [InlineData("4702_task_updated_comhandler.xml")]
    [InlineData("7045_service_installed_system.xml")]
    [InlineData("4697_service_installed_security.xml")]
    public void Score_MauThat_KhongCoDuongTinhGia(string fixture)
    {
        Assert.NotEqual(RiskLevel.High, _scorer.Score(Parse(fixture)));
    }

    // ---------------------------------------------------------------- Nhanh High

    private static WindowsMonitorEvent Fake(
        int eventId = 7045,
        MonitoredObjectType objectType = MonitoredObjectType.Service,
        string? imagePath = null,
        string rawXml = "<Event />") => new()
        {
            EventId = eventId,
            Hostname = "MAY-TEST",
            TimeCreated = DateTime.UtcNow,
            ObjectType = objectType,
            ActionDescription = "test",
            ImagePath = imagePath,
            Channel = "System",
            ProviderName = "test",
            Data = new Dictionary<string, string>(),
            RawXml = rawXml
        };

    [Theory]
    [InlineData(@"C:\Users\Kazyy\AppData\Local\fake.exe")]
    [InlineData(@"C:\Windows\Temp\payload.exe")]
    [InlineData(@"c:\windows\temp\payload.exe")]   // khong phan biet hoa thuong
    public void Score_ServiceChayTuThuMucDangNgo_TraVeHigh(string imagePath)
    {
        Assert.Equal(RiskLevel.High, _scorer.Score(Fake(imagePath: imagePath)));
    }

    [Theory]
    [InlineData("powershell.exe -enc SQBuAHYAbwBrAGUA")]
    [InlineData("powershell.exe -EncodedCommand SQBuAHYA")]
    [InlineData("powershell.exe -w hidden -nop")]
    [InlineData("POWERSHELL.EXE -W HIDDEN")]       // khong phan biet hoa thuong
    public void Score_ThamSoDongLenhDangNgoTrongRawXml_TraVeHigh(string command)
    {
        var rawXml = $"<Event><EventData><Data>{command}</Data></EventData></Event>";
        Assert.Equal(RiskLevel.High, _scorer.Score(Fake(rawXml: rawXml)));
    }

    /// <summary>
    /// Rule duong dan chi ap dung cho Service. Task co duong dan tuong tu van phai
    /// duoc bat qua nhanh RawXml chu khong qua nhanh ImagePath.
    /// </summary>
    [Fact]
    public void Score_TaskCoImagePathDangNgo_KhongTinhTheoNhanhDuongDan()
    {
        var task = Fake(
            eventId: 4698,
            objectType: MonitoredObjectType.ScheduledTask,
            imagePath: @"C:\Users\x\AppData\Local\fake.exe");

        // RawXml sach -> khong co gi kich hoat nhanh High.
        Assert.Equal(RiskLevel.Low, _scorer.Score(task));
    }

    /// <summary>High phai thang Medium khi mot event thoa ca hai.</summary>
    [Fact]
    public void Score_VuaLa4702_VuaDangNgo_TraVeHigh()
    {
        var evt = Fake(
            eventId: 4702,
            objectType: MonitoredObjectType.ScheduledTask,
            rawXml: "<Event>powershell -w hidden</Event>");

        Assert.Equal(RiskLevel.High, _scorer.Score(evt));
    }
}
