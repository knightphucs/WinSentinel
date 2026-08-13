using TaskServiceMonitor.Data;
using TaskServiceMonitor.Monitoring;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Test cho lớp chuyển <c>Data</c> thành cột jsonb. Chạy được mà KHÔNG cần
/// PostgreSQL thật vì converter chỉ là serialize/deserialize thuần.
/// </summary>
public class EventDataJsonConverterTests
{
    [Fact]
    public void RoundTrip_GiuNguyenToanBoField()
    {
        var goc = new Dictionary<string, string>
        {
            ["ServiceName"] = "WinSentinelSvc",
            ["ImagePath"] = @"C:\Windows\System32\snmptrap.exe",
            ["StartType"] = "demand start"
        };

        var ketQua = EventDataJsonConverter.Deserialize(EventDataJsonConverter.Serialize(goc));

        Assert.Equal(goc.Count, ketQua.Count);
        foreach (var (key, value) in goc)
        {
            Assert.Equal(value, ketQua[key]);
        }
    }

    /// <summary>
    /// Mau that: 4698 co TaskContent la ca mot XML long ben trong. Ky tu dac biet
    /// (dau nhay, xuong dong) phai qua duoc JSON ma khong bien dang.
    /// </summary>
    [Fact]
    public void RoundTrip_ChiuDuocXmlLongBenTrong()
    {
        var parser = new WindowsEventParser();
        var evt = parser.Parse(SampleXml.Load("4698_task_created_exec.xml"));

        var ketQua = EventDataJsonConverter.Deserialize(EventDataJsonConverter.Serialize(evt.Data));

        Assert.Equal(evt.Data.Count, ketQua.Count);
        Assert.Equal(evt.Data["TaskName"], ketQua["TaskName"]);

        // TaskContent chua XML co dau nhay va xuong dong - de vo nhat.
        Assert.Contains("<Task", ketQua["TaskContent"], StringComparison.Ordinal);
        Assert.Equal(evt.Data["TaskContent"], ketQua["TaskContent"]);
    }

    [Fact]
    public void Deserialize_ChuoiRongHoacNull_TraVeDictionaryRong()
    {
        Assert.Empty(EventDataJsonConverter.Deserialize(null));
        Assert.Empty(EventDataJsonConverter.Deserialize(""));
        Assert.Empty(EventDataJsonConverter.Deserialize("   "));
    }

    [Fact]
    public void Serialize_Null_TraVeJsonRong()
    {
        Assert.Equal("{}", EventDataJsonConverter.Serialize(null));
    }

    /// <summary>
    /// Comparer bao cho EF biet hai dictionary co khac nhau khong. Sai cho nay thi
    /// EF co the bo qua thay doi hoac ghi thua.
    /// </summary>
    [Fact]
    public void Comparer_SoSanhTheoNoiDung_KhongPhaiTheoThamChieu()
    {
        var a = new Dictionary<string, string> { ["param1"] = "BITS" };
        var b = new Dictionary<string, string> { ["param1"] = "BITS" };
        var c = new Dictionary<string, string> { ["param1"] = "Spooler" };

        Assert.True(EventDataJsonConverter.Comparer.Equals(a, b));
        Assert.False(EventDataJsonConverter.Comparer.Equals(a, c));
    }

    /// <summary>
    /// Snapshot phai tao BAN SAO. Neu tra ve chinh tham chieu cu thi EF khong phat
    /// hien duoc thay doi ben trong dictionary.
    /// </summary>
    [Fact]
    public void Comparer_Snapshot_TaoBanSaoDocLap()
    {
        var goc = new Dictionary<string, string> { ["param1"] = "BITS" };

        var ban_sao = EventDataJsonConverter.Comparer.Snapshot(goc);

        Assert.NotSame(goc, ban_sao);
        Assert.Equal("BITS", ban_sao["param1"]);
    }
}
