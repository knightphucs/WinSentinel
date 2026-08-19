# Phân rã hành vi → Event ID → Cảnh báo

Tài liệu trả lời trực tiếp hai câu hỏi mentor giao:

1. **Mỗi hành vi cần phân tích thì nằm ở Event ID / event log nào?**
2. **Gom những log đó lại thì cảnh báo cái gì lên webapp?**

Đọc kèm [wef-mapping.md](wef-mapping.md) (đối chiếu tài liệu WEF của Microsoft)
và [bao-cao-mentor.md](bao-cao-mentor.md) (báo cáo tổng thể dự án).

> **Quy ước đọc bảng**
> ✅ = đã bắt được và đã có nhánh parse riêng, có mẫu XML thật kiểm chứng
> ⚠️ = bắt được event nhưng phần phân tích còn thiếu
> ❌ = Windows **không phát** event cho hành vi này, phải lấp bằng cách khác

---

## 0. Tóm tắt cho người đọc vội

| | Số hành vi |
|---|---|
| Mentor nêu | 10 |
| Windows có event sẵn, dự án đã bắt đủ | 6 |
| Windows có event, dự án bắt được nhưng **chưa phân tích** | 1 |
| **Windows KHÔNG phát event nào** | 2 |
| Windows có event nhưng **máy dev không phát ra** | 1 |

Ba dòng cuối là phần đáng nói nhất và được giải thích kỹ ở mục 3.

---

## 1. Scheduled Task

### 1.1. Bảng phân rã

| Hành vi mentor nêu | Event ID | Channel | Field mang bằng chứng | TT |
|---|---|---|---|---|
| **Tạo mới task** | `4698` | Security | `TaskName`, `TaskContent` | ✅ |
| | `106` | TaskScheduler/Operational | `TaskName`, `UserContext` | ✅ |
| **Chỉnh sửa task** (đổi đường dẫn thực thi / đổi tham số) | `4702` | Security | **`TaskContentNew`** | ✅ |
| | `140` | TaskScheduler/Operational | `TaskName`, `UserName` | ✅ |
| **Lệnh đáng ngờ** (`powershell -enc`, `cmd /c`, `mshta.exe`) | `4698` / `4702` / `140` | | `TaskCommand`, `TaskArguments` (bóc từ XML lồng) | ⚠️ |
| **Thực thi từ thư mục bất thường** (`%TEMP%`, `C:\Users\Public`, `AppData`) | `4698` / `4702` | Security | `TaskCommand` | ⚠️ |
| | `200` | TaskScheduler/Operational | `ActionName` (đường dẫn **thực sự chạy**) | ⚠️ |
| **Thực thi với quyền cao** (`SYSTEM` / administrator) | `4698` / `4702` | Security | `TaskRunLevel` = `HighestAvailable`<br>`TaskRunAsUser` = `S-1-5-18` / `LocalSystem` | ⚠️ |
| **Xoá bỏ task** | `4699` | Security | `TaskName` | ✅ |
| | `141` | TaskScheduler/Operational | `TaskName`, `UserName` | ✅ |
| *(bổ sung — mentor không nêu)* Task **thực sự chạy** | `200` / `201` | TaskScheduler/Operational | `ActionName`, `ResultCode`, `TaskInstanceId` | ✅ |

### 1.2. Ba điều cần biết khi đọc log task

**a) `4702` chỉ mang bản MỚI, không mang bản cũ.**
Field là `TaskContentNew` — khác hẳn `TaskContent` của 4698-4701. Nghĩa là bản
thân một event 4702 **không trả lời được** câu "đường dẫn thực thi đã bị đổi từ gì
sang gì". Muốn biết thì phải so với bản ghi gần nhất **cùng `ObjectName`** trong
DB. Đây là lý do dự án cần một tầng tương quan chứ không chỉ chấm điểm từng event
rời rạc (rule `TASK_COMMAND_CHANGED`).

**b) Nội dung task là XML lồng trong XML.**
`TaskContent` bị escape trong thẻ `<Data>`. Phải parse tầng hai mới lấy được
`<Command>`, `<Arguments>`, `<Principal><RunLevel>`. Dự án đã làm
(`WindowsEventParser.EnrichFromTaskContent`).

