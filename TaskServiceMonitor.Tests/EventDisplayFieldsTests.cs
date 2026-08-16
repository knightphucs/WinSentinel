using TaskServiceMonitor.Monitoring;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Ranh giới cần nhớ khi đọc file này: parser CHỈ thấy XML. Level/Keywords luôn có
/// trong <c>&lt;System&gt;</c> nên test được bằng mẫu thật; còn tên Task Category và
/// Description là metadata provider, với event local phải null ở đây — đó là kỳ
/// vọng ĐÚNG, không phải thiếu sót.
/// </summary>
public class EventDisplayFieldsTests
{
    private readonly WindowsEventParser _parser = new();

    // ---------------------------------------------------------------- Tu <System> (mau that)

    [Theory]
    [InlineData("4698_task_created_exec.xml", 0, 12804, "0x8020000000000000")]
    [InlineData("7045_service_installed_system.xml", 4, 0, "0x8080000000000000")]
    [InlineData("7040_service_starttype_changed.xml", 4, 0, "0x8080000000000000")]
    [InlineData("200_task_action_started.xml", 4, 200, "0x8000000000000000")]
    public void Parse_DocDuocLevelTaskKeywordsTuSystem(
        string fixture, int expectedLevel, int expectedTask, string expectedKeywords)
    {
        var result = _parser.Parse(SampleXml.Load(fixture));

        Assert.Equal(expectedLevel, result.Level);
        Assert.Equal(expectedTask, result.TaskCategoryId);
        Assert.Equal(expectedKeywords, result.Keywords);
    }

    [Theory]
    // Bang chuan cua Windows: 0 = LogAlways nhung Event Viewer van hien "Information".
    [InlineData("4698_task_created_exec.xml", "Information")]   // Level 0
    [InlineData("7045_service_installed_system.xml", "Information")] // Level 4
    public void Parse_DoiLevelSoSangTen(string fixture, string expected)
    {
        Assert.Equal(expected, _parser.Parse(SampleXml.Load(fixture)).LevelDisplayName);
    }

    [Fact]
    public void Parse_EventLocal_KhongCoDescriptionVaTenTaskCategory()
    {
        var result = _parser.Parse(SampleXml.Load("7045_service_installed_system.xml"));

        // GIOI HAN CO THAT, khong phai bug - xem ghi chu dau class.
        Assert.Null(result.Description);
        Assert.Null(result.TaskCategoryName);
        Assert.Null(result.OpcodeName);

        Assert.NotNull(result.LevelDisplayName);
        Assert.NotNull(result.Keywords);
    }

    // ---------------------------------------------------------------- Tu <RenderingInfo> (mau TU SOAN)

    /// <summary>
    /// Chạy trên fixture TỰ SOẠN: khẳng định logic parse, KHÔNG khẳng định event
    /// forwarded thật trông đúng như vậy.
    /// </summary>
    [Fact]
    public void Parse_CoRenderingInfo_LayDuocDescriptionVaTenDaDich()
    {
        var result = _parser.Parse(SampleXml.Load("renderinginfo_synthetic.xml"));

        Assert.NotNull(result.Description);
        Assert.StartsWith("A service was installed in the system.", result.Description);

        Assert.Equal("None", result.TaskCategoryName);
        Assert.Equal("Info", result.OpcodeName);

        // Ten da dich thang gia tri hex tho o <System>.
        Assert.Equal("Classic", result.Keywords);
        Assert.Equal(4, result.Level);
        Assert.Equal("Information", result.LevelDisplayName);
    }

    [Fact]
    public void Parse_CoRenderingInfo_VanDocDungPhanSystemVaEventData()
    {
        var result = _parser.Parse(SampleXml.Load("renderinginfo_synthetic.xml"));

        // Nhanh enrich 7045 khong duoc bi <RenderingInfo> lam lech.
        Assert.Equal(7045, result.EventId);
        Assert.Equal("ForwardedEvents", result.Channel);
        Assert.Equal("WEF-SOURCE-01", result.Hostname);
        Assert.Equal("WinSentinelSyntheticSvc", result.ObjectName);
        Assert.True(result.IsRecognized);
    }
}
