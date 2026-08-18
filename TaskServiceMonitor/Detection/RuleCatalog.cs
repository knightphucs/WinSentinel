using TaskServiceMonitor.Models;
using TaskServiceMonitor.Monitoring;

namespace TaskServiceMonitor.Detection;

/// <summary>
/// Danh mục rule phát hiện — nơi khai báo tập trung DUY NHẤT "hành vi nào đáng cảnh
/// báo", giống vai trò của <see cref="MonitoredEventIds"/> với danh sách Event ID.
///
/// Đối chiếu 1-1 với bảng ở <c>docs/hanh-vi-mapping.md</c> mục 4.2. Sửa ở đây thì
/// sửa cả tài liệu đó.
///
/// Toàn bộ rule trong danh mục này là HÀM THUẦN trên một event đơn lẻ. Rule cần nhìn
/// nhiều event (task tạo rồi xoá ngay, lệnh bị đổi so với lần trước) nằm ở
/// <see cref="CorrelationRules"/> vì chúng cần truy vấn DB.
/// </summary>
internal static class RuleCatalog
{
    // ---------------------------------------------------------------- Mã rule
    // Hằng số thay vì chuỗi rải rác: API lọc theo ruleId, test so theo ruleId.

    internal const string TaskCreated = "TASK_CREATED";
    internal const string TaskDeleted = "TASK_DELETED";
    internal const string TaskToggled = "TASK_TOGGLED";
    internal const string TaskUpdated = "TASK_UPDATED";
    internal const string TaskElevated = "TASK_ELEVATED";
    internal const string TaskWritableDir = "TASK_WRITABLE_DIR";
    internal const string TaskLolBin = "TASK_LOLBIN";
    internal const string TaskEncodedPs = "TASK_ENCODED_PS";

    internal const string ServiceInstalled = "SERVICE_INSTALLED";
    internal const string ServiceNonStandardPath = "SERVICE_NONSTANDARD_PATH";
    internal const string ServiceStartTypeChanged = "SERVICE_STARTTYPE_CHANGED";
    internal const string ServiceCrash = "SERVICE_CRASH";
    internal const string ServiceImagePathChanged = "SERVICE_IMAGEPATH_CHANGED";
    internal const string ServiceAccountChanged = "SERVICE_ACCOUNT_CHANGED";

    /// <summary>Lưới an toàn quét cả RawXml — xem <c>EvaluateSuspiciousRawContent</c>.</summary>
    internal const string SuspiciousRawContent = "SUSPICIOUS_RAW_CONTENT";

    // Hai mã dưới do CorrelationRules sinh ra, khai ở đây để mã rule nằm cùng một chỗ.
    internal const string TaskCommandChanged = "TASK_COMMAND_CHANGED";
    internal const string TaskCreateThenDelete = "TASK_CREATE_THEN_DELETE";

    /// <summary>
    /// Event ID của nhóm "service dừng bất thường".
    ///
    /// CỐ Ý KHÔNG có 7036 (state changed): ID đó báo cả chuyển sang Running lẫn
    /// Stopped, mà máy dev chưa phát ra nên chưa có mẫu thật để biết phân biệt bằng
    /// field nào. Đưa vào bây giờ là đoán cấu trúc — trái quy tắc dự án.
    /// </summary>
    internal static readonly int[] ServiceFailureEventIds = [7000, 7009, 7024, 7031, 7034];

    // ---------------------------------------------------------------- Danh mục

