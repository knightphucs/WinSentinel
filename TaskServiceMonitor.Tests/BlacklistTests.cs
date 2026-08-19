using TaskServiceMonitor.Detection;
using TaskServiceMonitor.Models;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Test cho tầng blacklist (bước 14): so khớp và — quan trọng hơn — ĐIỀU KIỆN HỌC.
///
/// Phần học được test kỹ hơn phần so khớp là có chủ đích: học sai một lần thì dấu
/// hiệu đó nằm lại trong DB và bắn cảnh báo High mãi mãi, trong khi so sai chỉ ảnh
/// hưởng một event.
/// </summary>
public class BlacklistTests
{
    private static BlacklistEntry Entry(
        BlacklistKind kind,
        string value,
        bool enabled = true,
        RiskLevel severity = RiskLevel.High) => new()
        {
            Kind = kind,
            Value = BlacklistMatcher.Normalize(value),
            Severity = severity,
            Source = BlacklistSource.Manual,
            Enabled = enabled,
            CreatedAt = DateTime.UtcNow
        };

    private static WindowsMonitorEvent TaskEvent(
        string? command = null,
        string? arguments = null,
        string? runAsUser = null,
        int eventId = 4698) => new()
        {
            EventId = eventId,
            Hostname = "MAY-TEST",
            TimeCreated = DateTime.UtcNow,
            ObjectType = MonitoredObjectType.ScheduledTask,
            ObjectName = @"\WinSentinelTest",
            ActionDescription = "test",
            TaskCommand = command,
            TaskArguments = arguments,
            TaskRunAsUser = runAsUser,
            Channel = "Security",
            ProviderName = "test",
            Data = new Dictionary<string, string>(),
            RawXml = "<Event />"
        };

    private static WindowsMonitorEvent ServiceEvent(string? imagePath = null, string? account = null) => new()
    {
        EventId = 7045,
        Hostname = "MAY-TEST",
        TimeCreated = DateTime.UtcNow,
        ObjectType = MonitoredObjectType.Service,
        ObjectName = "WinSentinelSvc",
        ActionDescription = "test",
        ImagePath = imagePath,
        ServiceAccount = account,
        Channel = "System",
        ProviderName = "test",
        Data = new Dictionary<string, string>(),
        RawXml = "<Event />"
    };

    // ============================================================ So khớp

    [Fact]
    public void DuongDan_KhopChinhXac()
    {
        var entries = new[] { Entry(BlacklistKind.ExecutablePath, @"C:\Users\Public\evil.exe") };

        var matches = BlacklistMatcher.Match(
            TaskEvent(command: @"C:\Users\Public\evil.exe"), entries);

        Assert.Single(matches);
        Assert.Equal("lệnh của task", matches[0].MatchedIn);
    }

    /// <summary>
    /// Đường dẫn so BẰNG NHAU, không phải chuỗi con. Nếu dùng <c>Contains</c> thì một
    /// dòng <c>c:\a.exe</c> sẽ khớp luôn <c>c:\a.exe.bak</c> — hai file khác nhau.
    /// </summary>
    [Fact]
    public void DuongDan_KhongKhopKieuChuoiCon()
    {
        var entries = new[] { Entry(BlacklistKind.ExecutablePath, @"C:\Users\Public\evil.exe") };

        var matches = BlacklistMatcher.Match(
            TaskEvent(command: @"C:\Users\Public\evil.exe.bak"), entries);

        Assert.Empty(matches);
    }

    [Fact]
    public void DuongDan_KhongPhanBietHoaThuong()
    {
        var entries = new[] { Entry(BlacklistKind.ExecutablePath, @"C:\Users\Public\Evil.exe") };

        var matches = BlacklistMatcher.Match(
            TaskEvent(command: @"c:\users\public\EVIL.EXE"), entries);

        Assert.Single(matches);
    }

