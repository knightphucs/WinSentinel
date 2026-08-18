using TaskServiceMonitor.Monitoring;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Test cho <see cref="AdHocLogReader.BuildBrowseXPath"/> — bộ lọc khoảng thời gian
/// của "duyệt log bất kỳ" và 4 panel log ở chế độ "Toàn bộ channel".
///
/// Vì sao đáng test riêng: lọc thời gian ở đây chạy Ở SERVER, nhúng thẳng vào XPath
/// mà Windows đọc. Sai định dạng mốc thời gian thì Windows **không báo lỗi** — nó chỉ
/// trả về ít hơn (hoặc không trả gì), im lặng, rất khó lần ra. Hàm thuần nên chạy
/// được trên mọi máy, không cần Windows.
/// </summary>
public class AdHocLogXPathTests
{
    private static readonly DateTime From = new(2026, 8, 18, 6, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 8, 18, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void KhongCoDieuKien_TraVeDauSao()
    {
        Assert.Equal("*", AdHocLogReader.BuildBrowseXPath(null, null, null));
    }

    [Fact]
    public void ChiEventId_GiuNguyenDangCu()
    {
        // Dang nay da chay tu buoc 8, khong duoc doi khi them loc thoi gian.
        Assert.Equal("*[System[(EventID=4698)]]", AdHocLogReader.BuildBrowseXPath(4698, null, null));
    }

    [Fact]
    public void ChiMocBatDau_ChiSinhDieuKienLonHon()
    {
        Assert.Equal(
            "*[System[TimeCreated[@SystemTime>='2026-08-18T06:30:00.000Z']]]",
            AdHocLogReader.BuildBrowseXPath(null, From, null));
    }

    [Fact]
    public void ChiMocKetThuc_ChiSinhDieuKienNhoHon()
    {
        Assert.Equal(
            "*[System[TimeCreated[@SystemTime<='2026-08-18T14:00:00.000Z']]]",
            AdHocLogReader.BuildBrowseXPath(null, null, To));
    }

    [Fact]
    public void EventIdVaCaHaiMoc_NoiBangAnd()
    {
        Assert.Equal(
            "*[System[(EventID=7045) and TimeCreated[" +
            "@SystemTime>='2026-08-18T06:30:00.000Z' and " +
            "@SystemTime<='2026-08-18T14:00:00.000Z']]]",
            AdHocLogReader.BuildBrowseXPath(7045, From, To));
    }

    /// <summary>
    /// BẪY THẬT: <c>DateTime</c> đi qua tầng API có thể mang <c>Kind.Unspecified</c>
    /// (hoặc Local). Hàm phải tự ép về UTC, không được in ra chuỗi có offset kiểu
    /// <c>+07:00</c> — Windows sẽ hiểu sai và trả về sai khoảng, im lặng.
    /// </summary>
    [Fact]
    public void MocKhongMangKind_VanInRaDangUtcCoHauToZ()
    {
        var unspecified = new DateTime(2026, 8, 18, 6, 30, 0, DateTimeKind.Unspecified);

        var xpath = AdHocLogReader.BuildBrowseXPath(null, unspecified, null);

        Assert.Contains("2026-08-18T06:30:00.000Z", xpath);
        Assert.DoesNotContain("+", xpath);
    }
}