    internal static readonly DetectionRule[] All =
    [
        // ------------------------------------------------------ Scheduled Task

        new()
        {
            Id = TaskCreated,
            Name = "Task được tạo mới",
            TypicalSeverity = RiskLevel.Low,
            ObjectType = MonitoredObjectType.ScheduledTask,
            Description =
                "Một scheduled task mới được đăng ký. Bản thân việc này bình thường, " +
                "nhưng là bước đầu của phần lớn kỹ thuật duy trì truy cập.",
            RelatedEventIds = [4698, 106],
            Evaluate = evt => evt.EventId is 4698 or 106
                ? Hit(RiskLevel.Low, $"Task '{evt.ObjectName}' được tạo bởi {Actor(evt)}")
                : null
        },

        new()
        {
            Id = TaskDeleted,
            Name = "Task bị xoá",
            TypicalSeverity = RiskLevel.Low,
            ObjectType = MonitoredObjectType.ScheduledTask,
            Description =
                "Một scheduled task bị xoá. Đáng chú ý khi xảy ra ngay sau lúc tạo — " +
                "xem thêm rule TASK_CREATE_THEN_DELETE.",
            RelatedEventIds = [4699, 141],
            Evaluate = evt => evt.EventId is 4699 or 141
                ? Hit(RiskLevel.Low, $"Task '{evt.ObjectName}' bị xoá bởi {Actor(evt)}")
                : null
        },

        new()
        {
            Id = TaskToggled,
            Name = "Task bị bật / tắt",
            TypicalSeverity = RiskLevel.Low,
            ObjectType = MonitoredObjectType.ScheduledTask,
            Description = "Trạng thái bật/tắt của task bị thay đổi.",
            RelatedEventIds = [4700, 4701],
            Evaluate = evt => evt.EventId is 4700 or 4701
                ? Hit(RiskLevel.Low,
                    $"Task '{evt.ObjectName}' bị {(evt.EventId == 4700 ? "BẬT" : "TẮT")} bởi {Actor(evt)}")
                : null
        },

        new()
        {
            Id = TaskUpdated,
            Name = "Task bị sửa định nghĩa",
            TypicalSeverity = RiskLevel.Medium,
            ObjectType = MonitoredObjectType.ScheduledTask,
            Description =
                "Định nghĩa của một task sẵn có bị ghi đè. Nguy hiểm hơn tạo mới: kẻ tấn " +
                "công chiếm một task hệ thống đã có sẵn quyền và ít bị để ý. " +
                "Lưu ý event 4702 CHỈ mang bản mới, không mang bản cũ.",
            RelatedEventIds = [4702, 140],
            Evaluate = evt => evt.EventId is 4702 or 140
                ? Hit(RiskLevel.Medium, $"Task '{evt.ObjectName}' bị sửa bởi {Actor(evt)}")
                : null
        },

        new()
        {
            Id = TaskElevated,
            Name = "Task chạy với quyền cao",
            TypicalSeverity = RiskLevel.Medium,
            ObjectType = MonitoredObjectType.ScheduledTask,
            Description =
                "Task được cấu hình chạy dưới quyền SYSTEM/Administrators hoặc " +
                "RunLevel=HighestAvailable. Task chạy quyền cao mà trỏ tới file người " +
                "dùng thường ghi được là đường leo thang đặc quyền kinh điển.",
            RelatedEventIds = [4698, 4702],
            Evaluate = EvaluateTaskElevated
        },

        new()
        {
            Id = TaskWritableDir,
            Name = "Task chạy file từ thư mục người dùng ghi được",
            TypicalSeverity = RiskLevel.High,
            ObjectType = MonitoredObjectType.ScheduledTask,
            Description =
                "Task thực thi file nằm trong %TEMP%, %APPDATA%, C:\\Users\\Public, " +
                "Downloads... Task hợp lệ gần như không bao giờ chạy từ những nơi này " +
                "vì người dùng thường ghi đè được nội dung.",
            RelatedEventIds = [4698, 4702, 140, 200],
            Evaluate = EvaluateTaskWritableDir
        },

        new()
        {
            Id = TaskLolBin,
            Name = "Task gọi binary hay bị lạm dụng",
            TypicalSeverity = RiskLevel.High,
            ObjectType = MonitoredObjectType.ScheduledTask,
            Description =
                "Task chạy mshta/regsvr32/certutil/bitsadmin/wscript... — binary ký sẵn " +
                "của Windows hay được dùng để chạy code gián tiếp và né whitelist.",
            RelatedEventIds = [4698, 4702, 140, 200],
            Evaluate = EvaluateTaskLolBin
        },

        new()
        {
            Id = TaskEncodedPs,
            Name = "Task dùng PowerShell mã hoá / ẩn cửa sổ",
            TypicalSeverity = RiskLevel.High,
            ObjectType = MonitoredObjectType.ScheduledTask,
            Description =
                "Lệnh của task chứa -EncodedCommand, -w hidden, -ExecutionPolicy Bypass, " +
                "IEX, DownloadString... Đây là các cờ dùng để giấu nội dung lệnh và chạy " +
                "code tải từ mạng.",
            RelatedEventIds = [4698, 4702, 140, 200],
            Evaluate = EvaluateTaskEncodedPs
        },

        // ------------------------------------------------------------- Service

        new()
        {
            Id = ServiceInstalled,
            Name = "Service mới được cài",
            TypicalSeverity = RiskLevel.Low,
            ObjectType = MonitoredObjectType.Service,
            Description =
                "Một service mới được đăng ký. Một thao tác cài sinh HAI event ở hai " +
                "channel khác nhau: 7045 (System) và 4697 (Security).",
            RelatedEventIds = [7045, 4697],
            Evaluate = evt => evt.EventId is 7045 or 4697
                ? Hit(RiskLevel.Low,
                    $"Service '{evt.ObjectName}' được cài, chạy bằng tài khoản " +
                    $"'{evt.ServiceAccount ?? "(không rõ)"}', start type '{evt.StartType ?? "(không rõ)"}'")
                : null
        },

        new()
        {
            Id = ServiceNonStandardPath,
            Name = "Service chạy từ vị trí không tiêu chuẩn",
            TypicalSeverity = RiskLevel.High,
            ObjectType = MonitoredObjectType.Service,
            Description =
                "Binary của service không nằm trong System32/SysWOW64/Program Files. " +
                "Nằm hẳn trong thư mục người dùng ghi được (%TEMP%, AppData, Public) thì " +
                "là dấu hiệu persistence rõ ràng.",
            RelatedEventIds = [7045, 4697],
            Evaluate = EvaluateServiceNonStandardPath
        },

        new()
        {
            Id = ServiceStartTypeChanged,
            Name = "Start type của service bị đổi",
            TypicalSeverity = RiskLevel.Medium,
            ObjectType = MonitoredObjectType.Service,
            Description =
                "Kiểu khởi động của service thay đổi. Đổi sang auto start là bước then " +
                "chốt để một service sống sót qua reboot.",
            RelatedEventIds = [7040],
            Evaluate = EvaluateServiceStartTypeChanged
        },

        new()
        {
            Id = ServiceCrash,
            Name = "Service dừng đột ngột / không khởi động được",
            TypicalSeverity = RiskLevel.Medium,
            ObjectType = MonitoredObjectType.Service,
            Description =
                "Service kết thúc bất thường, treo lúc khởi động, hoặc không chạy được. " +
                "Hay xảy ra ngay sau khi binary của service bị thay thế.",
            RelatedEventIds = ServiceFailureEventIds,
            Evaluate = evt => ServiceFailureEventIds.Contains(evt.EventId)
                ? Hit(RiskLevel.Medium,
                    $"Service '{evt.ObjectName ?? "(không rõ)"}' — {evt.ActionDescription} (event {evt.EventId})",
                    "Kiểm tra xem binary của service có bị thay thế ngay trước đó không.")
                : null
        },

        new()
        {
            Id = ServiceImagePathChanged,
            Name = "Đường dẫn thực thi của service bị đổi",
            TypicalSeverity = RiskLevel.High,
            ObjectType = MonitoredObjectType.Service,
            Description =
                "binPath của một service sẵn có bị sửa. SCM KHÔNG phát event cho hành vi " +
                "này — chỉ bắt được qua audit registry (4657) hoặc qua ServiceConfigWatcher. " +
                "Đây là kỹ thuật duy trì truy cập không sinh event 7045.",
            RelatedEventIds = [4657],
            Evaluate = evt => EvaluateRegistryValueChange(
                evt, "ImagePath", ServiceImagePathChanged),
        },

        new()
        {
            Id = ServiceAccountChanged,
            Name = "Tài khoản chạy service bị đổi",
            TypicalSeverity = RiskLevel.Medium,
            ObjectType = MonitoredObjectType.Service,
            Description =
                "Tài khoản khởi chạy service bị sửa. Cũng không có event SCM — bắt qua " +
                "4657 trên registry value 'ObjectName' hoặc qua ServiceConfigWatcher. " +
                "Đổi sang LocalSystem là leo thang đặc quyền.",
            RelatedEventIds = [4657],
            Evaluate = evt => EvaluateRegistryValueChange(
                evt, "ObjectName", ServiceAccountChanged),
        },

        // --------------------------------------------------------- Lưới an toàn

        new()
        {
            Id = SuspiciousRawContent,
            Name = "Nội dung event chứa dấu hiệu đáng ngờ",
            TypicalSeverity = RiskLevel.High,
            ObjectType = MonitoredObjectType.Unknown,
            Description =
                "Dấu hiệu đáng ngờ (-EncodedCommand, -w hidden, DownloadString...) xuất " +
                "hiện trong XML thô nhưng không nằm ở field nào đã có rule riêng. Bắt " +
                "được cả những Event ID chưa có nhánh parse riêng.",
            RelatedEventIds = [],
            Evaluate = EvaluateSuspiciousRawContent
        }
    ];

