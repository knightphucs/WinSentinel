using TaskServiceMonitor.Models;
using TaskServiceMonitor.Monitoring;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Test chạy trên MẪU XML THẬT lấy từ máy dev, không phải XML tự bịa.
/// Mỗi nhóm test ứng với một cấu trúc XML khác nhau đã gặp trong thực tế.
/// </summary>
public class WindowsEventParserTests
{
    private readonly WindowsEventParser _parser = new();

    // ---------------------------------------------------------------- Chung

    [Theory]
    [InlineData("4697_service_installed_security.xml", 4697, "Security")]
    [InlineData("4698_task_created_exec.xml", 4698, "Security")]
    [InlineData("4699_task_deleted.xml", 4699, "Security")]
    [InlineData("4700_task_enabled.xml", 4700, "Security")]
    [InlineData("4701_task_disabled_exec.xml", 4701, "Security")]
    [InlineData("4702_task_updated_comhandler.xml", 4702, "Security")]
    [InlineData("7040_service_starttype_changed.xml", 7040, "System")]
    [InlineData("7045_service_installed_system.xml", 7045, "System")]
    public void Parse_DocDungThongTinChung(string fixture, int expectedEventId, string expectedChannel)
    {
        var result = _parser.Parse(SampleXml.Load(fixture));

        Assert.Equal(expectedEventId, result.EventId);
        Assert.Equal(expectedChannel, result.Channel);
        Assert.Equal("DESKTOP-9C4QS7J", result.Hostname);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.NotEmpty(result.RawXml);
        Assert.NotNull(result.ObjectName);
    }

