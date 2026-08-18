using TaskServiceMonitor.Detection;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Test cho lớp bóc/chuẩn hoá đường dẫn. Đây là chỗ dễ sai nhất của tầng phát hiện:
/// giá trị thật lấy từ event log KHÔNG phải đường dẫn thuần — có dấu nháy, có tham số,
/// có tiền tố NT namespace, có biến môi trường chưa giãn.
///
/// Thuần hàm, không đụng WinAPI ⇒ chạy được trên mọi máy.
/// </summary>
public class ExecutablePathParserTests
{
    // ------------------------------------------------------------ Bóc exe

    [Theory]
    // ImagePath cua service la CA dong lenh, khong chi duong dan.
    [InlineData(@"""C:\Program Files\App\svc.exe"" -k netsvcs", @"C:\Program Files\App\svc.exe")]
    [InlineData(@"C:\Windows\System32\svchost.exe -k netsvcs", @"C:\Windows\System32\svchost.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe", @"C:\Windows\System32\cmd.exe")]
    // Nhay mo ma khong dong - du lieu di dang van phai tra ve cai gi do, khong duoc nem.
    [InlineData(@"""C:\a\b.exe", @"C:\a\b.exe")]
    [InlineData("  C:\\a\\b.exe  ", @"C:\a\b.exe")]
    public void ExtractExecutable_BocDungPhanDuongDan(string raw, string expected)
    {
        Assert.Equal(expected, ExecutablePathParser.ExtractExecutable(raw));
    }

    [Fact]
    public void ExtractExecutable_ChuoiRong_TraVeRong()
    {
        Assert.Equal(string.Empty, ExecutablePathParser.ExtractExecutable("   "));
    }

    // ------------------------------------------------------------ Chuẩn hoá

    /// <summary>
    /// Driver he thong dung tien to NT namespace. Khong quy doi thi chung bi tinh nham
    /// la "ngoai thu muc he thong" -> hang loat duong tinh gia.
    /// </summary>
    [Theory]
    [InlineData(@"\??\C:\Windows\System32\drivers\x.sys", @"C:\Windows\System32\drivers\x.sys")]
    [InlineData(@"\SystemRoot\System32\drivers\y.sys", @"C:\Windows\System32\drivers\y.sys")]
    [InlineData(@"\systemroot\System32\z.sys", @"C:\Windows\System32\z.sys")]
    [InlineData("C:/Windows/System32/a.exe", @"C:\Windows\System32\a.exe")]
    public void Normalize_QuyDoiTienToDacBiet(string raw, string expected)
    {
        Assert.Equal(expected, ExecutablePathParser.Normalize(raw));
    }

    [Fact]
    public void Normalize_Null_TraVeRong()
    {
        Assert.Equal(string.Empty, ExecutablePathParser.Normalize(null));
    }

    /// <summary>
    /// Duong dan tuong doi PHAI duoc giu nguyen. Neu lop nay goi Path.GetFullPath thi
    /// no se ghep voi thu muc lam viec cua app - hoan toan vo nghia voi event den tu
    /// may khac.
    /// </summary>
    [Fact]
    public void Normalize_KhongGhepVoiThuMucLamViec()
    {
        Assert.Equal(@"a\b.exe", ExecutablePathParser.Normalize(@"a\b.exe"));
    }

    // ------------------------------------------------------------ Tên file

    [Theory]
    [InlineData(@"C:\Windows\System32\mshta.exe http://evil", "mshta.exe")]
    [InlineData(@"""C:\Program Files\App\svc.exe"" -k", "svc.exe")]
    [InlineData(@"\??\C:\Windows\System32\drivers\x.sys", "x.sys")]
    [InlineData("RUNDLL32.EXE", "rundll32.exe")]
    public void FileName_TraVeTenChuThuong(string raw, string expected)
    {
        Assert.Equal(expected, ExecutablePathParser.FileName(raw));
    }

    // ------------------------------------------------------------ Giãn biến môi trường

    [Fact]
    public void ExpandLocally_KhongCoPhanTram_TraVeNguyenVan()
    {
        const string path = @"C:\Windows\System32\cmd.exe";
        Assert.Equal(path, ExecutablePathParser.ExpandLocally(path));
    }

    [Fact]
    public void ExpandLocally_CoBienMoiTruong_DuocGian()
    {
        var expanded = ExecutablePathParser.ExpandLocally(@"%SystemRoot%\System32\cmd.exe");

        Assert.DoesNotContain("%SystemRoot%", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"System32\cmd.exe", expanded, StringComparison.OrdinalIgnoreCase);
    }
}