    /// <summary>Bảng rule cho <c>GET /api/alerts/rules</c>.</summary>
    internal static IReadOnlyList<DetectionRuleDto> Describe() =>
    [
        .. All.Select(r => new DetectionRuleDto
        {
            Id = r.Id,
            Name = r.Name,
            TypicalSeverity = r.TypicalSeverity,
            ObjectType = r.ObjectType,
            Description = r.Description,
            RelatedEventIds = r.RelatedEventIds
        }),

        // Hai rule tương quan không nằm trong All (cần DB) nhưng vẫn phải xuất hiện
        // trong bảng rule, nếu không mentor đọc bảng sẽ thấy thiếu.
        new()
        {
            Id = TaskCommandChanged,
            Name = "Lệnh của task bị đổi so với lần ghi nhận trước",
            TypicalSeverity = RiskLevel.Medium,
            ObjectType = MonitoredObjectType.ScheduledTask,
            Description =
                "So định nghĩa mới với bản ghi gần nhất cùng tên task trong DB. Cần thiết " +
                "vì event 4702 chỉ mang bản MỚI, tự nó không cho biết đã đổi từ gì sang gì.",
            RelatedEventIds = [4702, 140]
        },
        new()
        {
            Id = TaskCreateThenDelete,
            Name = "Task vừa tạo đã bị xoá ngay",
            TypicalSeverity = RiskLevel.High,
            ObjectType = MonitoredObjectType.ScheduledTask,
            Description =
                "Task được tạo rồi bị xoá trong thời gian ngắn — dấu hiệu chạy một lần " +
                "rồi dọn dấu vết, thứ mà việc chấm điểm từng event rời rạc không thấy được.",
            RelatedEventIds = [4698, 4699, 106, 141]
        }
    ];