    [Fact]
    public void Parse_TimeCreated_LuonLaUtc()
    {
        var result = _parser.Parse(SampleXml.Load("4698_task_created_exec.xml"));

        // Event log ghi SystemTime theo UTC; khong duoc am tham doi sang gio local
        // vi nhieu may nguon co the khac mui gio.
        Assert.Equal(DateTimeKind.Utc, result.TimeCreated.Kind);
        Assert.Equal(new DateTime(2026, 8, 13, 3, 26, 53, DateTimeKind.Utc), result.TimeCreated,
            TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("4698_task_created_exec.xml", MonitoredObjectType.ScheduledTask)]
    [InlineData("4702_task_updated_comhandler.xml", MonitoredObjectType.ScheduledTask)]
    [InlineData("7045_service_installed_system.xml", MonitoredObjectType.Service)]
    [InlineData("4697_service_installed_security.xml", MonitoredObjectType.Service)]
    public void Parse_PhanLoaiDungObjectType(string fixture, MonitoredObjectType expected)
    {
        Assert.Equal(expected, _parser.Parse(SampleXml.Load(fixture)).ObjectType);
    }

    // ---------------------------------------------------------------- Scheduled Task

    [Fact]
    public void Parse_4698_LayDuocTaskNameVaLenhChay()
    {
        var result = _parser.Parse(SampleXml.Load("4698_task_created_exec.xml"));

        Assert.Equal("Task created", result.ActionDescription);
        Assert.Equal(@"\WinSentinelTest", result.ObjectName);
        Assert.Equal(@"DESKTOP-9C4QS7J\Kazyy", result.ActorAccount);

        // TaskContent la XML LONG TRONG XML -> phai parse tang hai moi ra command.
        Assert.Equal("Exec", result.TaskActionType);
        Assert.Equal("cmd.exe", result.TaskCommand);
        Assert.Equal("LeastPrivilege", result.TaskRunLevel);
        Assert.NotNull(result.TaskContentXml);
    }

    /// <summary>
    /// BAY QUAN TRONG: 4702 dung field 'TaskContentNew', cac event task khac dung
    /// 'TaskContent'. Neu parser chi doc 'TaskContent' thi 4702 se mat sach du lieu
    /// task - dung o event nhay cam nhat (task bi sua).
    /// </summary>
    [Fact]
    public void Parse_4702_DocDuocTaskContentNew_KhongPhaiTaskContent()
    {
        var xml = SampleXml.Load("4702_task_updated_comhandler.xml");

        Assert.Contains("TaskContentNew", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Name='TaskContent'", xml, StringComparison.Ordinal);

        var result = _parser.Parse(xml);

        Assert.Equal("Task updated", result.ActionDescription);
        Assert.NotNull(result.TaskContentXml);
        Assert.NotNull(result.TaskActionType);
    }

    /// <summary>
    /// Task khong nhat thiet chay bang &lt;Exec&gt;. Task he thong dung &lt;ComHandler&gt;
    /// voi mot CLSID va KHONG he co &lt;Command&gt; - day la du lieu binh thuong,
    /// khong duoc coi la parse loi.
    /// </summary>
    [Fact]
    public void Parse_TaskDungComHandler_LuuClsidVaKhongCoCommand()
    {
        var result = _parser.Parse(SampleXml.Load("4701_task_disabled_comhandler.xml"));

        Assert.Equal("ComHandler", result.TaskActionType);
        Assert.Equal("{5654D1B7-5DD0-4F6B-9AAC-5F0F602FAE03}", result.TaskComHandlerClassId);
        Assert.Null(result.TaskCommand);

        // Van phai nhan dang duoc event, khong duoc roi vao nhanh du phong.
        Assert.True(result.IsRecognized);
    }

    [Fact]
    public void Parse_TaskDungExec_KhongCoClsid()
    {
        var result = _parser.Parse(SampleXml.Load("4701_task_disabled_exec.xml"));

        Assert.Equal("Exec", result.TaskActionType);
        Assert.Equal("cmd.exe", result.TaskCommand);
        Assert.Equal("/c echo updated", result.TaskArguments);
        Assert.Null(result.TaskComHandlerClassId);
    }

    /// <summary>
    /// 4699 (task deleted) VAN co field TaskContent nhung noi dung RONG.
    /// Parser phai coi do la null chu khong duoc tra ve chuoi rong.
    /// </summary>
    [Fact]
    public void Parse_4699_TaskContentRong_KhongLamHongEvent()
    {
        var result = _parser.Parse(SampleXml.Load("4699_task_deleted.xml"));

        Assert.Equal("Task deleted", result.ActionDescription);
        Assert.Equal(@"\WinSentinelTest", result.ObjectName);
        Assert.True(result.IsRecognized);
        Assert.Null(result.TaskContentXml);
    }

    // ---------------------------------------------------------------- Service

    /// <summary>
    /// 4697 va 7045 mo ta CUNG mot hanh dong (cai service) nhung ten field khac nhau
    /// va gia tri khac dinh dang: 4697 tra ma so, 7045 tra chu. Parser phai chuan hoa
    /// ve cung dang thi dashboard moi gop duoc hai nguon.
    /// </summary>
    [Fact]
    public void Parse_4697_va_7045_ChuanHoaVeCungDinhDang()
    {
        var fromSecurity = _parser.Parse(SampleXml.Load("4697_service_installed_security.xml"));
        var fromSystem = _parser.Parse(SampleXml.Load("7045_service_installed_system.xml"));

        // Cung mo ta mot lan cai service WinSentinelSvc.
        Assert.Equal("WinSentinelSvc", fromSecurity.ObjectName);
        Assert.Equal("WinSentinelSvc", fromSystem.ObjectName);

        Assert.Equal(@"C:\Windows\System32\snmptrap.exe", fromSecurity.ImagePath);
        Assert.Equal(@"C:\Windows\System32\snmptrap.exe", fromSystem.ImagePath);

        // 4697 goc la ServiceStartType='3' va ServiceType='0x10' -> phai doi sang chu.
        Assert.Equal("demand start", fromSecurity.StartType);
        Assert.Equal("demand start", fromSystem.StartType);
        Assert.Equal("user mode service", fromSecurity.ServiceType);
        Assert.Equal("user mode service", fromSystem.ServiceType);

        Assert.Equal("LocalSystem", fromSecurity.ServiceAccount);
        Assert.Equal("LocalSystem", fromSystem.ServiceAccount);
    }

    /// <summary>
    /// 7040 khong co field co ten, chi co param1..param4:
    /// param1 = ten hien thi, param2 = start type CU, param3 = start type MOI, param4 = ten ngan.
    /// </summary>
    [Fact]
    public void Parse_7040_MapDungThuTuParam()
    {
        var result = _parser.Parse(SampleXml.Load("7040_service_starttype_changed.xml"));

        Assert.Equal("Service start type changed", result.ActionDescription);
        Assert.Equal("BITS", result.ObjectName);
        Assert.Equal("Background Intelligent Transfer Service", result.DisplayName);
        Assert.Equal("demand start", result.PreviousStartType);
        Assert.Equal("auto start", result.StartType);
    }

    /// <summary>
    /// Nhom Security co the &lt;Security/&gt; RONG -> phai lay user tu SubjectUserName.
    /// Nhom SCM thi nguoc lai: khong co SubjectUserName, chi co SID o System/Security.
    /// </summary>
    [Fact]
    public void Parse_LayActorAccount_DungNguonTheoTungNhom()
    {
        var security = _parser.Parse(SampleXml.Load("4698_task_created_exec.xml"));
        Assert.Equal(@"DESKTOP-9C4QS7J\Kazyy", security.ActorAccount);
        Assert.StartsWith("S-1-5-21-", security.ActorSid, StringComparison.Ordinal);

        var scm = _parser.Parse(SampleXml.Load("7040_service_starttype_changed.xml"));
        Assert.Equal("S-1-5-18", scm.ActorSid);
        Assert.Equal("LocalSystem", scm.ActorAccount);
    }

    // ---------------------------------------------------------------- TaskScheduler-Operational
    // Mau that thu duoc tu channel Microsoft-Windows-TaskScheduler/Operational (xem
    // Phase 3/4 cua ke hoach nang cap) - task "\WinSentinelSampleCapture" tao/sua/
    // chay/xoa qua chinh UI cua app.

    /// <summary>
    /// TaskName cua channel nay CO khoang trang thua o cuoi trong XML goc (da xac
    /// nhan qua mau that: "\WinSentinelSampleCapture "). Parser phai cat di de khop
    /// dinh dang "\Path" khong khoang trang ma cac nguon khac (COM, Security) dung -
    /// khong cat se lam lech key luc doi chieu ObjectName giua cac nguon.
    /// </summary>
    [Fact]
    public void Parse_106_TaskDangKy_CatKhoangTrangThuaCuaTaskName()
    {
        var result = _parser.Parse(SampleXml.Load("106_task_registered_operational.xml"));

        Assert.Equal("Task registered (Operational)", result.ActionDescription);
        Assert.Equal(MonitoredObjectType.ScheduledTask, result.ObjectType);
        Assert.Equal(@"\WinSentinelSampleCapture", result.ObjectName);
        Assert.Equal(@"KAZYY3103\win10", result.ActorAccount);
        Assert.True(result.IsRecognized);
    }

    /// <summary>140 (task updated) va 141 (task deleted) dung CUNG mot hinh dang field.</summary>
    [Theory]
    [InlineData("140_task_updated_operational.xml", "Task updated (Operational)")]
    [InlineData("141_task_deleted_operational.xml", "Task deleted (Operational)")]
    public void Parse_140Va141_CungHinhDangField(string fixture, string expectedAction)
    {
        var result = _parser.Parse(SampleXml.Load(fixture));

        Assert.Equal(expectedAction, result.ActionDescription);
        Assert.Equal(@"\WinSentinelSampleCapture", result.ObjectName);
        Assert.Equal(@"KAZYY3103\win10", result.ActorAccount);
        Assert.True(result.IsRecognized);
    }

    /// <summary>
    /// 200/201 KHONG co UserContext/UserName nhu 106/140/141 - actor phai lay tu
    /// System/Security (giong nhom Service) chu khong duoc de trong.
    /// </summary>
    [Fact]
    public void Parse_200_ActionStarted_LayLenhVaInstanceId()
    {
        var result = _parser.Parse(SampleXml.Load("200_task_action_started.xml"));

        Assert.Equal("Task action started", result.ActionDescription);
        Assert.Equal(@"\WinSentinelSampleCapture", result.ObjectName);
        Assert.Equal("cmd.exe", result.TaskCommand);
        Assert.False(string.IsNullOrWhiteSpace(result.TaskInstanceId));
        Assert.Equal("S-1-5-18", result.ActorSid);
        Assert.Equal("LocalSystem", result.ActorAccount);
    }

    /// <summary>
    /// 201 kem ResultCode. Mau nay CO ActionName rong (task dung ComHandler, khong co
    /// ten lenh) - phai ra null (qua helper Get()) chu khong phai chuoi rong.
    /// </summary>
    [Fact]
    public void Parse_201_ActionCompleted_LayResultCode()
    {
        var result = _parser.Parse(SampleXml.Load("201_task_action_completed.xml"));

        Assert.Equal("Task action completed", result.ActionDescription);
        Assert.Equal("0", result.TaskActionResultCode);
        Assert.False(string.IsNullOrWhiteSpace(result.TaskInstanceId));
        Assert.Null(result.TaskCommand);
    }

    // ---------------------------------------------------------------- Nhanh du phong

    /// <summary>
    /// Event ID chua biet cau truc KHONG duoc lam crash parser - phai tra ve event
    /// hop le voi IsRecognized = false va giu nguyen du lieu tho trong Data.
    /// </summary>
    [Fact]
    public void Parse_EventIdLa_KhongCrash_VaDanhDauChuaNhanDang()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System>
                <Provider Name='Service Control Manager'/>
                <EventID>9999</EventID>
                <TimeCreated SystemTime='2026-08-13T03:26:53.0000000Z'/>
                <EventRecordID>123</EventRecordID>
                <Channel>System</Channel>
                <Computer>MAY-LA</Computer>
                <Security UserID='S-1-5-18'/>
              </System>
              <EventData>
                <Data Name='ServiceName'>DichVuLa</Data>
              </EventData>
            </Event>
            """;

        var result = _parser.Parse(xml);

        Assert.False(result.IsRecognized);
        Assert.Equal(9999, result.EventId);
        Assert.Equal("MAY-LA", result.Hostname);
        Assert.Equal("DichVuLa", result.ObjectName);
        Assert.Equal("DichVuLa", result.Data["ServiceName"]);
    }

    /// <summary>
    /// 7036 va 7034 chua co mau that nen CO Y chua co nhanh parse rieng.
    /// Test nay khoa lai cam ket do: neu ai them nhanh cho chung thi phai them mau
    /// that va sua test, khong duoc doan cau truc.
    /// </summary>
    [Fact]
    public void RecognizedEventIds_ChiGomEventIdDaCoMauThat()
    {
        Assert.Equal(
            [106, 140, 141, 200, 201, 4697, 4698, 4699, 4700, 4701, 4702, 7040, 7045],
            WindowsEventParser.RecognizedEventIds.Order());

        Assert.DoesNotContain(7036, WindowsEventParser.RecognizedEventIds);
        Assert.DoesNotContain(7034, WindowsEventParser.RecognizedEventIds);
    }

    [Fact]
    public void Parse_XmlHong_NemFormatException()
    {
        Assert.ThrowsAny<Exception>(() => _parser.Parse("day khong phai xml"));
    }
}
