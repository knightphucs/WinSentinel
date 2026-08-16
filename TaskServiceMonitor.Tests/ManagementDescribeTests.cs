using System.Runtime.InteropServices;
using TaskServiceMonitor.Management;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Phần thuần hàm của <see cref="TaskManager"/>/<see cref="ServiceManager"/> — map mã
/// số sang chữ và đọc buffer. Không đụng COM hay advapi32 nên test được bình thường.
/// </summary>
public class ManagementDescribeTests
{
    [Theory]
    [InlineData(0, "Exec")]
    [InlineData(5, "ComHandler")]
    // Truoc buoc 9, moi gia tri khac 0 deu bi gan nham nhan "ComHandler".
    [InlineData(6, "SendEmail")]
    [InlineData(7, "ShowMessage")]
    [InlineData(99, "Unknown (99)")]
    public void DescribeActionType_MapDungTungLoai(int type, string expected)
    {
        Assert.Equal(expected, TaskManager.DescribeActionType(type));
    }

    [Theory]
    [InlineData(0, "LeastPrivilege")]
    [InlineData(1, "HighestAvailable")]
    public void DescribeRunLevel_MapDung(int level, string expected)
    {
        Assert.Equal(expected, TaskManager.DescribeRunLevel(level));
    }

    [Theory]
    [InlineData(3, "InteractiveToken")]
    [InlineData(5, "ServiceAccount")]
    public void DescribeLogonType_MapDung(int type, string expected)
    {
        Assert.Equal(expected, TaskManager.DescribeLogonType(type));
    }

    [Theory]
    [InlineData(0x10, "own process")]
    [InlineData(0x20, "shared process")]
    [InlineData(0x1, "kernel driver")]
    public void DescribeServiceType_MapDung(uint type, string expected)
    {
        Assert.Equal(expected, ServiceManager.DescribeServiceType(type));
    }

    [Theory]
    [InlineData(1u, 60000u, "Khởi động lại service sau 60s")]
    [InlineData(2u, 0u, "Khởi động lại máy")]
    [InlineData(3u, 500u, "Chạy lệnh sau 500ms")]
    [InlineData(0u, 0u, "Không làm gì")]
    public void DescribeRecoveryAction_MapDung(uint type, uint delay, string expected)
    {
        Assert.Equal(expected, ServiceManager.DescribeRecoveryAction(type, delay));
    }

    // ---------------------------------------------------------------- ReadMultiSz

    [Fact]
    public void ReadMultiSz_ConTroRong_TraDanhSachRong()
    {
        Assert.Empty(ServiceManager.ReadMultiSz(nint.Zero));
    }

    [Fact]
    public void ReadMultiSz_DocHetMoiChuoi_KhongDungOChuoiDauTien()
    {
        // "RpcSs\0LanmanWorkstation\0\0" - dung dinh dang lpDependencies that.
        var block = "RpcSs\0LanmanWorkstation\0\0";
        var ptr = Marshal.StringToHGlobalUni(block);

        try
        {
            var result = ServiceManager.ReadMultiSz(ptr);

            // Day chinh la bay: Marshal.PtrToStringUni() se chi tra ve "RpcSs".
            Assert.Equal(["RpcSs", "LanmanWorkstation"], result);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void ReadMultiSz_MotPhanTu()
    {
        var ptr = Marshal.StringToHGlobalUni("RpcSs\0\0");

        try
        {
            Assert.Equal(["RpcSs"], ServiceManager.ReadMultiSz(ptr));
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
