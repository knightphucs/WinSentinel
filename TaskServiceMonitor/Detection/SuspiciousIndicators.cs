namespace TaskServiceMonitor.Detection;

/// <summary>
/// Nguồn sự thật DUY NHẤT về "cái gì bị coi là đáng ngờ": thư mục người dùng ghi
/// được, binary hay bị lạm dụng, cờ PowerShell, principal quyền cao, thư mục hệ
/// thống chuẩn.
///
/// Trước bước 11, kiến thức này nằm rải trong hai mảng private của
/// <c>RiskScorer</c> (<c>\Temp\</c>, <c>\AppData\</c>, <c>-enc</c>...). Gom về đây
/// để <c>RuleCatalog</c> và <c>RiskScorer</c> dùng chung — thêm một thư mục đáng
/// ngờ thì cả hai cùng thấy, không có đường nào lệch.
///
/// ⚠️ Đây là so khớp CHUỖI và ĐƯỜNG DẪN, không phải phân tích hành vi.
/// <c>cmd.exe /c &lt;bất cứ gì&gt;</c> vẫn lọt nếu tham số không khớp từ khoá nào.
/// </summary>
internal static class SuspiciousIndicators
{
    // ----------------------------------------------------- Thư mục ghi được

    /// <summary>
    /// Thư mục mà người dùng thường (không cần quyền admin) ghi được. Binary hợp lệ
    /// của task/service gần như không bao giờ nằm ở đây — đây là dấu hiệu persistence
    /// điển hình.
    ///
    /// Có CẢ dạng biến môi trường nguyên văn lẫn dạng đã giãn: task lưu đúng chuỗi
    /// người dùng gõ (<c>%TEMP%\a.exe</c>), còn service thường lưu đường dẫn đầy đủ.
    /// </summary>
    internal static readonly string[] WritableDirectoryFragments =
    [
        // Dạng biến môi trường - giữ nguyên văn trong định nghĩa task.
        "%TEMP%",
        "%TMP%",
        "%APPDATA%",
        "%LOCALAPPDATA%",
        "%PUBLIC%",
        "%USERPROFILE%",

        // Dạng đã giãn.
        @"\Temp\",
        @"\AppData\",
        @"\Users\Public",
        @"\Downloads\",
        @"\Recycle"
    ];

    /// <summary>
    /// Thư mục ghi được nhưng RẤT NHIỀU phần mềm hợp lệ dùng — chỉ nâng lên Medium,
    /// không phải High, nếu không tỉ lệ dương tính giả sẽ không chấp nhận được.
    /// </summary>
    internal static readonly string[] LowConfidenceDirectoryFragments =
    [
        @"\ProgramData\"
    ];

    /// <summary>
    /// Thư mục được coi là vị trí chuẩn của service/task hệ thống. Nằm ngoài đây thì
    /// là "vị trí không tiêu chuẩn" theo cách mentor mô tả.
    /// </summary>
    internal static readonly string[] StandardSystemDirectories =
    [
        @"C:\Windows\System32",
        @"C:\Windows\SysWOW64",
        @"C:\Windows\servicing",
        @"C:\Windows\Microsoft.NET",
        @"C:\Program Files\",
        @"C:\Program Files (x86)\"
    ];

    // ----------------------------------------------------- Binary bị lạm dụng

    /// <summary>
    /// LOLBin mà chỉ riêng việc một scheduled task gọi tới đã đủ đáng ngờ. Task hệ
    /// thống hợp lệ gần như không dùng nhóm này.
    /// </summary>
    internal static readonly string[] HighConfidenceLolBins =
    [
        "mshta.exe",
        "regsvr32.exe",
        "wscript.exe",
        "cscript.exe",
        "certutil.exe",
        "bitsadmin.exe",
        "curl.exe"
    ];