**c) Không phải task nào cũng có `<Command>`.**
Rất nhiều task hệ thống dùng `<ComHandler>` với một CLSID và **không hề có đường
dẫn file**. Đây là dữ liệu thật, không phải parse lỗi — rule phát hiện phải bỏ qua
êm chứ không được coi là "thiếu dữ liệu".

**d) Cặp Security ↔ Operational không thay thế nhau.**
`4698/4702/4699` cho biết **ai đã thay đổi định nghĩa** task.
`200/201` cho biết task **thực sự đã chạy** cái gì và kết quả ra sao — Security
channel hoàn toàn không có thông tin này. Muốn chứng minh "task độc hại đã thực
thi" thì phải dùng `200/201`.

---

## 2. Windows Service

### 2.1. Bảng phân rã

| Hành vi mentor nêu | Event ID | Channel | Field mang bằng chứng | TT |
|---|---|---|---|---|
| **Tạo mới / cài đặt service** | `7045` | System | `ServiceName`, `ImagePath`, `ServiceType`, `StartType`, `AccountName` | ✅ |
| | `4697` | Security | `ServiceName`, `ServiceFileName`, `ServiceType`, `ServiceStartType`, `ServiceAccount` | ✅ |
| **Thay đổi cấu hình (ImagePath / binPath)** | **không có event SCM** | | | ❌ |
| | ↳ lấp bằng `4657` | Security | `ObjectValueName=ImagePath`, `OldValue`, `NewValue` | (mục 3.1) |
| | ↳ hoặc poll WinAPI | — | `QueryServiceConfig.lpBinaryPathName` | (mục 3.1) |
| **Thay đổi tài khoản khởi chạy** | **không có event SCM** | | | ❌ |
| | ↳ lấp bằng `4657` | Security | `ObjectValueName=ObjectName`, `OldValue`, `NewValue` | (mục 3.1) |
| | ↳ hoặc poll WinAPI | — | `QueryServiceConfig.lpServiceStartName` | (mục 3.1) |
| *(liên quan)* Thay đổi start type | `7040` | System | `param2` (cũ) → `param3` (mới), `param4` (tên ngắn) | ✅ |
| **Thực thi từ vị trí không tiêu chuẩn** | `7045` / `4697` | | `ImagePath` / `ServiceFileName` | ⚠️ |
| **Service crash / dừng đột ngột** | `7034` | System | `param1` = tên service | ❌ (mục 3.2) |
| | `7031` | System | `param1`, số lần lỗi, hành động khôi phục | ❌ chưa theo dõi |
| | `7024` | System | dừng kèm mã lỗi riêng của service | ❌ chưa theo dõi |
| | `7000` | System | không khởi động được | ❌ chưa theo dõi |
| | `7009` | System | quá thời gian chờ khi kết nối | ❌ chưa theo dõi |
| | `7036` | System | chuyển trạng thái (→ Stopped) | ❌ máy dev không phát |

### 2.2. Hai điều cần biết khi đọc log service

**a) Cài một service sinh HAI event ở HAI channel.**
`7045` (System, SCM ghi) và `4697` (Security, audit ghi). Không trùng lặp mà **bổ
sung nhau**, và **định dạng giá trị khác nhau**:

| | `4697` | `7045` |
|---|---|---|
| Start type | mã số — `ServiceStartType='3'` | chữ — `'demand start'` |
| Service type | hex — `ServiceType='0x10'` | chữ — `'user mode service'` |

Parser phải chuẩn hoá về một dạng, nếu không dashboard không gộp được. Dự án đã
làm (`DescribeServiceType`, `DescribeStartType`).

Lưu ý audit policy: `4697` dùng subcategory **`Security System Extension`**, còn
`4698-4702` dùng **`Other Object Access Events`**. Bật thiếu một cái là mất hẳn
nhóm tương ứng.

**b) `ImagePath` không phải đường dẫn thuần.**
Ba dạng thật hay gặp, rule so khớp phải xử lý được cả ba:

```
"C:\Program Files\App\svc.exe" -k netsvcs     ← có dấu nháy + tham số
\??\C:\Windows\System32\drivers\x.sys          ← tiền tố NT namespace
\SystemRoot\System32\drivers\y.sys             ← đường dẫn tương đối theo SystemRoot
```

So chuỗi thô lên nguyên `ImagePath` sẽ vừa sót vừa báo nhầm.

---

## 3. Ba chỗ Windows KHÔNG cho sẵn — và cách lấp

Đây là phần quan trọng nhất của tài liệu này.

### 3.1. Đổi `binPath` và đổi Service Account: không có event nào

Service Control Manager **không ghi event** khi `ChangeServiceConfig` sửa
`lpBinaryPathName` hay `lpServiceStartName`. `7040` — event duy nhất SCM phát khi
cấu hình đổi — **chỉ báo start type**, đúng 4 field `param1..param4` và không có
chỗ nào chứa đường dẫn hay tài khoản.

Điều này đáng chú ý về mặt an ninh: **đổi binPath của một service có sẵn là kỹ
thuật persistence kinh điển**, vừa không cần tạo service mới (không sinh 7045),
vừa thừa hưởng quyền và start type sẵn có. Nếu chỉ nghe 7045/7040 thì hành vi này
đi qua hoàn toàn im lặng.

Dự án lấp bằng **hai đường độc lập**, cố ý làm cả hai để đường này hỏng thì đường
kia vẫn bắt được:

**Đường A — log Windows thật: Event `4657` (Registry value modified)**

Cấu hình service nằm trong registry, nên sửa cấu hình = sửa registry value:

```
HKLM\SYSTEM\CurrentControlSet\Services\<tên>\ImagePath    ← binPath
HKLM\SYSTEM\CurrentControlSet\Services\<tên>\ObjectName   ← service account
HKLM\SYSTEM\CurrentControlSet\Services\<tên>\Start        ← start type
```

`4657` mang sẵn `OldValue` và `NewValue` — tức là **có luôn cả "đổi từ gì sang
gì"**, thứ mà 4702 của task không có.

Bật (quyền Administrator):

```powershell
# 1. Bật audit subcategory Registry
auditpol /set /subcategory:"Registry" /success:enable

# 2. Đặt SACL trên khoá Services (audit quyền Set Value cho Everyone)
#    Làm bằng regedit: chuột phải khoá Services > Permissions > Advanced >
#    Auditing > Add > Principal: Everyone > Type: Success >
#    Applies to: This key and subkeys > Advanced permissions: Set Value
```

> **Khối lượng 4657 được khống chế bằng PHẠM VI SACL, không phải bằng lọc phía
> app.** Đặt SACL đúng khoá `Services` thì lượng event rất nhỏ. Đặt SACL lên cả
> `HKLM\SYSTEM` là ngập log ngay.

**Đường B — lưới an toàn: poll + diff bằng WinAPI**

`ServiceConfigWatcher` định kỳ (mặc định 60 giây) gọi `EnumServicesStatusEx` +
`QueryServiceConfig` — đúng bộ hàm `services.msc` dùng — chụp snapshot
`ImagePath` / `Account` / `StartType` của mọi service, rồi so với snapshot lần
trước. Lệch ở đâu thì sinh cảnh báo ở đó.

| | Đường A (4657) | Đường B (poll) |
|---|---|---|
| Là log Windows thật | ✅ | ❌ (app tự sinh) |
| Có `OldValue`/`NewValue` | ✅ | ✅ (so snapshot) |
| Cần cấu hình OS | ✅ SACL + auditpol | ❌ chạy được ngay |
| Độ trễ | tức thời | ≤ 60 giây |
| Bắt được thay đổi lúc app tắt | ✅ (đọc bù bằng cursor) | ✅ (so với snapshot đã lưu DB) |

Snapshot được lưu xuống DB nên **restart app không mất mốc so sánh** — cùng tinh
thần với cursor `EventRecordID` ở bước 7. Lần chạy đầu tiên **chỉ lập baseline,
không sinh cảnh báo**, nếu không sẽ có khoảng 200 cảnh báo giả ngay lần bật đầu.

### 3.2. Service crash: `7034` không bao giờ phát trên máy này