    /// <summary>Đường dẫn có tham số phía sau vẫn phải bóc ra đúng phần exe.</summary>
    [Fact]
    public void DuongDan_BocKhoiThamSo()
    {
        var entries = new[] { Entry(BlacklistKind.ExecutablePath, @"C:\Users\Public\evil.exe") };

        var matches = BlacklistMatcher.Match(
            ServiceEvent(imagePath: @"""C:\Users\Public\evil.exe"" -k netsvcs"), entries);

        Assert.Single(matches);
        Assert.Equal("ImagePath của service", matches[0].MatchedIn);
    }

    /// <summary>
    /// 🪤 BUG THẬT, bắt được bằng test end-to-end trên 200 event thật.
    ///
    /// <c>ExtractExecutable</c> từng cắt tại dấu cách đầu tiên, nên đường dẫn KHÔNG có
    /// nháy mà chứa khoảng trắng — dạng cực phổ biến vì <c>Program Files</c> có dấu
    /// cách — bị cắt thành <c>C:\Program</c> và mọi so khớp đều trượt:
    /// <c>MicrosoftEdgeUpdate.exe</c> xuất hiện 8 lần trong 200 event gần nhất mà
    /// blacklist khớp 0 dòng.
    ///
    /// Ảnh hưởng rộng hơn blacklist: <c>MatchLivingOffTheLandBinary</c> cũng BỎ SÓT
    /// mọi LOLBin nằm trong thư mục có dấu cách.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe", "microsoftedgeupdate.exe")]
    [InlineData(@"C:\Program Files\App\svc.exe -k netsvcs", "svc.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe /c dir", "cmd.exe")]
    // Duong dan trong nhay van phai dung nhu truoc.
    [InlineData(@"""C:\Program Files\App\svc.exe"" -flag", "svc.exe")]
    public void TenFile_DuongDanCoKhoangTrangKhongNhay(string command, string expectedFile)
    {
        var entries = new[] { Entry(BlacklistKind.FileName, expectedFile) };

        Assert.Single(BlacklistMatcher.Match(TaskEvent(command: command), entries));
    }

    /// <summary>Đường dẫn đầy đủ cũng phải bóc đúng khi có khoảng trắng, không nháy.</summary>
    [Fact]
    public void DuongDan_CoKhoangTrangKhongNhay()
    {
        var entries = new[]
        {
            Entry(BlacklistKind.ExecutablePath,
                @"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe")
        };

        var matches = BlacklistMatcher.Match(
            TaskEvent(command: @"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe"),
            entries);

        Assert.Single(matches);
    }

    [Fact]
    public void TenFile_KhopBatKeThuMuc()
    {
        var entries = new[] { Entry(BlacklistKind.FileName, "evil.exe") };

        var matches = BlacklistMatcher.Match(
            TaskEvent(command: @"D:\somewhere\else\evil.exe"), entries);

        Assert.Single(matches);
    }

    /// <summary>Chuỗi lệnh là dạng DUY NHẤT dùng <c>Contains</c>.</summary>
    [Fact]
    public void ChuoiLenh_KhopTrongThamSo()
    {
        var entries = new[] { Entry(BlacklistKind.CommandFragment, "-enc") };

        var matches = BlacklistMatcher.Match(
            TaskEvent(command: @"C:\Windows\System32\powershell.exe", arguments: "-enc SQBFAFgA"),
            entries);

        Assert.Single(matches);
        Assert.Equal("tham số của task", matches[0].MatchedIn);
    }

    [Fact]
    public void TaiKhoan_KhopServiceAccount()
    {
        var entries = new[] { Entry(BlacklistKind.Account, "LocalSystem") };

        var matches = BlacklistMatcher.Match(ServiceEvent(account: "LocalSystem"), entries);

        Assert.Single(matches);
    }

    [Fact]
    public void DongDaTat_KhongDuocKhop()
    {
        var entries = new[] { Entry(BlacklistKind.ExecutablePath, @"C:\Users\Public\evil.exe", enabled: false) };

        var matches = BlacklistMatcher.Match(
            TaskEvent(command: @"C:\Users\Public\evil.exe"), entries);

        Assert.Empty(matches);
    }

    [Fact]
    public void DanhSachRong_KhongNo()
    {
        Assert.Empty(BlacklistMatcher.Match(TaskEvent(command: @"C:\a.exe"), []));
    }

    /// <summary>Một event dính nhiều dòng thì phải trả về đủ, không dừng ở dòng đầu.</summary>
    [Fact]
    public void NhieuDongCungKhop_TraDuHet()
    {
        var entries = new[]
        {
            Entry(BlacklistKind.ExecutablePath, @"C:\Users\Public\evil.exe"),
            Entry(BlacklistKind.FileName, "evil.exe"),
        };

        var matches = BlacklistMatcher.Match(
            TaskEvent(command: @"C:\Users\Public\evil.exe"), entries);

        Assert.Equal(2, matches.Count);
    }

    // ============================================================ Điều kiện học

    /// <summary>
    /// RÀO QUAN TRỌNG NHẤT. Học một binary trong System32 nghĩa là mọi task Windows
    /// hợp lệ dùng nó sẽ thành High vĩnh viễn — đúng thảm hoạ mà
    /// <c>ContextualLolBins</c> sinh ra để tránh (rundll32 từng cho 6/6 dương tính giả).
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\rundll32.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\SysWOW64\regsvr32.exe")]
    [InlineData(@"C:\Program Files\App\svc.exe")]
    [InlineData(@"C:\Program Files (x86)\App\svc.exe")]
    public void KhongBaoGioHoc_BinaryTrongThuMucHeThong(string path)
    {
        Assert.False(BlacklistLearner.IsLearnablePath(path));

        var candidate = BlacklistLearner.FromHit(
            TaskEvent(command: path), RuleCatalog.TaskLolBin, RiskLevel.High, "test");

        Assert.Null(candidate);
    }

    /// <summary>Tên trần không định danh được file cụ thể nào.</summary>
    [Theory]
    [InlineData("mshta.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void KhongHoc_TenTranHoacRong(string? value)
    {
        Assert.False(BlacklistLearner.IsLearnablePath(value));
    }

    [Fact]
    public void Hoc_DuongDanTrongThuMucGhiDuoc()
    {
        var candidate = BlacklistLearner.FromHit(
            TaskEvent(command: @"C:\Users\Public\evil.exe"),
            RuleCatalog.TaskLolBin, RiskLevel.High, "bang chung");

        Assert.NotNull(candidate);
        Assert.Equal(BlacklistKind.ExecutablePath, candidate.Kind);
        Assert.Equal(@"c:\users\public\evil.exe", candidate.Value);
        Assert.Equal(RiskLevel.High, candidate.Severity);
    }

    /// <summary>
    /// ⚠️ QUYẾT ĐỊNH LẤY TỪ DỮ LIỆU THẬT — đừng nới mà không đo lại.
    ///
    /// <c>TASK_WRITABLE_DIR</c> KHÔNG được dạy blacklist. Lần đo trên 1.807 event thật:
    /// rule này dạy 2 đường dẫn và cả hai đều là OneDrive của Microsoft, chiếm 17/19
    /// cảnh báo BLACKLIST_HIT. <c>%LOCALAPPDATA%</c> là nơi phần mềm per-user hợp lệ
    /// cài đặt, nên vị trí đủ để cảnh báo nhưng không đủ để kết án vĩnh viễn.
    /// </summary>
    [Fact]
    public void KhongHoc_TuThuMucGhiDuoc_VIDUONGTINHGIA_ONEDRIVE()
    {
        var onedrive = TaskEvent(
            command: @"C:\Users\kazyy\AppData\Local\Microsoft\OneDrive\OneDriveStandaloneUpdater.exe");

        Assert.Null(BlacklistLearner.FromHit(
            onedrive, RuleCatalog.TaskWritableDir, RiskLevel.High, "test"));

        Assert.DoesNotContain(RuleCatalog.TaskWritableDir, BlacklistLearner.TeachingRules);
    }

    /// <summary>
    /// Nhưng SERVICE chạy từ AppData thì VẪN học: service là phạm vi toàn máy, phải có
    /// quyền admin mới cài — phần mềm per-user không cài service vào AppData. Đây là
    /// dòng đúng duy nhất trong lần đo trên.
    /// </summary>
    [Fact]
    public void VanHoc_ServiceChayTuAppData()
    {
        var candidate = BlacklistLearner.FromHit(
            ServiceEvent(imagePath: @"C:\Users\kazyy\AppData\Local\fake.exe"),
            RuleCatalog.ServiceNonStandardPath, RiskLevel.High, "test");

        Assert.NotNull(candidate);
        Assert.Equal(@"c:\users\kazyy\appdata\local\fake.exe", candidate.Value);
    }

    /// <summary>Medium là "đáng xem", chưa đủ chắc để đóng dấu vĩnh viễn.</summary>
    [Theory]
    [InlineData(RiskLevel.Low)]
    [InlineData(RiskLevel.Medium)]
    public void KhongHoc_KhiMucChuaPhaiHigh(RiskLevel severity)
    {
        var candidate = BlacklistLearner.FromHit(
            TaskEvent(command: @"C:\Users\Public\evil.exe"),
            RuleCatalog.TaskWritableDir, severity, "test");

        Assert.Null(candidate);
    }

    /// <summary>
    /// Rule không nói về một file cụ thể thì không được dạy blacklist —
    /// <c>SUSPICIOUS_RAW_CONTENT</c> quét cả XML thô nên giá trị nó bắt được không
    /// gắn với đường dẫn nào.
    /// </summary>
    [Theory]
    [InlineData(RuleCatalog.SuspiciousRawContent)]
    [InlineData(RuleCatalog.TaskCreated)]
    [InlineData(RuleCatalog.TaskCreateThenDelete)]
    [InlineData(RuleCatalog.ServiceStartTypeChanged)]
    public void KhongHoc_TuRuleKhongNoiVeMotFile(string ruleId)
    {
        var candidate = BlacklistLearner.FromHit(
            TaskEvent(command: @"C:\Users\Public\evil.exe"), ruleId, RiskLevel.High, "test");

        Assert.Null(candidate);
    }

    /// <summary>Service học từ ImagePath khi task không có lệnh.</summary>
    [Fact]
    public void Hoc_TuImagePathCuaService()
    {
        var candidate = BlacklistLearner.FromHit(
            ServiceEvent(imagePath: @"C:\Users\Public\svc.exe -k run"),
            RuleCatalog.ServiceNonStandardPath, RiskLevel.High, "test");

        Assert.NotNull(candidate);
        Assert.Equal(@"c:\users\public\svc.exe", candidate.Value);
    }

    /// <summary>
    /// Giá trị học ra phải khớp lại được bằng chính <see cref="BlacklistMatcher"/> —
    /// hai bên dùng chung hàm chuẩn hoá, lệch nhau là blacklist học xong không bao giờ
    /// khớp và trông như tính năng hỏng.
    /// </summary>
    [Fact]
    public void GiaTriHocRa_KhopLaiDuoc()
    {
        var evt = TaskEvent(command: @"C:\Users\Public\Evil.exe");

        var candidate = BlacklistLearner.FromHit(
            evt, RuleCatalog.TaskLolBin, RiskLevel.High, "test");

        Assert.NotNull(candidate);

        var entry = new BlacklistEntry
        {
            Kind = candidate.Kind,
            Value = candidate.Value,
            Severity = candidate.Severity,
            Source = BlacklistSource.AutoLearned,
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };

        Assert.Single(BlacklistMatcher.Match(evt, [entry]));
    }
}
