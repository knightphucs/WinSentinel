using System.Xml.Linq;
using TaskServiceMonitor.Management;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// <c>BuildTaskXml</c> là hàm thuần (model vào, XML ra) nên test được mà không cần
/// Windows — đây chính là lý do giữ cách dựng XML thay vì object model của COM.
///
/// XML sinh ra phải cùng schema mà <c>WindowsEventParser</c> đọc từ event 4698/4702.
/// </summary>
public class BuildTaskXmlTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Build(TaskDefinitionRequest req) =>
        XDocument.Parse(TaskManager.BuildTaskXml(req));

    private static TaskDefinitionRequest Minimal() => new()
    {
        Name = "WinSentinelDemo",
        Actions = [new ActionRequest("Exec", @"C:\Windows\System32\cmd.exe", "/c echo hi")],
        Triggers = [new TriggerRequest("Time", "2026-08-17T23:59:00")],
    };

    [Fact]
    public void SinhDungSchemaTaskScheduler()
    {
        var doc = Build(Minimal());

        Assert.Equal(Ns + "Task", doc.Root!.Name);
        Assert.Equal("1.2", doc.Root.Attribute("version")?.Value);
    }

    [Fact]
    public void NhieuAction_SinhDuTatCa()
    {
        // Truoc buoc 10 chi doc duoc action dau tien; day la phep thu cho viec do.
        var doc = Build(Minimal() with
        {
            Actions =
            [
                new ActionRequest("Exec", @"C:\Windows\System32\cmd.exe", "/c echo one"),
                new ActionRequest("Exec", @"C:\Windows\System32\notepad.exe"),
            ],
        });

        var execs = doc.Descendants(Ns + "Exec").ToList();

        Assert.Equal(2, execs.Count);
        Assert.Equal("/c echo one", execs[0].Element(Ns + "Arguments")?.Value);
        // Action khong co tham so thi KHONG duoc sinh the <Arguments> rong.
        Assert.Null(execs[1].Element(Ns + "Arguments"));
    }

    [Fact]
    public void NhieuTrigger_SinhDungTenPhanTu()
    {
        var doc = Build(Minimal() with
        {
            Triggers =
            [
                new TriggerRequest("Time", "2026-08-17T23:59:00"),
                new TriggerRequest("Logon"),
                new TriggerRequest("Boot"),
            ],
        });

        var triggers = doc.Descendants(Ns + "Triggers").Single();

        Assert.NotNull(triggers.Element(Ns + "TimeTrigger"));
        Assert.NotNull(triggers.Element(Ns + "LogonTrigger"));
        Assert.NotNull(triggers.Element(Ns + "BootTrigger"));
    }

    [Fact]
    public void TriggerDaily_BatBuocCoScheduleByDay()
    {
        // CalendarTrigger thieu lich con thi Task Scheduler tu choi ca task.
        var doc = Build(Minimal() with
        {
            Triggers = [new TriggerRequest("Daily", "2026-08-17T08:00:00", DaysInterval: 3)],
        });

        var byDay = doc.Descendants(Ns + "ScheduleByDay").Single();

        Assert.Equal("3", byDay.Element(Ns + "DaysInterval")?.Value);
    }

    [Fact]
    public void RunLevel_VaoDungPrincipal()
    {
        var doc = Build(Minimal() with { RunLevel = "HighestAvailable" });

        Assert.Equal("HighestAvailable",
            doc.Descendants(Ns + "Principal").Single().Element(Ns + "RunLevel")?.Value);
    }

    [Fact]
    public void GroupId_LoaiTru_UserId()
    {
        // Khai ca hai thi Task Scheduler tu choi - chi duoc mot.
        var doc = Build(Minimal() with { UserId = "KAZYY\\win10", GroupId = "BUILTIN\\Administrators" });
        var principal = doc.Descendants(Ns + "Principal").Single();

        Assert.NotNull(principal.Element(Ns + "GroupId"));
        Assert.Null(principal.Element(Ns + "UserId"));
    }

    [Fact]
    public void AuthorVaDescription_VaoRegistrationInfo()
    {
        var doc = Build(Minimal() with { Author = "Kazyy", Description = "mo ta thu" });
        var reg = doc.Descendants(Ns + "RegistrationInfo").Single();

        Assert.Equal("Kazyy", reg.Element(Ns + "Author")?.Value);
        Assert.Equal("mo ta thu", reg.Element(Ns + "Description")?.Value);
    }

    [Fact]
    public void Hidden_VaoSettings()
    {
        Assert.Equal("true",
            Build(Minimal() with { Hidden = true })
                .Descendants(Ns + "Settings").Single().Element(Ns + "Hidden")?.Value);
    }

    [Fact]
    public void ActionThieuCommand_BiBoQua()
    {
        var doc = Build(Minimal() with
        {
            Actions =
            [
                new ActionRequest("Exec", @"C:\Windows\System32\cmd.exe"),
                new ActionRequest("Exec", null),
            ],
        });

        Assert.Single(doc.Descendants(Ns + "Exec"));
    }

    [Fact]
    public void GiaTriNguoiDungNhap_DuocESCAPE_KhongPhaiChenXml()
    {
        // XElement escape tu dong - '</Command>' thanh text, khong thanh the moi.
        var doc = Build(Minimal() with
        {
            Actions = [new ActionRequest("Exec", @"C:\Windows\System32\cmd.exe",
                "</Arguments></Exec><Exec><Command>evil.exe</Command></Exec>")],
        });

        Assert.Single(doc.Descendants(Ns + "Exec"));
        Assert.DoesNotContain("evil.exe",
            doc.Descendants(Ns + "Command").Select(c => c.Value));
    }
}