    /// <summary>Chạy toàn bộ rule thuần hàm trên một event.</summary>
    internal static IReadOnlyList<(DetectionRule Rule, RuleHit Hit)> Evaluate(WindowsMonitorEvent evt)
    {
        List<(DetectionRule, RuleHit)> hits = [];

        foreach (var rule in All)
        {
            // Một rule hỏng không được làm chết cả đường ghi event.
            RuleHit? hit;
            try
            {
                hit = rule.Evaluate(evt);
            }
            catch (Exception)
            {
                continue;
            }

            if (hit is not null)
            {
                hits.Add((rule, hit));
            }
        }

        return hits;
    }

    /// <summary>
    /// Mức rủi ro của event = mức CAO NHẤT trong các rule khớp. Đây là hàm mà
    /// <c>RiskScorer</c> gọi — nhờ vậy màu trên dashboard và danh sách cảnh báo không
    /// bao giờ nói hai chuyện khác nhau.
    /// </summary>
    internal static RiskLevel HighestSeverity(WindowsMonitorEvent evt)
    {
        var hits = Evaluate(evt);

        return hits.Count == 0
            ? RiskLevel.Low
            : hits.Max(h => h.Hit.Severity);
    }

    // ---------------------------------------------------------------- Rule cụ thể

    private static RuleHit? EvaluateTaskElevated(WindowsMonitorEvent evt)
    {
        if (evt.ObjectType != MonitoredObjectType.ScheduledTask)
        {
            return null;
        }

        var highestRunLevel = string.Equals(
            evt.TaskRunLevel, SuspiciousIndicators.HighestRunLevel, StringComparison.OrdinalIgnoreCase);

        var elevatedPrincipal = SuspiciousIndicators.IsElevatedPrincipal(evt.TaskRunAsUser);

        if (!highestRunLevel && !elevatedPrincipal)
        {
            return null;
        }

        var reason = highestRunLevel && elevatedPrincipal
            ? $"RunLevel={evt.TaskRunLevel}, chạy dưới '{evt.TaskRunAsUser}'"
            : highestRunLevel
                ? $"RunLevel={evt.TaskRunLevel}"
                : $"Chạy dưới '{evt.TaskRunAsUser}'";

        return Hit(
            RiskLevel.Medium,
            $"Task '{evt.ObjectName}' chạy quyền cao — {reason}",
            "Đối chiếu xem task có thực sự cần quyền này không.");
    }

