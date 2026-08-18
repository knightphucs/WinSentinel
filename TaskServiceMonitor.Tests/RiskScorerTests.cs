using TaskServiceMonitor.Models;
using TaskServiceMonitor.Monitoring;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Từ bước 11, <see cref="RiskScorer"/> chỉ còn là lớp mỏng uỷ quyền cho
/// <c>Detection.RuleCatalog</c> (lấy mức cao nhất trong các rule khớp). Test ở đây giữ
/// nguyên vai trò cũ: khoá lại HÀNH VI CHẤM ĐIỂM nhìn từ ngoài vào — mẫu thật nào ra
/// mức nào — còn chi tiết từng rule thì ở <c>RuleCatalogTests</c>.
/// </summary>
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

    /// <summary>
    /// Nhom "ghi nhan hanh vi": tao/xoa/bat-tat task, cai service. Mentor CO liet ke
    /// chung nen chung phai sinh canh bao, nhung o muc Low de khong lam ngap dashboard.
    /// </summary>
    [Theory]
    [InlineData("4698_task_created_exec.xml")]
    [InlineData("4699_task_deleted.xml")]
    [InlineData("4700_task_enabled.xml")]
    [InlineData("7045_service_installed_system.xml")]
    [InlineData("4697_service_installed_security.xml")]
    public void Score_EventBinhThuong_TraVeLow(string fixture)
    {
        Assert.Equal(RiskLevel.Low, _scorer.Score(Parse(fixture)));
    }

    /// <summary>
    /// ĐỔI Ở BƯỚC 11 (trước đây kỳ vọng Low): 7040 nay khớp rule
    /// SERVICE_STARTTYPE_CHANGED -> Medium. Mentor liet ke "thay doi cau hinh service"
    /// la hanh vi can theo doi, nen no phai noi len chu khong the nam im o Low.
    ///
    /// CO Y chi Medium chu khong High du mau nay la 'demand start' -> 'auto start':
    /// BITS doi qua lai lien tuc, cham High la tu lam ngap tab Canh bao.
    /// </summary>
    [Fact]
    public void Score_7040DoiStartType_TraVeMedium()
    {
        Assert.Equal(RiskLevel.Medium, _scorer.Score(Parse("7040_service_starttype_changed.xml")));
    }

    /// <summary>
    /// Khoa lai ket qua da kiem chung bang tay: KHONG mau that nao bi cham nham thanh
    /// High. Neu ai noi long rule den muc gay duong tinh gia tren du lieu binh thuong,
    /// test nay se do. Day la test QUAN TRONG NHAT cua tang phat hien.
    /// </summary>
    [Theory]
    [InlineData("4697_service_installed_security.xml")]
    [InlineData("4698_task_created_exec.xml")]
    [InlineData("4699_task_deleted.xml")]
    [InlineData("4700_task_enabled.xml")]
    [InlineData("4701_task_disabled_comhandler.xml")]
    [InlineData("4701_task_disabled_exec.xml")]
    [InlineData("4702_task_updated_comhandler.xml")]
    [InlineData("7040_service_starttype_changed.xml")]
    [InlineData("7045_service_installed_system.xml")]
    [InlineData("106_task_registered_operational.xml")]
    [InlineData("140_task_updated_operational.xml")]
    [InlineData("141_task_deleted_operational.xml")]
    [InlineData("200_task_action_started.xml")]
    [InlineData("201_task_action_completed.xml")]
    public void Score_MauThat_KhongCoDuongTinhGia(string fixture)
    {
        Assert.NotEqual(RiskLevel.High, _scorer.Score(Parse(fixture)));
    }

    // ---------------------------------------------------------------- Nhanh High

    private static WindowsMonitorEvent Fake(
        int eventId = 7045,
        MonitoredObjectType objectType = MonitoredObjectType.Service,
        string? imagePath = null,
        string? taskCommand = null,
        string rawXml = "<Event />") => new()
        {
            EventId = eventId,
            Hostname = "MAY-TEST",
            TimeCreated = DateTime.UtcNow,
            ObjectType = objectType,
            ActionDescription = "test",
            ImagePath = imagePath,
            TaskCommand = taskCommand,
            Channel = "System",
            ProviderName = "test",
            Data = new Dictionary<string, string>(),
            RawXml = rawXml
        };

    [Theory]
    [InlineData(@"C:\Users\Kazyy\AppData\Local\fake.exe")]
    [InlineData(@"C:\Windows\Temp\payload.exe")]
    [InlineData(@"c:\windows\temp\payload.exe")]   // khong phan biet hoa thuong
    [InlineData(@"C:\Users\Public\payload.exe")]   // them o buoc 11
    public void Score_ServiceChayTuThuMucDangNgo_TraVeHigh(string imagePath)
    {
        Assert.Equal(RiskLevel.High, _scorer.Score(Fake(imagePath: imagePath)));
    }

    /// <summary>
    /// Luoi an toan: dau hieu nam trong RawXml nhung khong o field da parse van phai
    /// bat duoc. Day la hanh vi cua RiskScorer TRUOC buoc 11, co y giu lai - bo di la
    /// mot buoc lui (xem RuleCatalog.EvaluateSuspiciousRawContent).
    /// </summary>
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
    /// ĐỔI Ở BƯỚC 11 — day la LO HONG da va, khong phai thay doi tuy hung.
    ///
    /// Truoc buoc 11, rule duong dan CHI ap cho Service, nen mot task tro vao AppData
    /// van cham Low va co han mot test khoa lai hanh vi do. Nhung mentor yeu cau ro
    /// "Task thuc thi tu thu muc bat thuong (%TEMP%, C:\Users\Public)" - nen rule
    /// duong dan bay gio ap cho CA task, doc tu TaskCommand.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\fake.exe")]
    [InlineData(@"C:\Users\Public\evil.cmd")]
    [InlineData(@"%TEMP%\dropper.exe")]            // dang bien moi truong nguyen van
    public void Score_TaskChayTuThuMucGhiDuoc_TraVeHigh(string command)
    {
        var task = Fake(
            eventId: 4698,
            objectType: MonitoredObjectType.ScheduledTask,
            taskCommand: command);

        Assert.Equal(RiskLevel.High, _scorer.Score(task));
    }

    /// <summary>Task chay quyen cao -> Medium (chua du de len High neu lenh sach).</summary>
    [Fact]
    public void Score_TaskChayQuyenCao_TraVeMedium()
    {
        var task = Fake(eventId: 4698, objectType: MonitoredObjectType.ScheduledTask) with
        {
            TaskRunLevel = "HighestAvailable",
            TaskCommand = @"C:\Windows\System32\cmd.exe"
        };

        Assert.Equal(RiskLevel.Medium, _scorer.Score(task));
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