    /// <summary>
    /// LOLBin **cần thêm ngữ cảnh** mới báo động.
    ///
    /// ĐO TRÊN DỮ LIỆU THẬT (15.059 event): chấm theo tên thôi thì
    /// <c>rundll32.exe</c> sinh 6 cảnh báo High và <b>toàn bộ đều là dương tính giả</b> —
    /// đúng một task Microsoft (<c>\Microsoft\Windows\Application Experience\PcaPatchDbTask</c>)
    /// gọi <c>%windir%\system32\rundll32.exe</c>. Bỏ hẳn hai binary này khỏi danh sách
    /// thì mất luôn khả năng phát hiện, nên tách ra: chỉ báo khi tham số có dấu hiệu
    /// tải/chạy từ xa (xem <see cref="RemoteExecutionIndicators"/>).
    /// </summary>
    internal static readonly string[] ContextualLolBins =
    [
        "rundll32.exe",
        "msiexec.exe"
    ];

    /// <summary>Toàn bộ LOLBin, dùng khi quét chuỗi tham số.</summary>
    internal static readonly string[] LivingOffTheLandBinaries =
        [.. HighConfidenceLolBins, .. ContextualLolBins];

    /// <summary>
    /// Shell (<c>cmd</c>, <c>powershell</c>). Mentor nêu đích danh <c>cmd /c</c>, nhưng
    /// nhóm này <b>bắt buộc phải xét theo ngữ cảnh</b> — không được báo động chỉ vì
    /// thấy tên.
    ///
    /// Lý do: <c>cmd.exe</c> và <c>powershell.exe</c> là hai binary được task/service
    /// hợp lệ dùng nhiều nhất trên một máy Windows bình thường. Chấm High theo tên
    /// thôi thì tab Cảnh báo ngập ngay — đúng thảm hoạ mà
    /// <see cref="ContextualLolBins"/> (rundll32) đã dạy một lần: 6/6 dương tính giả.
    ///
    /// Vì vậy chỉ báo khi shell ĐI KÈM một dấu hiệu khác — xem
    /// <see cref="MatchContextualShell"/>.
    /// </summary>
    internal static readonly string[] ContextualShells =
    [
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "powershell_ise.exe"
    ];

    /// <summary>
    /// Toán tử nối lệnh. Một shell chạy MỘT lệnh đơn giản là chuyện thường; nối nhiều
    /// lệnh lại là dấu hiệu của chuỗi thao tác được dựng sẵn (tải → chạy → xoá dấu vết).
    ///
    /// CỐ Ý không có <c>&amp;</c> trần và <c>;</c>: chúng xuất hiện quá nhiều trong
    /// tham số hợp lệ (URL có <c>&amp;</c>, đường dẫn có <c>;</c>) nên sẽ gây dương
    /// tính giả. Chỉ nới nếu đo trên dữ liệu thật thấy an toàn.
    /// </summary>
    internal static readonly string[] CommandChainOperators =
    [
        "&&",
        "||",
        " | "
    ];

    /// <summary>
    /// Dấu hiệu "chạy thứ gì đó từ xa / từ nguồn bất thường" trong tham số dòng lệnh.
    /// Đây là thứ biến một lời gọi <c>rundll32</c> bình thường thành đáng ngờ.
    /// </summary>
    internal static readonly string[] RemoteExecutionIndicators =
    [
        "http://",
        "https://",
        "ftp://",
        @"\\",          // UNC - tai binary tu may khac
        ".hta",
        "javascript:",
        "vbscript:",
        "scrobj.dll"    // regsvr32/rundll32 chay scriptlet
    ];

    // ----------------------------------------------------- Cờ dòng lệnh