    private static RuleHit? EvaluateTaskWritableDir(WindowsMonitorEvent evt)
    {
        if (evt.ObjectType != MonitoredObjectType.ScheduledTask || evt.TaskCommand is null)
        {
            return null;
        }

        // Đường dẫn nằm ở Command, nhưng cũng có thể nấp trong Arguments:
        // "cmd.exe /c C:\Users\Public\a.bat" - Command là cmd.exe hoàn toàn hợp lệ.
        var match = SuspiciousIndicators.MatchWritableDirectory(evt.TaskCommand)
            ?? SuspiciousIndicators.MatchWritableDirectoryInText(evt.TaskArguments);

        if (match is not null)
        {
            return Hit(
                RiskLevel.High,
                $"Task '{evt.ObjectName}' chạy: {Command(evt)} — khớp '{match}'",
                "Kiểm tra file đích: thư mục này người dùng thường ghi đè được.");
        }

        var lowConfidence = SuspiciousIndicators.MatchLowConfidenceDirectory(evt.TaskCommand);

        return lowConfidence is not null
            ? Hit(
                RiskLevel.Medium,
                $"Task '{evt.ObjectName}' chạy: {Command(evt)} — khớp '{lowConfidence}'",
                "ProgramData cũng ghi được nhưng nhiều phần mềm hợp lệ dùng — cần xác minh thêm.")
            : null;
    }

    private static RuleHit? EvaluateTaskLolBin(WindowsMonitorEvent evt)
    {
        if (evt.ObjectType != MonitoredObjectType.ScheduledTask || evt.TaskCommand is null)
        {
            return null;
        }

        // Ba duong, do tin cay giam dan:
        //  1. LOLBin nhom "chi can goi la dang ngo" (mshta, regsvr32, certutil...).
        //  2. LOLBin nhom "can ngu canh" (rundll32, msiexec) + dau hieu chay tu xa.
        //     Do tren du lieu that: cham theo ten thoi thi rundll32 sinh 6/6 duong tinh gia.
        //  3. LOLBin nap trong THAM SO (cmd.exe /c mshta http://...).
        var match = SuspiciousIndicators.MatchLivingOffTheLandBinary(evt.TaskCommand)
            ?? SuspiciousIndicators.MatchContextualLolBin(evt.TaskCommand, evt.TaskArguments)
            ?? SuspiciousIndicators.MatchLivingOffTheLandAnywhere(evt.TaskArguments);

        return match is not null
            ? Hit(
                RiskLevel.High,
                $"Task '{evt.ObjectName}' gọi '{match}' — {Command(evt)}",
                "Xem tham số đầy đủ trong raw XML để biết binary này được dùng làm gì.")
            : null;
    }

    private static RuleHit? EvaluateTaskEncodedPs(WindowsMonitorEvent evt)
    {
        if (evt.ObjectType != MonitoredObjectType.ScheduledTask)
        {
            return null;
        }

        var match = SuspiciousIndicators.MatchSuspiciousCommandFragment(
            evt.TaskCommand, evt.TaskArguments);

        return match is not null
            ? Hit(
                RiskLevel.High,
                $"Task '{evt.ObjectName}' dùng cờ '{match}' — {Command(evt)}",
                "Giải mã tham số -EncodedCommand (base64 UTF-16LE) để xem lệnh thật.")
            : null;
    }