Đã kiểm tra kỹ trên máy dev (Windows 11 ARM64): SCM ở đây chỉ ghi
`7023 / 7026 / 7030 / 7031 / 7040 / 7043 / 7045`. **Start/stop service không sinh
`7036`, và `7034` chưa từng xuất hiện lần nào.**

Nghĩa là nếu chỉ dùng đúng hai ID mentor liệt kê (`7034`, `7036`) thì mục "Service
Crash / Dừng đột ngột" sẽ **vĩnh viễn trống** trên máy này — demo không có gì để
xem.

Bổ sung 4 ID, trong đó `7031` là ID mà máy dev **thực sự có phát**:

| ID | Ý nghĩa | Vì sao thêm |
|---|---|---|
| `7031` | Service kết thúc bất thường, kèm số lần lỗi + hành động khôi phục | **Máy dev có phát.** Tín hiệu crash khả dụng nhất |
| `7024` | Service dừng kèm mã lỗi riêng của service | Crash "có kiểm soát" nhưng vẫn là lỗi |
| `7000` | Service không khởi động được | Binary bị xoá/thay → hay đi kèm sau khi binPath bị sửa |
| `7009` | Quá thời gian chờ khi kết nối tới service | Service treo lúc khởi động |

Giữ nguyên `7034`/`7036` trong danh sách — máy khác có thể phát, không việc gì
phải bỏ.

> **Quy tắc dự án: không đoán cấu trúc XML.** Bốn ID trên chỉ được viết nhánh
> parse riêng **sau khi** đã ép sinh event thật và lưu mẫu vào
> `TaskServiceMonitor.Tests/Fixtures/`. Trước đó chúng vẫn được thu và lưu, chỉ
> rơi vào nhánh dự phòng với `IsRecognized = false`.

Cách ép sinh mẫu thật:

```powershell
# 7031 / 7034 — giết tiến trình của service đang chạy
Get-CimInstance Win32_Service -Filter "Name='<tên>'" | Select-Object ProcessId
taskkill /f /pid <ProcessId>

# 7000 — trỏ service tới binary không tồn tại rồi start
sc.exe config WinSentinelCrashTest binPath= "C:\khong-ton-tai.exe"
sc.exe start WinSentinelCrashTest
```

---

## 4. Danh mục rule cảnh báo

Mỗi rule có một `RuleId` cố định, một tên tiếng Việt, một mức, và **câu bằng
chứng** trích từ chính event. Danh mục này cũng được app trả ra ở
`GET /api/alerts/rules` để hiện thành bảng ngay trong giao diện.

### 4.1. Vì sao có rule mức Low

Mentor liệt kê cả những hành vi bình thường (tạo task, cài service) — chúng **phải
xuất hiện trong danh sách** thì mới trả lời được câu "liệt kê các hành vi cần phân
tích". Nhưng nếu để chúng đẩy `RiskLevel` của event lên thì dashboard ngập màu.

Giải pháp: rule mức **Low = ghi nhận hành vi**, có mặt trong tab Cảnh báo nhưng
không làm event đổi màu. Tab Cảnh báo **mặc định lọc từ Medium trở lên**.

### 4.2. Bảng rule