    /// <summary>
    /// Tham số hay gặp ở PowerShell bị lạm dụng: chạy ẩn cửa sổ, truyền lệnh mã hoá
    /// base64 để né việc bị đọc nội dung, bỏ qua execution policy, tải và chạy trực tiếp.
    /// </summary>
    /// <remarks>
    /// <c>-enc</c> là chuỗi con của <c>-EncodedCommand</c> và về lý thuyết khớp nhầm
    /// được với <c>-encoding</c>. Đã quét toàn bộ mẫu XML thật hiện có: không có dương
    /// tính giả. Giữ nguyên theo đề bài.
    /// </remarks>
    internal static readonly string[] SuspiciousCommandFragments =
    [
        "-enc",
        "-EncodedCommand",
        "-w hidden",
        "-windowstyle hidden",
        "-nop",
        "-noprofile",
        "-ExecutionPolicy Bypass",
        "-ep bypass",
        "IEX",
        "Invoke-Expression",
        "DownloadString",
        "DownloadFile",
        "FromBase64String"
    ];

    // ----------------------------------------------------- Principal quyền cao

    /// <summary>
    /// Giá trị <c>&lt;RunLevel&gt;</c> nghĩa là "chạy với quyền cao nhất có thể".
    /// </summary>
    internal const string HighestRunLevel = "HighestAvailable";

    /// <summary>
    /// Principal chạy task với quyền tối đa. Gồm cả SID lẫn tên — định nghĩa task có
    /// thể ghi bằng một trong hai dạng.
    /// </summary>
    internal static readonly string[] ElevatedPrincipals =
    [
        "S-1-5-18",                 // LocalSystem
        "S-1-5-32-544",             // BUILTIN\Administrators
        "LocalSystem",
        @"NT AUTHORITY\SYSTEM",
        "SYSTEM",
        @"BUILTIN\Administrators",
        "Administrators"
    ];

    /// <summary>Tài khoản service có quyền cao nhất.</summary>
    internal static readonly string[] HighPrivilegeServiceAccounts =
    [
        "LocalSystem",
        @".\LocalSystem",
        @"NT AUTHORITY\SYSTEM",
        "S-1-5-18"
    ];

    // ----------------------------------------------------- Hàm kiểm tra