    private static RuleHit? EvaluateServiceNonStandardPath(WindowsMonitorEvent evt)
    {
        if (evt.ObjectType != MonitoredObjectType.Service ||
            evt.EventId is not (7045 or 4697) ||
            string.IsNullOrWhiteSpace(evt.ImagePath))
        {
            return null;
        }

        var writable = SuspiciousIndicators.MatchWritableDirectory(evt.ImagePath);

        if (writable is not null)
        {
            return Hit(
                RiskLevel.High,
                $"Service '{evt.ObjectName}' chạy từ {evt.ImagePath} — khớp '{writable}'",
                "Service trỏ vào thư mục ghi được là dấu hiệu persistence điển hình.");
        }

        if (SuspiciousIndicators.IsInStandardSystemDirectory(evt.ImagePath))
        {
            return null;
        }

        return Hit(
            RiskLevel.Medium,
            $"Service '{evt.ObjectName}' chạy từ {evt.ImagePath} — ngoài thư mục hệ thống",
            "Xác minh phần mềm cài service này.");
    }

    private static RuleHit? EvaluateServiceStartTypeChanged(WindowsMonitorEvent evt)
    {
        if (evt.EventId != 7040)
        {
            return null;
        }

        var from = evt.PreviousStartType ?? "(không rõ)";
        var to = evt.StartType ?? "(không rõ)";

        // Đổi sang auto start = service sẽ tự chạy lại sau mỗi lần khởi động máy.
        var becameAutoStart =
            SuspiciousIndicators.IsAutoStart(evt.StartType) &&
            !SuspiciousIndicators.IsAutoStart(evt.PreviousStartType);

        // CỐ Ý giữ Medium kể cả khi đổi sang auto start, KHÔNG nâng lên High.
        // Lý do lấy từ dữ liệu thật: mẫu 7040 duy nhất thu được trên máy dev là BITS đi
        // 'demand start' → 'auto start' — hành vi hoàn toàn bình thường của Windows và
        // lặp lại rất thường xuyên (BITS, wuauserv...). Chấm High ở đây là tự tay làm
        // ngập tab Cảnh báo bằng dương tính giả, đúng thứ mà bước tinh chỉnh trên dữ
        // liệu thật phải chặn. Tín hiệu persistence thật sự nằm ở SERVICE_NONSTANDARD_PATH.
        return Hit(
            RiskLevel.Medium,
            $"Service '{evt.ObjectName}' đổi start type: {from} → {to}",
            becameAutoStart
                ? "Service nay tự chạy cùng máy — xác minh thay đổi này là có chủ đích."
                : null);
    }

    /// <summary>
    /// Lưới an toàn: dấu hiệu đáng ngờ xuất hiện Ở ĐÂU ĐÓ trong XML thô mà KHÔNG nằm
    /// trong các field đã có rule riêng.
    ///
    /// Vì sao cần: các rule ở trên đọc field đã parse (<c>TaskCommand</c>,
    /// <c>ImagePath</c>...), nên chúng bỏ sót những Event ID chưa có nhánh parse riêng
    /// (7034/7036 và mọi ID sẽ thêm sau) cùng những field không được bóc ra cột riêng.
    /// Bản <c>RiskScorer</c> trước bước 11 quét thẳng cả <c>RawXml</c> — bỏ hẳn cách đó
    /// là một bước lùi, nên giữ lại ở đây.
    ///
    /// Điều kiện "không nằm trong field đã xét" tránh việc một task dùng
    /// <c>-EncodedCommand</c> sinh ra hai cảnh báo trùng ý nghĩa.
    /// </summary>
    private static RuleHit? EvaluateSuspiciousRawContent(WindowsMonitorEvent evt)
    {
        var match = SuspiciousIndicators.MatchSuspiciousCommandFragment(evt.RawXml);

        if (match is null)
        {
            return null;
        }

        // Đã có rule khác bắt được ở field có cấu trúc thì thôi.
        var alreadyCovered = SuspiciousIndicators.MatchSuspiciousCommandFragment(
            evt.TaskCommand, evt.TaskArguments) is not null;

        if (alreadyCovered)
        {
            return null;
        }

        return Hit(
            RiskLevel.High,
            $"Event {evt.EventId} ('{evt.ObjectName ?? "(không rõ)"}') chứa dấu hiệu '{match}' " +
            "trong nội dung thô",
            "Mở raw XML để xem dấu hiệu này nằm ở field nào.");
    }

