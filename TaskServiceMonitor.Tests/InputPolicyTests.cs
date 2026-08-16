using TaskServiceMonitor.Management;
using Xunit;

namespace TaskServiceMonitor.Tests;

/// <summary>
/// Whitelist cho GIÁ TRỊ người dùng nhập. Đáng test kỹ vì đây là thứ đứng giữa một ô
/// text trên trình duyệt và <c>CreateServiceW</c> — service tạo ra chạy LocalSystem.
///
/// Test dùng đường dẫn thật trong System32 (cmd.exe luôn có trên mọi máy Windows) vì
/// <c>EnsureAllowedExecutable</c> CÓ kiểm tra file tồn tại — đó là một lớp chặn, không
/// mock đi được mà vẫn giữ đúng ý nghĩa.
/// </summary>
public class InputPolicyTests
{
    private static InputPolicy CreatePolicy() => new(new ManagementOptions());

    private static readonly string System32 =
        Environment.GetFolderPath(Environment.SpecialFolder.System);

    // ---------------------------------------------------------------- Cho phep

    [Fact]
    public void EnsureAllowedExecutable_DuongDanHopLe_TraVeDuongDanDaChuanHoa()
    {
        var policy = CreatePolicy();
        var expected = Path.Combine(System32, "cmd.exe");

        Assert.Equal(expected, policy.EnsureAllowedExecutable(expected, "command"),
            ignoreCase: true);
    }

    [Fact]
    public void EnsureAllowedExecutable_GianBienMoiTruong()
    {
        var policy = CreatePolicy();

        // Khong gian %SystemRoot% thi buoc so sanh thu muc vo nghia.
        var resolved = policy.EnsureAllowedExecutable(@"%SystemRoot%\System32\cmd.exe", "command");

        Assert.Equal(Path.Combine(System32, "cmd.exe"), resolved, ignoreCase: true);
    }

    [Fact]
    public void EnsureAllowedExecutable_BocExeKhoiThamSo()
    {
        var policy = CreatePolicy();
        var exe = Path.Combine(System32, "cmd.exe");

        // BinaryPath cua service la CA DONG LENH, khong phai chi duong dan.
        Assert.Equal(exe, policy.EnsureAllowedExecutable($"{exe} /c echo hi", "binaryPath"), ignoreCase: true);
        Assert.Equal(exe, policy.EnsureAllowedExecutable($"\"{exe}\" -flag", "binaryPath"), ignoreCase: true);
    }

    // ---------------------------------------------------------------- Chan

    [Fact]
    public void EnsureAllowedExecutable_ThoatThuMucBangChamCham_BiChan()
    {
        var policy = CreatePolicy();

        // Day la ly do PHAI goi Path.GetFullPath truoc khi so tien to: chuoi nay khop
        // "C:\Windows\System32" theo kieu so chuoi tho, nhung tro toi cho khac han.
        var sneaky = Path.Combine(System32, @"..\..\Users\Public\evil.exe");

        Assert.Throws<UnsafeTargetException>(() => policy.EnsureAllowedExecutable(sneaky, "command"));
    }

    [Fact]
    public void EnsureAllowedExecutable_DuongDanUNC_BiChan()
    {
        var policy = CreatePolicy();

        Assert.Throws<UnsafeTargetException>(
            () => policy.EnsureAllowedExecutable(@"\\may-khac\share\evil.exe", "binaryPath"));
    }

    [Fact]
    public void EnsureAllowedExecutable_FileKhongTonTai_BiChan()
    {
        var policy = CreatePolicy();

        // Chan ky thuat "dang ky duong dan truoc, tha file vao sau".
        Assert.Throws<UnsafeTargetException>(
            () => policy.EnsureAllowedExecutable(Path.Combine(System32, "khong-ton-tai-abc.exe"), "command"));
    }

    [Fact]
    public void EnsureAllowedExecutable_NgoaiThuMucChoPhep_BiChan()
    {
        var policy = CreatePolicy();
        var outside = Path.Combine(Path.GetTempPath(), "evil.exe");

        Assert.Throws<UnsafeTargetException>(() => policy.EnsureAllowedExecutable(outside, "binaryPath"));
    }

    [Fact]
    public void EnsureAllowedExecutable_TrongThuMucNhungNgoaiDanhSachExe_BiChan()
    {
        var policy = CreatePolicy();

        // powershell.exe nam trong System32 va ton tai that - chi bi chan boi lop 7.
        // CO Y khong cho vi no nhan -EncodedCommand.
        var ex = Assert.Throws<UnsafeTargetException>(
            () => policy.EnsureAllowedExecutable(Path.Combine(System32, @"WindowsPowerShell\v1.0\powershell.exe"), "command"));

        Assert.Contains("powershell.exe", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureAllowedExecutable_Rong_BiChan(string? raw)
    {
        Assert.Throws<UnsafeTargetException>(() => CreatePolicy().EnsureAllowedExecutable(raw, "command"));
    }

    [Fact]
    public void ThuMucChoPhep_KhongKhopTienToNuaChung()
    {
        // "C:\WindowsEvil" KHONG duoc coi la nam trong "C:\Windows" - day la ly do
        // phai them dau '\' cuoi khi chuan hoa danh sach thu muc.
        var policy = new InputPolicy(new ManagementOptions
        {
            AllowedExecutableDirectories = [@"C:\Windows"],
        });

        Assert.All(policy.AllowedDirectories,
            d => Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), d));
    }

