using TaskServiceMonitor.Management;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Rào an toàn chặn thao tác ghi lên task/service hệ thống.
/// Đây là lớp bảo vệ DUY NHẤT giữa một cú bấm nhầm trên web UI (chạy quyền admin)
/// và việc xoá mất service thật của Windows — nên test kỹ.
/// </summary>
public class SafeNameGuardTests
{
    private readonly SafeNameGuard _guard = new("WinSentinel");

    [Theory]
    [InlineData("WinSentinelDemo")]
    [InlineData("WinSentinelDemoSvc")]
    [InlineData("WinSentinel")]
    [InlineData(@"\WinSentinelDemo")]        // Task Scheduler hay dung dang \Ten
    [InlineData("winsentineldemo")]          // khong phan biet hoa thuong
    [InlineData("WINSENTINEL_TEST")]
    public void IsWritable_TenDungTienTo_ChoPhep(string name)
    {
        Assert.True(_guard.IsWritable(name));
    }

    [Theory]
    [InlineData("Spooler")]
    [InlineData("W32Time")]
    [InlineData("MyWinSentinel")]            // tien to phai o DAU, khong phai o giua
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsWritable_TenKhongDungTienTo_TuChoi(string? name)
    {
        Assert.False(_guard.IsWritable(name));
    }

    /// <summary>
    /// Chan meo dung '..' de nhay ra ngoai pham vi cho phep.
    /// </summary>
    [Theory]
    [InlineData(@"WinSentinel\..\Spooler")]
    [InlineData(@"WinSentinel\..")]
    [InlineData("WinSentinel/../Spooler")]
    public void IsWritable_MeoVuotThuMuc_TuChoi(string name)
    {
        Assert.False(_guard.IsWritable(name));
    }

    /// <summary>
    /// Chi cho phep ten phang o thu muc goc. Task trong thu muc con bi tu choi
    /// de khong dung nham vao cay task he thong cua Microsoft.
    /// </summary>
    [Theory]
    [InlineData(@"WinSentinel\Sub\Task")]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag")]
    public void IsWritable_TenCoThuMucCon_TuChoi(string name)
    {
        Assert.False(_guard.IsWritable(name));
    }

    [Fact]
    public void EnsureWritable_TenHopLe_KhongNem()
    {
        _guard.EnsureWritable("WinSentinelDemo");
    }

    [Fact]
    public void EnsureWritable_TenKhongHopLe_NemKemGiaiThich()
    {
        var ex = Assert.Throws<UnsafeTargetException>(() => _guard.EnsureWritable("Spooler"));

        // Thong diep phai noi ro TAI SAO va SUA O DAU, khong phai "access denied" cut lui.
        Assert.Contains("Spooler", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WinSentinel", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Management:WritablePrefix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_TienToRong_NemNgay()
    {
        // Tien to rong nghia la MOI thu deu ghi duoc -> mat sach tac dung bao ve.
        Assert.Throws<ArgumentException>(() => new SafeNameGuard(""));
        Assert.Throws<ArgumentException>(() => new SafeNameGuard("   "));
    }

    [Fact]
    public void TienToTuyBien_DuocTonTrong()
    {
        var guard = new SafeNameGuard("Lab_");

        Assert.True(guard.IsWritable("Lab_Service1"));
        Assert.False(guard.IsWritable("WinSentinelDemo"));
    }
}