    /// <summary>
    /// Đường dẫn có nằm trong thư mục người dùng ghi được không. So CẢ bản nguyên văn
    /// lẫn bản đã giãn biến môi trường — event forward từ máy khác thì bản giãn theo
    /// máy này không đáng tin, nên không được chỉ dựa vào nó.
    /// </summary>
    internal static string? MatchWritableDirectory(string? path)
    {
        var normalized = ExecutablePathParser.ExtractAndNormalize(path);

        if (normalized.Length == 0)
        {
            return null;
        }

        var expanded = ExecutablePathParser.ExpandLocally(normalized);

        return WritableDirectoryFragments.FirstOrDefault(fragment =>
            normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
            expanded.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Như <see cref="MatchWritableDirectory"/> nhưng so trên CẢ chuỗi thô, không bóc
    /// đường dẫn ra trước.
    ///
    /// Dùng cho tham số dòng lệnh: với <c>cmd.exe /c C:\Users\Public\a.bat</c> thì
    /// <c>Command</c> là <c>cmd.exe</c> hoàn toàn hợp lệ, đường dẫn đáng ngờ nằm trong
    /// <c>Arguments</c> — bóc "file thực thi" ra khỏi chuỗi tham số là vô nghĩa.
    /// </summary>
    internal static string? MatchWritableDirectoryInText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var expanded = ExecutablePathParser.ExpandLocally(text);

        return WritableDirectoryFragments.FirstOrDefault(fragment =>
            text.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
            expanded.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Thư mục ghi được nhưng độ tin cậy thấp (ProgramData).</summary>
    internal static string? MatchLowConfidenceDirectory(string? path)
    {
        var normalized = ExecutablePathParser.ExtractAndNormalize(path);

        if (normalized.Length == 0)
        {
            return null;
        }

        var expanded = ExecutablePathParser.ExpandLocally(normalized);

        return LowConfidenceDirectoryFragments.FirstOrDefault(fragment =>
            normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
            expanded.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Đường dẫn có nằm trong thư mục hệ thống chuẩn không. Rỗng/không bóc được thì
    /// coi là CHUẨN (trả <c>true</c>) — thà bỏ sót còn hơn báo động vì thiếu dữ liệu.
    /// </summary>
    /// <remarks>
    /// 🪤 BẪY ĐÃ DÍNH: so trên bản ĐÃ BÓC EXE thôi là SAI với mọi đường dẫn có khoảng
    /// trắng mà không có nháy. <c>ExtractExecutable</c> cắt tại dấu cách đầu tiên, nên
    /// <c>C:\Program Files\App\svc.exe</c> thành <c>C:\Program</c> — không còn khớp
    /// tiền tố <c>C:\Program Files\</c> nữa, và cả thư mục Program Files bị coi là
    /// "không tiêu chuẩn".
    ///
    /// Hai hậu quả thật: <c>SERVICE_NONSTANDARD_PATH</c> báo nhầm mọi service cài ở
    /// Program Files ghi đường dẫn không nháy, và <c>BlacklistLearner</c> — vốn dựa
    /// vào đúng hàm này làm rào chặn quan trọng nhất — sẽ ĐÓNG DẤU XẤU một binary hợp
    /// lệ. Test <c>KhongBaoGioHoc_BinaryTrongThuMucHeThong</c> bắt được chỗ này.
    ///
    /// Sửa bằng cách so THÊM bản chưa bóc exe. Câu hỏi ở đây là "đường dẫn này có nằm
    /// dưới thư mục X không", mà phép so tiền tố vốn không cần biết tên exe kết thúc ở
    /// đâu — nên bản chưa bóc mới là bản đúng, bản đã bóc chỉ là phòng khi chuỗi có
    /// tham số phía sau.
    /// </remarks>
    internal static bool IsInStandardSystemDirectory(string? path)
    {
        // Ban CHUA boc exe: dung cho đường dẫn có khoảng trắng, không nháy.
        var raw = ExecutablePathParser.Normalize(path);

        // Ban DA boc exe: dung khi chuoi mang ca tham so phia sau.
        var extracted = ExecutablePathParser.ExtractAndNormalize(path);

        if (raw.Length == 0 && extracted.Length == 0)
        {
            return true;
        }

        string[] candidates =
        [
            raw,
            extracted,
            ExecutablePathParser.ExpandLocally(raw),
            ExecutablePathParser.ExpandLocally(extracted)
        ];

        return StandardSystemDirectories.Any(dir =>
            candidates.Any(c => c.Length > 0 && c.StartsWith(dir, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Tên file thực thi có nằm trong nhóm LOLBin **độ tin cậy cao** không — nhóm mà
    /// riêng việc gọi tới đã đủ báo động.
    /// </summary>
    internal static string? MatchLivingOffTheLandBinary(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var fileName = ExecutablePathParser.FileName(commandLine);

        return HighConfidenceLolBins.FirstOrDefault(
            binary => binary.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// LOLBin nhóm "cần ngữ cảnh" (<c>rundll32</c>, <c>msiexec</c>) CỘNG VỚI dấu hiệu
    /// chạy từ xa trong tham số. Thiếu vế thứ hai thì không báo — xem
    /// <see cref="ContextualLolBins"/> để biết vì sao.
    /// </summary>
    internal static string? MatchContextualLolBin(string? commandLine, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var fileName = ExecutablePathParser.FileName(commandLine);

        var binary = ContextualLolBins.FirstOrDefault(
            b => b.Equals(fileName, StringComparison.OrdinalIgnoreCase));

        if (binary is null || string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        var indicator = RemoteExecutionIndicators.FirstOrDefault(
            i => arguments.Contains(i, StringComparison.OrdinalIgnoreCase));

        return indicator is null ? null : $"{binary} + {indicator}";
    }

    /// <summary>
    /// LOLBin xuất hiện ở BẤT KỲ đâu trong chuỗi, kể cả trong tham số
    /// (<c>cmd.exe /c mshta http://...</c>) — chỗ mà kiểm tra theo tên file bỏ sót.
    /// </summary>
    internal static string? MatchLivingOffTheLandAnywhere(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var match = LivingOffTheLandBinaries.FirstOrDefault(
                binary => value.Contains(binary, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Shell (<c>cmd</c>/<c>powershell</c>) CỘNG VỚI một dấu hiệu khác trong tham số.
    /// Thiếu vế thứ hai thì KHÔNG báo — xem <see cref="ContextualShells"/> để biết vì sao.
    ///
    /// Bốn loại ngữ cảnh làm một lời gọi shell trở nên đáng ngờ:
    /// <list type="number">
    ///   <item>tham số trỏ vào thư mục người dùng ghi được (<c>cmd /c %TEMP%\a.bat</c>)</item>
    ///   <item>tham số có dấu hiệu tải/chạy từ xa (<c>http://</c>, UNC)</item>
    ///   <item>tham số chứa cờ đáng ngờ (<c>-enc</c>, <c>IEX</c>...)</item>
    ///   <item>tham số nối nhiều lệnh (<c>&amp;&amp;</c>, <c>||</c>)</item>
    /// </list>
    ///
    /// Trả về chuỗi mô tả cả hai vế (<c>"cmd.exe + http://"</c>) để câu bằng chứng nói
    /// rõ vì sao lời gọi này bị chấm, thay vì chỉ nói "dùng cmd.exe".
    /// </summary>
    internal static string? MatchContextualShell(string? commandLine, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var fileName = ExecutablePathParser.FileName(commandLine);

        var shell = ContextualShells.FirstOrDefault(
            s => s.Equals(fileName, StringComparison.OrdinalIgnoreCase));

        if (shell is null || string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        var writable = MatchWritableDirectoryInText(arguments);
        if (writable is not null)
        {
            return $"{shell} + tham số trỏ '{writable}'";
        }

        var remote = RemoteExecutionIndicators.FirstOrDefault(
            i => arguments.Contains(i, StringComparison.OrdinalIgnoreCase));
        if (remote is not null)
        {
            return $"{shell} + '{remote}'";
        }

        var fragment = MatchSuspiciousCommandFragment(arguments);
        if (fragment is not null)
        {
            return $"{shell} + cờ '{fragment}'";
        }

        var chain = CommandChainOperators.FirstOrDefault(
            op => arguments.Contains(op, StringComparison.Ordinal));

        return chain is null ? null : $"{shell} + nối lệnh '{chain.Trim()}'";
    }

    /// <summary>Cờ dòng lệnh đáng ngờ xuất hiện ở bất kỳ giá trị nào truyền vào.</summary>
    internal static string? MatchSuspiciousCommandFragment(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var match = SuspiciousCommandFragments.FirstOrDefault(
                fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Principal có phải quyền cao không (so khớp CHÍNH XÁC, không phải chuỗi con).</summary>
    internal static bool IsElevatedPrincipal(string? principal)
    {
        if (string.IsNullOrWhiteSpace(principal))
        {
            return false;
        }

        var value = principal.Trim();

        return ElevatedPrincipals.Any(p => p.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tài khoản service có phải quyền cao nhất không.</summary>
    internal static bool IsHighPrivilegeServiceAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return false;
        }

        var value = account.Trim();

        return HighPrivilegeServiceAccounts.Any(a => a.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Start type có phải "tự khởi động cùng máy" không. Đổi sang auto start là bước
    /// then chốt để một service độc hại sống sót qua reboot.
    ///
    /// Nhận cả dạng chữ của 7045 (<c>auto start</c>) lẫn dạng đã chuẩn hoá từ mã số
    /// của 4697 (xem <c>WindowsEventParser.DescribeStartType</c>).
    /// </summary>
    internal static bool IsAutoStart(string? startType) =>
        !string.IsNullOrWhiteSpace(startType) &&
        startType.Contains("auto", StringComparison.OrdinalIgnoreCase);
}