    // ---------------------------------------------------------------- Ten

    [Theory]
    [InlineData("WinSentinelDemo")]
    [InlineData("WinSentinel_1.2-x")]
    [InlineData(@"\WinSentinelDemo")] // dau '\' dau bi bo qua
    public void EnsureValidName_ChapNhanTenHopLe(string name)
    {
        CreatePolicy().EnsureValidName(name);
    }

    [Theory]
    [InlineData("Win Sentinel")]   // khoang trang
    [InlineData("WinSentinel;rm")] // dau cham phay
    [InlineData("WinSentinel$x")]
    [InlineData("")]
    [InlineData(null)]
    public void EnsureValidName_ChanTenSaiDinhDang(string? name)
    {
        Assert.Throws<UnsafeTargetException>(() => CreatePolicy().EnsureValidName(name));
    }

    [Fact]
    public void EnsureValidName_ChanTenQuaDai()
    {
        var policy = new InputPolicy(new ManagementOptions { MaxNameLength = 10 });

        Assert.Throws<UnsafeTargetException>(() => policy.EnsureValidName(new string('a', 11)));
    }

    // ---------------------------------------------------------------- Tham so

    [Fact]
    public void EnsureValidArguments_ChapNhanRong()
    {
        CreatePolicy().EnsureValidArguments(null);
        CreatePolicy().EnsureValidArguments("");
        CreatePolicy().EnsureValidArguments("/c echo hello");
    }

    [Fact]
    public void EnsureValidArguments_ChanQuaDai()
    {
        var policy = new InputPolicy(new ManagementOptions { MaxArgumentsLength = 5 });

        Assert.Throws<UnsafeTargetException>(() => policy.EnsureValidArguments("123456"));
    }

    [Fact]
    public void EnsureValidArguments_ChanKyTuDieuKhien()
    {
        Assert.Throws<UnsafeTargetException>(() => CreatePolicy().EnsureValidArguments("abc\0def"));
    }

    // ---------------------------------------------------------------- Tai khoan service

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LocalSystem")]
    public void EnsureAllowedServiceAccount_MacDinhLaNull(string? account)
    {
        // null = LocalSystem, dung mac dinh cua CreateServiceW.
        Assert.Null(InputPolicy.EnsureAllowedServiceAccount(account));
    }

    [Fact]
    public void EnsureAllowedServiceAccount_ChapNhanTaiKhoanDungSan()
    {
        Assert.Equal(@"NT AUTHORITY\LocalService",
            InputPolicy.EnsureAllowedServiceAccount(@"nt authority\localservice"));
    }

    [Theory]
    [InlineData(@"KAZYY\win10")]
    [InlineData(@"DOMAIN\Administrator")]
    public void EnsureAllowedServiceAccount_ChanTaiKhoanTuY(string account)
    {
        // Nhan tai khoan tuy y = phai nhan ca mat khau, tuc them mot duong cho
        // credential di qua web form vao CreateServiceW.
        Assert.Throws<UnsafeTargetException>(() => InputPolicy.EnsureAllowedServiceAccount(account));
    }

    // ---------------------------------------------------------------- Dependencies

    [Fact]
    public void BuildDependencyMultiSz_KetThucBangHaiNull()
    {
        var block = CreatePolicy().BuildDependencyMultiSz(["RpcSs", "LanmanWorkstation"]);

        Assert.Equal("RpcSs\0LanmanWorkstation\0\0", block);
    }

    [Fact]
    public void BuildDependencyMultiSz_RongThiNull()
    {
        Assert.Null(CreatePolicy().BuildDependencyMultiSz([]));
        Assert.Null(CreatePolicy().BuildDependencyMultiSz(null));
    }

    [Fact]
    public void BuildDependencyMultiSz_KiemTraTungTen()
    {
        Assert.Throws<UnsafeTargetException>(
            () => CreatePolicy().BuildDependencyMultiSz(["RpcSs", "ten; xau"]));
    }

    [Fact]
    public void BuildDependencyMultiSz_LaPhepNguocCuaReadMultiSz()
    {
        // Vong khep kin: ghi ra roi doc lai phai duoc dung danh sach ban dau.
        var input = new[] { "RpcSs", "http" };
        var block = CreatePolicy().BuildDependencyMultiSz(input)!;

        var ptr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(block);
        try
        {
            Assert.Equal(input, ServiceManager.ReadMultiSz(ptr));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }
    }

    // ---------------------------------------------------------------- StartBoundary

    [Fact]
    public void ParseStartBoundary_RongThiHenGioXa()
    {
        var result = InputPolicy.ParseStartBoundary(null);

        Assert.True(DateTime.Parse(result) > DateTime.Now.AddMonths(6));
    }

    [Fact]
    public void ParseStartBoundary_ChuanHoaVeDinhDangTaskScheduler()
    {
        Assert.Equal("2026-08-17T23:59:00", InputPolicy.ParseStartBoundary("2026-08-17T23:59"));
    }

    [Fact]
    public void ParseStartBoundary_SaiDinhDang_NemArgumentException()
    {
        // ArgumentException -> 400 (loi nhap lieu), khong phai UnsafeTargetException -> 403.
        Assert.Throws<ArgumentException>(() => InputPolicy.ParseStartBoundary("hom qua"));
    }
}