| RuleId | Tên | Mức | Sinh ra từ |
|---|---|---|---|
| `TASK_CREATED` | Task được tạo mới | Low | 4698, 106 |
| `TASK_DELETED` | Task bị xoá | Low | 4699, 141 |
| `TASK_TOGGLED` | Task bị bật / tắt | Low | 4700, 4701 |
| `TASK_UPDATED` | Task bị sửa định nghĩa | Medium | 4702, 140 |
| `TASK_COMMAND_CHANGED` | Lệnh của task bị đổi so với lần ghi nhận trước | Medium → **High** nếu lệnh mới đáng ngờ | 4702/140 + tra DB |
| `TASK_ELEVATED` | Task chạy với quyền cao (SYSTEM / HighestAvailable) | Medium | `TaskRunLevel`, `TaskRunAsUser` |
| `TASK_WRITABLE_DIR` | Task chạy file từ thư mục người dùng ghi được | **High** | `TaskCommand`, `ActionName` |
| `TASK_LOLBIN` | Task gọi binary hay bị lạm dụng | **High** | `TaskCommand`, `TaskArguments` |
| `TASK_ENCODED_PS` | PowerShell mã hoá / ẩn cửa sổ / bỏ qua execution policy | **High** | `TaskCommand`, `TaskArguments` |
| `TASK_CREATE_THEN_DELETE` | Task vừa tạo đã bị xoá ngay (≤ 10 phút) | **High** | tương quan theo `ObjectName` |
| `SERVICE_INSTALLED` | Service mới được cài | Low | 7045, 4697 |
| `SERVICE_NONSTANDARD_PATH` | Service chạy từ ngoài thư mục hệ thống | Medium → **High** nếu ở thư mục ghi được | `ImagePath` |
| `SERVICE_IMAGEPATH_CHANGED` | Đường dẫn thực thi của service bị đổi | **High** | 4657 hoặc poller |
| `SERVICE_ACCOUNT_CHANGED` | Tài khoản chạy service bị đổi | Medium → **High** nếu đổi sang LocalSystem | 4657 hoặc poller |
| `SERVICE_STARTTYPE_CHANGED` | Start type bị đổi | Medium (xem ghi chú dưới) | 7040, 4657 |
| `SERVICE_CRASH` | Service dừng đột ngột / không khởi động được | Medium → **High** nếu service vừa bị cài/sửa trong 24h | 7031/7034/7024/7000/7009 |
| `SERVICE_SUSPICIOUS_COMMAND` | Lệnh của service chứa LOLBin / shell / cờ mã hoá | **High** | 7045, 4697 |
| `BLACKLIST_HIT` | Khớp dấu hiệu đã bị đóng dấu xấu | **High** | mọi Event ID |
| `SUSPICIOUS_RAW_CONTENT` | Nội dung event chứa dấu hiệu đáng ngờ (lưới an toàn) | **High** | mọi Event ID |

> **`SERVICE_STARTTYPE_CHANGED` giữ Medium kể cả khi đổi sang auto start.**
> Thiết kế ban đầu định nâng lên High vì auto start là bước để service sống sót qua
> reboot. Nhưng mẫu `7040` thật duy nhất thu được trên máy dev là **BITS đi
> `demand start` → `auto start`** — hành vi hoàn toàn bình thường của Windows và lặp
> lại rất thường xuyên (BITS, wuauserv...). Chấm High ở đây là tự tay làm ngập tab
> Cảnh báo. Tín hiệu persistence thật sự nằm ở `SERVICE_NONSTANDARD_PATH`.
> Đây là ví dụ cụ thể của nguyên tắc "chỉnh rule dựa trên dữ liệu thật" ở mục 5.c.

> **`SUSPICIOUS_RAW_CONTENT` là lưới an toàn**, không phải rule chính. Các rule ở trên
> đọc field đã parse, nên chúng bỏ sót những Event ID chưa có nhánh parse riêng
> (7034/7036 và mọi ID thêm sau) cùng những field không được bóc ra cột riêng. Bản
> `RiskScorer` trước bước 11 quét thẳng cả `RawXml` — bỏ hẳn cách đó là một bước lùi.
> Rule này chỉ kích hoạt khi dấu hiệu **không** nằm ở field đã có rule riêng, nên
> không sinh cảnh báo trùng ý nghĩa.

### 4.3. Dấu hiệu dùng để chấm

