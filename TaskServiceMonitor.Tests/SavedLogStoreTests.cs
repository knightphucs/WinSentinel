using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskServiceMonitor.Configuration;
using TaskServiceMonitor.Monitoring;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Rào an toàn của <see cref="SavedLogStore.Resolve"/>: tên file đi thẳng từ URL vào
/// một app chạy quyền Administrator, rào thủng là đọc/xoá được file bất kỳ trên máy.
/// Test thuần chuỗi, không tạo/xoá file thật.
/// </summary>
public class SavedLogStoreTests
{
    private static SavedLogStore CreateStore() =>
        new(Options.Create(new EventLogOptions { SavedLogDirectory = "saved-logs" }),
            NullLogger<SavedLogStore>.Instance);

    [Theory]
    [InlineData("Application-20260816-230000.evtx")]
    [InlineData("he thong co dau cach.evtx")]
    [InlineData("HOA-THUONG.EVTX")] // duoi khong phan biet hoa thuong
    public void Resolve_ChapNhanTenFileHopLe(string fileName)
    {
        var store = CreateStore();

        var resolved = store.Resolve(fileName);

        Assert.StartsWith(store.RootDirectory, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fileName, Path.GetFileName(resolved));
    }

    [Theory]
    // Nhay ra ngoai thu muc goc.
    [InlineData(@"..\..\Windows\System32\config\SAM.evtx")]
    [InlineData("../../etc/passwd.evtx")]
    [InlineData(@"sub\folder\log.evtx")]
    // Duong dan tuyet doi.
    [InlineData(@"C:\Windows\Temp\evil.evtx")]
    [InlineData(@"\\may-khac\share\log.evtx")]
    // Sai duoi - khong cho dung endpoint nay de doc/xoa loai file khac.
    [InlineData("appsettings.json")]
    [InlineData("log.evtx.exe")]
    [InlineData("log")]
    // Rong.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Resolve_ChanTenFileNguyHiem(string? fileName)
    {
        var store = CreateStore();

        Assert.Throws<UnsafeSavedLogNameException>(() => store.Resolve(fileName));
    }

    [Fact]
    public void Resolve_MoiKetQuaDeuNamTrongThuMucGoc()
    {
        var store = CreateStore();

        // Bat bien quan trong nhat: khong nem exception thi duong dan tra ve BAT BUOC
        // nam trong thu muc goc.
        foreach (var candidate in new[] { "a.evtx", "..evtx", "-.evtx", "  b.evtx  " })
        {
            string resolved;
            try
            {
                resolved = store.Resolve(candidate);
            }
            catch (UnsafeSavedLogNameException)
            {
                continue; // Bi chan cung la ket qua dung.
            }

            Assert.StartsWith(
                store.RootDirectory + Path.DirectorySeparatorChar,
                resolved,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- Luu event dang chon

    [Fact]
    public void BuildRecordIdXPath_DungDinhDangXPath()
    {
        var xpath = SavedLogStore.BuildRecordIdXPath([12, 34, 56]);

        Assert.Equal("*[System[(EventRecordID=12 or EventRecordID=34 or EventRecordID=56)]]", xpath);
    }

    [Fact]
    public void BuildRecordIdXPath_MotPhanTu()
    {
        Assert.Equal("*[System[(EventRecordID=7)]]", SavedLogStore.BuildRecordIdXPath([7]));
    }

    [Fact]
    public void BuildRecordIdXPath_DanhSachRong_Nem()
    {
        Assert.Throws<ArgumentException>(() => SavedLogStore.BuildRecordIdXPath([]));
    }

    [Fact]
    public void BuildRecordIdXPath_QuaNhieu_Nem()
    {
        // Chan o tang app de loi bao duoc cho nguoi dung, thay vi bung ra tu Win32.
        var tooMany = Enumerable.Range(1, SavedLogStore.MaxSelectedRecords + 1)
            .Select(i => (long)i)
            .ToArray();

        Assert.Throws<ArgumentException>(() => SavedLogStore.BuildRecordIdXPath(tooMany));
    }

    [Fact]
    public void RootDirectory_LaDuongDanTuyetDoiDaChuanHoa()
    {
        var store = CreateStore();

        Assert.True(Path.IsPathFullyQualified(store.RootDirectory));
        Assert.Equal(Path.GetFullPath(store.RootDirectory), store.RootDirectory);
    }
}