    /// <summary>
    /// Rule cho event 4657 (registry value modified) — đường DUY NHẤT có log Windows
    /// thật cho việc đổi binPath / đổi service account.
    ///
    /// ⚠️ CHƯA VERIFY BẰNG MẪU THẬT (máy dev chưa bật SACL trên khoá Services).
    /// Vì vậy rule đọc phòng thủ qua <c>evt.Data</c> — đây là dictionary chứa nguyên
    /// <c>&lt;EventData&gt;</c> mà nhánh dự phòng của parser luôn điền, nên tên field
    /// khác dự đoán thì rule chỉ đơn giản KHÔNG khớp, không bao giờ sinh dữ liệu sai.
    /// Có mẫu thật rồi thì viết nhánh parse riêng và siết lại chỗ này.
    /// </summary>
    private static RuleHit? EvaluateRegistryValueChange(
        WindowsMonitorEvent evt, string valueName, string ruleId)
    {
        if (evt.EventId != 4657)
        {
            return null;
        }

        if (!evt.Data.TryGetValue("ObjectName", out var keyPath) ||
            !keyPath.Contains(@"\Services\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!evt.Data.TryGetValue("ObjectValueName", out var changedValue) ||
            !changedValue.Equals(valueName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        evt.Data.TryGetValue("OldValue", out var oldValue);
        evt.Data.TryGetValue("NewValue", out var newValue);

        var serviceName = ServiceNameFromRegistryPath(keyPath);

        if (ruleId == ServiceImagePathChanged)
        {
            var writable = SuspiciousIndicators.MatchWritableDirectory(newValue);

            return Hit(
                RiskLevel.High,
                $"Service '{serviceName}' đổi binPath: {oldValue ?? "(không rõ)"} → {newValue ?? "(không rõ)"}" +
                (writable is not null ? $" — đường dẫn mới khớp '{writable}'" : string.Empty),
                "SCM không phát event cho thay đổi này; đây là log từ audit registry.");
        }

        var toHighPrivilege = SuspiciousIndicators.IsHighPrivilegeServiceAccount(newValue);

        return Hit(
            toHighPrivilege ? RiskLevel.High : RiskLevel.Medium,
            $"Service '{serviceName}' đổi tài khoản: {oldValue ?? "(không rõ)"} → {newValue ?? "(không rõ)"}",
            toHighPrivilege ? "Tài khoản mới có quyền cao nhất trên máy." : null);
    }

    // ---------------------------------------------------------------- Tiện ích

    private static RuleHit Hit(RiskLevel severity, string evidence, string? recommendation = null) =>
        new() { Severity = severity, Evidence = evidence, Recommendation = recommendation };

    private static string Actor(WindowsMonitorEvent evt) => evt.ActorAccount ?? "(không rõ)";

    /// <summary>Ghép Command + Arguments thành một chuỗi đọc được cho câu bằng chứng.</summary>
    private static string Command(WindowsMonitorEvent evt) =>
        string.IsNullOrWhiteSpace(evt.TaskArguments)
            ? evt.TaskCommand ?? "(không có lệnh)"
            : $"{evt.TaskCommand} {evt.TaskArguments}";

    /// <summary>
    /// <c>\REGISTRY\MACHINE\SYSTEM\ControlSet001\Services\Foo\ImagePath</c> → <c>Foo</c>.
    /// </summary>
    internal static string ServiceNameFromRegistryPath(string keyPath)
    {
        const string marker = @"\Services\";

        var index = keyPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (index < 0)
        {
            return "(không rõ)";
        }

        var rest = keyPath[(index + marker.Length)..];
        var slash = rest.IndexOf('\\');

        return slash > 0 ? rest[..slash] : rest;
    }
}