**Thư mục người dùng ghi được** (→ `TASK_WRITABLE_DIR`, `SERVICE_NONSTANDARD_PATH`):
`%TEMP%`, `%TMP%`, `%APPDATA%`, `%LOCALAPPDATA%`, `%PUBLIC%`, `%USERPROFILE%`,
`\Temp\`, `\AppData\`, `C:\Users\Public`, `\Downloads\`.
Khớp **cả dạng biến môi trường nguyên văn lẫn dạng đã giãn** — task lưu `%TEMP%\a.exe`
đúng như người dùng gõ, không tự giãn.
`C:\ProgramData` chỉ tính **Medium**: rất nhiều phần mềm hợp lệ dùng thư mục này.

**Binary hay bị lạm dụng** (→ `TASK_LOLBIN`): `mshta.exe`, `rundll32.exe`,
`regsvr32.exe`, `wscript.exe`, `cscript.exe`, `certutil.exe`, `bitsadmin.exe`,
`msiexec.exe`, `curl.exe`.

**Cờ PowerShell đáng ngờ** (→ `TASK_ENCODED_PS`): `-enc`, `-EncodedCommand`,
`-w hidden`, `-windowstyle hidden`, `-nop`, `-noprofile`,
`-ExecutionPolicy Bypass`, `IEX`, `DownloadString`, `FromBase64String`.

**Shell cần ngữ cảnh** (→ `TASK_LOLBIN`, `SERVICE_SUSPICIOUS_COMMAND`): `cmd.exe`,
`powershell.exe`, `pwsh.exe`. Mentor nêu đích danh `cmd /c`, nhưng nhóm này **chỉ báo
khi đi kèm** một trong bốn ngữ cảnh: tham số trỏ thư mục ghi được, dấu hiệu tải/chạy
từ xa (`http://`, UNC), cờ đáng ngờ ở trên, hoặc nối lệnh (`&&`, `||`, `|`).
Lý do y hệt `rundll32`: đây là hai binary mà task/service **hợp lệ** dùng nhiều nhất.

**Principal quyền cao** (→ `TASK_ELEVATED`): `RunLevel = HighestAvailable`, hoặc
`UserId` ∈ { `S-1-5-18`, `LocalSystem`, `NT AUTHORITY\SYSTEM`,
`BUILTIN\Administrators`, `S-1-5-32-544` }.

**Thư mục hệ thống được coi là chuẩn** (không báo `SERVICE_NONSTANDARD_PATH`):
`C:\Windows\System32`, `C:\Windows\SysWOW64`, `C:\Windows\servicing`,
`C:\Program Files`, `C:\Program Files (x86)`.

---

## 4.4. Blacklist — đóng dấu dấu hiệu đã gặp

Trả lời phần mentor giao: *"detect kỹ hơn trong những lần chúng thực thi với tác vụ
bất thường, từ đó đánh các dấu hiệu đó vào blacklist, rồi alert thẳng lên dashboard"*.

**Hai lớp, cố ý KHÔNG gộp:**

| | `SuspiciousIndicators` (code) | `Blacklist` (DB) |
|---|---|---|
| Nội dung | Dấu hiệu **tổng quát** (`%TEMP%`, `mshta.exe`, `-enc`) | Giá trị **cụ thể đã gặp trên máy này** |
| Sửa | Phải build lại | Sửa lúc đang chạy, qua UI |
| Kiểm chứng | Unit test trên 14 fixture thật | Đếm số lần khớp để rà dương tính giả |

Gộp lại là tạo hai nguồn sự thật cho cùng một thứ. Blacklist **không** được seed bằng
nội dung của `SuspiciousIndicators`.

**Bốn rào của phần tự học** (`BlacklistLearner`) — bỏ cái nào cũng đủ làm ngập tab
Cảnh báo:

1. Chỉ học từ hit mức **High**.
2. Chỉ học **đường dẫn cụ thể**, không học tên file trần, không học chuỗi con.
3. **KHÔNG BAO GIỜ** học binary trong `System32` / `SysWOW64` / `Program Files`.
4. Chỉ học từ 4 rule nói về một file cụ thể (xem `TeachingRules`).

### ⚠️ Số đo thật — vì sao `TASK_WRITABLE_DIR` KHÔNG được dạy blacklist

Lần chạy `--rebuild-alerts` đầu tiên trên **1.807 event thật**, rule đó dạy 2 đường
dẫn và **cả hai đều là dương tính giả**, chiếm **17/19** cảnh báo `BLACKLIST_HIT`:

```
%localappdata%\microsoft\onedrive\onedrivestandaloneupdater.exe   10 hit
...\onedrive\26.139.0720.0007\onedrivelauncher.exe                 7 hit
```

Cả hai là **OneDrive của Microsoft**. `%LOCALAPPDATA%` chính là nơi phần mềm per-user
hợp lệ cài đặt (OneDrive, Teams, Chrome, VS Code) — không liệt kê hết được.

Kết luận: **vị trí đủ để cảnh báo, không đủ để kết án vĩnh viễn**. Rule vẫn chạy và
vẫn hiện ở tab Cảnh báo, chỉ không được dạy blacklist. Sau khi bỏ:
`BLACKLIST_HIT` **19 → 2**, và 2 dòng còn lại là dương tính thật.

`SERVICE_NONSTANDARD_PATH` thì **giữ lại** dù cũng là tín hiệu vị trí: một *service*
chạy từ AppData bất thường hơn hẳn một *task*, vì service là phạm vi toàn máy và phải
có quyền admin mới cài — phần mềm per-user không cài service vào AppData.

Có 2 test khoá quyết định này lại (`KhongHoc_TuThuMucGhiDuoc_VIDUONGTINHGIA_ONEDRIVE`,
`VanHoc_ServiceChayTuAppData`) — nới lại mà không đo là test đổ.

---

## 5. ⚠️ Giới hạn — nói đúng mức

**a) Rule là so khớp chuỗi và đường dẫn, không phải sandbox, không phải AV.**
`cmd.exe /c <bất cứ gì>` vẫn lọt nếu tham số không khớp từ khoá nào. Tầng này thu
hẹp bề mặt và làm nổi hành vi đáng ngờ lên, **không đảm bảo bắt hết**.

**b) Whitelist và detection là hai lớp khác nhau, không thay thế nhau.**

| Lớp | Trả lời câu hỏi |
|---|---|
| `SafeNameGuard` | Được phép **ghi** lên tên này không? |
| `InputPolicy` | **Giá trị nhập vào** có hợp lệ không? |
| `RuleCatalog` (mới) | Hành vi **đã xảy ra** có đáng ngờ không? |

Hai lớp đầu chặn *app này* làm bậy. Lớp thứ ba phát hiện *bất kỳ ai* làm bậy,
kể cả bằng `schtasks.exe` hay `sc.exe` ngoài app.

Hệ quả khi demo: app **cố tình không tự tạo được** task trỏ vào
`C:\Users\Public` (whitelist chặn) — nên phải mô phỏng "kẻ tấn công" bằng công cụ
ngoài:

```powershell
schtasks /create /tn WinSentinelEvil /tr "C:\Users\Public\a.cmd" /sc once /st 23:00
```

**c) `rundll32.exe` là nguồn dương tính giả điển hình.**
Rất nhiều task hệ thống hợp lệ dùng nó. Danh sách LOLBin phải được chỉnh **dựa
trên số liệu thật** — chạy `--rebuild-alerts` trên toàn bộ event đã lưu rồi đếm
theo từng rule, không chốt danh sách bằng cảm tính.

**d) Chưa làm, cố ý hoãn**: signature virus (mentor sẽ giao sau). Không đoán trước
phạm vi.

---

## 6. Điều kiện để dữ liệu xuất hiện đầy đủ

Thiếu bất kỳ dòng nào dưới đây thì nhóm event tương ứng **không bao giờ sinh ra**,
và bảng cảnh báo sẽ trống mà không có lỗi nào báo ra.

```powershell
# Task 4698-4702
auditpol /set /subcategory:"Other Object Access Events" /success:enable

# Service 4697
auditpol /set /subcategory:"Security System Extension" /success:enable

# Registry 4657 (đổi binPath / service account) — cần thêm SACL, xem mục 3.1
auditpol /set /subcategory:"Registry" /success:enable

# Channel TaskScheduler/Operational (106/140/141/200/201) — mặc định TẮT
wevtutil sl "Microsoft-Windows-TaskScheduler/Operational" /e:true

# Kiểm tra lại
auditpol /get /category:*
```

App phải chạy quyền **Administrator** để đọc channel `Security`.

> Thiếu quyền đọc `Security` **không** ném lỗi lúc subscribe — nó nổi lên bất đồng
> bộ qua `EventRecordWrittenEventArgs.EventException` với thông báo
> `"The handle is invalid."`. Xem tab **Log Summary** trong app để biết channel nào
> thực sự đang nhận được event.
