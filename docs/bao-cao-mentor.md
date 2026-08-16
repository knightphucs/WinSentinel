# WinSentinel — Báo cáo dự án

Giám sát tập trung hành vi **Scheduled Task** và **Windows Service** qua Windows Event Log.

| | |
|---|---|
| Nền tảng | ASP.NET Core 8 (`net8.0-windows`), 1 project, không tách microservice |
| Lưu trữ | PostgreSQL 17 + EF Core 8 (Npgsql 8.0.11) |
| Realtime | SignalR |
| Frontend | HTML/JS thuần, không framework, không thư viện biểu đồ |
| Quy mô | 42 file C# (~6.050 dòng), 14 file JS (~3.200 dòng) |
| Kiểm thử | **180 unit test**, chạy `dotnet test` |
| Dữ liệu thật đang có | 12.055 event, phủ 2 ngày |

---

## 1. Tóm tắt trong một phút

App làm ba việc, khép thành một vòng:

1. **Nghe** Windows Event Log theo thời gian thực, bắt 15 Event ID liên quan tới Task/Service.
2. **Chuẩn hoá** XML thô → một model duy nhất → chấm điểm rủi ro → lưu DB → đẩy lên dashboard.
3. **Tự thao tác** Task/Service qua WinAPI. Thao tác đó sinh ra event Windows thật, chính app bắt lại và hiện lên — **vòng khép kín**, chứng minh cả đường đọc lẫn đường ghi đều đúng.

Điểm khác biệt so với việc chỉ mở Event Viewer: app **hiểu** nội dung event (bóc XML lồng trong XML để lấy lệnh mà task sẽ chạy), **chấm rủi ro**, **lưu lịch sử** và **chống mất event khi app tắt**.

---

## 2. Kiến trúc và luồng dữ liệu

```
Windows Event Log                    App                              Trình duyệt
─────────────────                    ───                              ───────────
Security ─┐
System   ─┼─► EventLogWatcher ─► WindowsEventParser ─► RiskScorer ─┐
TaskSched ┘   (push, realtime)      (XML → model)      (Low/Med/High)│
                     │                                              ▼
                     │                                        EventQueue
                     │                                    (Channel<T>, chặn 1000)
                     │                                              │
                     │                                              ▼
                     │                                  EventPersistenceService
                     │                                     │            │
                     │                                     ▼            ▼
                     │                                PostgreSQL    SignalR ─► Dashboard
                     │
                     └─► ChannelStatusRegistry (trạng thái từng channel)
```

| Thành phần | File | Method chính |
|---|---|---|
| Nghe log realtime | `Monitoring/EventWatcherService.cs` | `ExecuteAsync`, `TrySubscribe`, `OnEventRecordWritten` |
| Parse XML → model | `Monitoring/WindowsEventParser.cs` | `Parse(string)`, `Parse(string, EventRecord?)` |
| Lấy Description/Level | `Monitoring/EventRecordDescriber.cs` | `Apply`, `Try<T>` |
| Chấm rủi ro | `Monitoring/RiskScorer.cs` | `Score` |
| Hàng đợi | `Monitoring/EventQueue.cs` | `TryEnqueue` |
| Ghi DB + bắn SignalR | `Monitoring/EventPersistenceService.cs` | `ExecuteAsync` |
| Lưu, chống trùng | `Data/EventStorageService.cs` | `SaveAsync` |
| Model chuẩn hoá | `Models/WindowsMonitorEvent.cs` | — |

**Vì sao có hàng đợi ở giữa?** `OnEventRecordWritten` là callback **đồng bộ** do Windows gọi — chặn nó là nghẽn luồng nhận event. Handler chỉ parse rồi `TryEnqueue` và trả về ngay; việc ghi DB do `EventPersistenceService` làm ở luồng khác. Hàng đợi có chặn trên 1000 và **log Warning kèm số đếm khi rớt event** — mất dữ liệu giám sát thì phải kêu to.

---

## 3. Cơ chế theo dõi log

### 3.1. Push subscription, không phải polling

Dùng `EventLogWatcher` — .NET bọc `EvtSubscribe` với callback, tức **mô hình push** trong tài liệu [Subscribing to Events](https://learn.microsoft.com/en-us/windows/win32/wes/subscribing-to-events). Windows chủ động gọi lại khi có event mới, app không phải hỏi vòng.

Mỗi channel một watcher riêng (một watcher chỉ subscribe được đúng một channel) — `TrySubscribe`, `Monitoring/EventWatcherService.cs`.

### 3.2. Lọc ngay tại tầng OS bằng XPath

Không lấy hết rồi lọc trong app — lọc ngay lúc subscribe:

```
*[System[(EventID=4698 or EventID=4699 or …) and (EventRecordID>128374)]]
```

Dựng ở `MonitoredEventIds.BuildXPathFilter` (`Monitoring/MonitoredEventIds.cs`). **Mỗi channel một bộ Event ID riêng** (`ByChannel`) — 106/140/141/200/201 là số nhỏ, chung chung, lọc gộp sẽ trùng chéo.

> ⚠️ Bẫy đã dính: XPath ưu tiên `and` cao hơn `or`. Không bọc nhóm `EventID` trong ngoặc riêng thì điều kiện `EventRecordID` chỉ AND với vế `or` cuối cùng.

### 3.3. Resume sau restart — chống mất event khi app tắt

Đây là phần mentor gợi ý ("lấy được Log ID"). **`EventRecordID` chính là Log ID** — số thứ tự duy nhất của một bản ghi trong một channel, khác hẳn Event ID vốn chỉ là *loại* sự kiện.

| | Là gì | Ví dụ |
|---|---|---|
| Event ID | Loại sự kiện, lặp vô số lần | `4698` — xuất hiện 2.280 lần trong DB |
| EventRecordID (Log ID) | Số thứ tự duy nhất một bản ghi | `128374` |

Cách làm:

1. Khởi động → đọc `MAX(RecordId)` theo **Channel** từ DB — `LoadLastKnownRecordIdsAsync`.
2. So với log thật để phát hiện log bị `wevtutil cl` (xoá) — `ResolveCursor` dùng `EventLogSession.GetLogInformation`. Nếu bản ghi mới nhất **nhỏ hơn** cursor cũ thì số thứ tự đã đánh lại từ đầu → bỏ cursor, subscribe lại từ đầu.
3. Nhúng cursor vào XPath, bật `ReadExistingEvents = true` để đọc bù đúng phần lỡ.

**Không dùng `EventBookmark`** (cách chính thống trong tài liệu) vì class đó cần `BinaryFormatter` để lưu, mà .NET 8 đã gỡ `BinaryFormatter` theo mặc định.

Cursor nhóm theo **Channel chứ không theo Hostname** — `RecordId` là số thứ tự của channel trên máy chạy app, không thuộc về máy nguồn. Quan trọng khi chuyển sang `ForwardedEvents`.

📄 Kịch bản demo từng bước: [`docs/log-id-demo.md`](log-id-demo.md)

### 3.4. Chống ghi trùng

Unique index `IX_Events_Dedup` trên `(Hostname, Channel, RecordId)`, lọc `RecordId IS NOT NULL` — `Data/MonitorDbContext.cs`, `OnModelCreating`. Chạy lại app không nhân đôi dữ liệu.

### 3.5. Hai đường đọc log, tách hẳn nhau

| | Realtime (curated) | Theo yêu cầu (ad-hoc) |
|---|---|---|
| API | `EventLogWatcher` (push) | `EventLogReader` (query) |
| File | `EventWatcherService.cs` | `AdHocLogReader.cs` |
| Phạm vi | 15 Event ID, 3 channel | **mọi** channel, mọi Event ID |
| Lưu DB | Có | **Không** |
| SignalR | Có | Không |

Tách vì nếu gộp, duyệt channel `Application` (30.682 bản ghi) sẽ ngập DB và feed realtime ngay lập tức.

### 3.6. Description — thứ Event Viewer có mà đọc XML không có

Event Viewer hiện hai loại thông tin **khác nguồn**:

| Loại | Nguồn | Có trong XML? |
|---|---|---|
| Event ID, Level (số), Computer, `<EventData>` | Bản thân bản ghi | **Có** |
| **Description**, tên Level/Task Category/Opcode | Render từ **message DLL của provider** | **KHÔNG** |

Nhóm hai là kết quả `EvtFormatMessage` (.NET: `EventRecord.FormatDescription()`), **chỉ lấy được khi còn giữ `EventRecord` sống**. Cả hai đường đọc log ban đầu đều `record.ToXml()` rồi vứt record → mất vĩnh viễn.

Đã dồn vào `EventRecordDescriber.Apply`, nối qua **một** overload `WindowsEventParser.Parse(rawXml, record)` để không nhánh nào quên gọi.

> Ngoại lệ: event chuyển tiếp qua WEF ở chế độ "Rendered Text" mang sẵn `<RenderingInfo><Message>` trong XML — parser đọc luôn ở `ApplyDisplayFields`. Đây là lý do **không được** đổi WEF sang `/cf:Events`.

---

## 4. WinAPI — ba tầng khác nhau

Đây là phần mentor hay hỏi sâu nhất: **mỗi loại đối tượng Windows expose một kiểu API khác nhau**, không có một API chung.

| Đối tượng | Cách gọi | Lý do |
|---|---|---|
| Event Log | .NET `System.Diagnostics.Eventing.Reader` (bọc `wevtapi.dll`) | .NET có sẵn wrapper tốt |
| **Service** | **P/Invoke `advapi32.dll`** | Đúng bộ hàm `services.msc` dùng |
| **Scheduled Task** | **COM `Schedule.Service`** | Windows **không** expose Task Scheduler qua DLL phẳng |

### 4.1. Service — P/Invoke advapi32

`Management/Native/AdvApi32.cs` chỉ khai báo P/Invoke, **không có logic**. Handle bọc `SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid` để không rò khi có exception.

| Hàm | Dùng ở | Method |
|---|---|---|
| `OpenSCManagerW` / `OpenServiceW` / `CloseServiceHandle` | mọi thao tác | — |
| `EnumServicesStatusExW` | liệt kê | `ServiceManager.List` |
| `QueryServiceConfigW` | cấu hình cơ bản | `ServiceManager.TryReadConfig` |
| `QueryServiceConfig2W` | Description, DelayedAutoStart, Recovery | `ReadConfig2`, `TryReadRecoveryActions` |
| `ChangeServiceConfig2W` | đặt Description khi tạo | `ServiceManager.SetDescription` |
| `QueryServiceStatusEx` | ProcessId, ControlsAccepted | `QueryStatusProcess` |
| `CreateServiceW` / `DeleteService` | tạo/xoá | `Create`, `Delete` |
| `ChangeServiceConfigW` | đổi start type | `ChangeStartType` |
| `StartServiceW` / `ControlService` | start/stop | `Start`, `Stop` |

**Kỹ thuật gọi hai lần (double-call)** — đặc trưng của Win32: gọi lần đầu với buffer rỗng chỉ để hỏi cần bao nhiêu byte, cấp phát, rồi gọi lại. Thấy ở `List`, `TryReadConfig`, `ReadConfig2`.

**Bẫy MULTI_SZ**: `lpDependencies` là *nhiều chuỗi nối nhau*, mỗi chuỗi kết thúc bằng một null, cả khối kết thúc bằng null thứ hai. `Marshal.PtrToStringUni` chỉ trả về **chuỗi đầu tiên** → phải tự duyệt: `ServiceManager.ReadMultiSz`, chiều ngược lại `InputPolicy.BuildDependencyMultiSz`.
*Bằng chứng*: `Spooler` trả về `RPCSS, http` — hai mục, không phải một.

### 4.2. Scheduled Task — COM late binding

`Management/TaskManager.cs`, `ConnectService()`:

```csharp
var type = Type.GetTypeFromProgID("Schedule.Service");
dynamic service = Activator.CreateInstance(type)!;
service.Connect();
```

Dùng `dynamic` (IDispatch) nên **không cần nhúng type library**. Mọi method theo cùng khuôn: `ConnectService()` → `try { … } finally { Marshal.FinalReleaseComObject(service); }`.

**Hai cách tạo task, dự án làm cả hai** (theo tài liệu [TaskService](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskservice#methods)):

| Cách | Method | Ghi chú |
|---|---|---|
| **Dựng XML** rồi `RegisterTask` | `TaskManager.CreateOrUpdate` → `BuildTaskXml` | **Mặc định** |
| `NewTask()` → gán `Definition.*` → `RegisterTaskDefinition` | `TaskManager.CreateViaObjectModel` | Bật bằng `?api=objectmodel` |

**Vì sao XML là mặc định:**
1. XML sinh ra **chính là XML đọc lại được từ event 4698/4702** — vòng khép kín với parser.
2. `BuildTaskXml` là **hàm thuần** (model vào, XML ra) → test được không cần Windows. Hiện có 10 test riêng cho nó.

Cả hai đường đi qua **cùng** `TaskManager.Validate()` — không đường nào lách được whitelist.

### 4.3. Thao tác nào sinh Event ID nào

Bảng này là thước đo độ phủ — app theo dõi 15 ID, **tự sinh được 8**:

| Event ID | Thao tác trong app |
|---|---|
| 4698 Task created | Tạo task (tên chưa có) |
| 4699 Task deleted | Xoá task |
| 4700 / 4701 | Bật / Tắt task |
| 4702 Task updated | Ghi đè task đã có |
| 4697 + 7045 | Tạo service — **một thao tác sinh HAI event, hai channel khác nhau** |
| 7040 | Đổi start type |
| 7036 / 7034 | **Không tự sinh được** — máy dev không phát 7036; 7034 là service crash |

`RegisterTask` dùng cờ `TASK_CREATE_OR_UPDATE`: cùng một endpoint, tên chưa có → 4698, tên đã có → 4702.

---

## 5. Bảo mật

Phần này quan trọng vì **app chạy quyền Administrator** (bắt buộc để đọc channel `Security`). Một web UI chạy SYSTEM-adjacent là mục tiêu ngon.

### 5.1. Ba lớp rào, trả lời ba câu hỏi khác nhau

| Lớp | File | Câu hỏi | Method |
|---|---|---|---|
| 1 | `Management/SafeNameGuard.cs` | Được phép **ghi** lên tên này không? | `IsWritable`, `EnsureWritable` |
| 2 | `Management/InputPolicy.cs` | **Tên** có đúng định dạng không? | `EnsureValidName` |
| 3 | `Management/InputPolicy.cs` | Thứ **sẽ chạy** có được phép không? | `EnsureAllowedExecutable` |

**Lớp 1** — chỉ ghi được đối tượng có tên bắt đầu bằng tiền tố `WinSentinel` và nằm ở thư mục gốc. Chặn `..`, chặn thư mục con. **Đọc thì không giới hạn.** Chặn ở **tầng server**, không phải chỉ ẩn nút.

**Lớp 3 là phần bổ sung ở bước cuối, và nó vá một lỗ hổng thật:** trước đó rào chỉ kiểm tra *tên*, nên `BinaryPath` của service đi thẳng vào `CreateServiceW` với account `null` → service tạo ra chạy **LocalSystem**, start được ngay qua API. Một ô text trên trình duyệt thành thực thi quyền SYSTEM.

### 5.2. Whitelist đường dẫn — 7 bước, thứ tự là load-bearing

`InputPolicy.EnsureAllowedExecutable`:

| # | Bước | Bịt kiểu lách nào |
|---|---|---|
| 1 | Bóc exe khỏi tham số | `BinaryPath` của service là *cả dòng lệnh* |
| 2 | Giãn `%SystemRoot%` | Không giãn thì so thư mục vô nghĩa |
| 3 | `Path.GetFullPath` | `C:\Windows\System32\..\..\Users\x\evil.exe` — **khớp tiền tố chuỗi nhưng trỏ chỗ khác** |
| 4 | Chặn UNC | `\\máy-khác\share\evil.exe` |
| 5 | Bắt file tồn tại | Đăng ký đường dẫn rồi thả file vào sau |
| 6 | Trong thư mục cho phép | So chuỗi có `\` cuối để `C:\WindowsEvil` không khớp `C:\Windows` |
| 7 | Tên exe trong danh sách | `powershell.exe` nằm trong System32 và tồn tại thật — chỉ bị chặn ở bước này |

Cấu hình ở `appsettings.json` mục `Management`. Cố ý **không** có `powershell.exe` trong danh sách mặc định: nó nhận `-EncodedCommand`, đúng thứ mà `RiskScorer` của chính dự án chấm là High.

### 5.3. ⚠️ Giới hạn — nói đúng mức, đừng nói quá

**Whitelist chặn ĐƯỜNG DẪN, không chặn HÀNH VI.** Một khi `cmd.exe` được phép thì `cmd.exe /c <bất cứ gì>` vẫn chạy. Nó **thu hẹp bề mặt tấn công**, không phải sandbox.

Những thứ **chưa** có, biết rõ và có chủ đích:
- **Không có xác thực/phân quyền người dùng.** `RequireElevation()` kiểm tra token của *chính app*, không phải của người gọi. Ai truy cập được `localhost:5080` là thao tác được.
- Không có CSRF token, không rate limit.
- Tài khoản service chỉ nhận 3 tài khoản dựng sẵn (`InputPolicy.EnsureAllowedServiceAccount`) — cố ý không nhận tài khoản domain vì sẽ phải nhận cả mật khẩu qua web form.

Đây là app **lab một máy**, không phải sản phẩm nhiều người dùng. Muốn đưa ra thật thì việc đầu tiên là thêm xác thực.

### 5.4. Những chỗ khác đã cứng hoá

| Rủi ro | Cách chặn | File / Method |
|---|---|---|
| Path traversal khi tải file log | 3 lớp: `Path.GetFileName` → ép đuôi `.evtx` → so đường dẫn tuyệt đối | `SavedLogStore.Resolve` |
| SQL injection | EF Core tham số hoá toàn bộ | `Api/EventEndpoints.cs` |
| XML injection khi tạo task | `XElement` tự escape — có test chứng minh | `TaskManager.BuildTaskXml` |
| XSS | Mọi giá trị render bằng `textContent`, không `innerHTML` | `wwwroot/*.js` |
| Body rỗng → 500 | `RequireField` chặn trước khi null đi vào COM/Win32 | `ManagementEndpoints.RequireField` |
| Rò handle Win32 | `SafeHandle` | `AdvApi32.SafeServiceHandle` |

### 5.5. RiskScorer

`Monitoring/RiskScorer.cs`, `Score` — rule-based, không state. Chấm High khi thấy dấu hiệu đáng ngờ trong đường dẫn (`\Temp\`, `\AppData\`) hoặc trong RawXml (`-enc`, `-EncodedCommand`, `-w hidden`).

`--backfill` dùng lại **chính class này** chứ không viết lại rule bằng SQL — một nguồn sự thật duy nhất.

---

## 6. Giao diện và phân tích

| Màn hình | File | Ghi chú |
|---|---|---|
| Dashboard (4 card + 4 biểu đồ) | `wwwroot/insight.js`, `charts.js` | Biểu đồ **SVG tự vẽ**, không thư viện |
| Nhật ký sự kiện (4 panel) | `wwwroot/app.js`, `logsbrowse.js` | Bảng trên / chi tiết dưới, kéo được |
| Khung chi tiết | `wwwroot/detailpane.js` | Tab General/Details như Event Viewer |
| Tasks / Services | `wwwroot/manage.js`, `taskdialog.js` | Hộp thoại Create Task 5 tab, **modeless** |
| Lưu log | `wwwroot/logexport.js` | `.evtx` / XML / CSV |

**Chi tiết đáng nói khi trình bày:**

- **Biểu đồ vẽ tay bằng SVG** — không kéo thêm Chart.js. Màu dùng biến `--chart-*` riêng, không tái dùng `--risk-*-bg` (mấy biến đó là nền badge nhạt, vẽ cột chồng lên nhau gần như vô hình).
- **Hộp thoại Create Task là modeless** — không có lớp phủ, vẫn thao tác được bảng phía sau, kéo di chuyển được như cửa sổ thật.
- **Lưu event đang chọn** — Ctrl/Shift+click chọn nhiều dòng, xuất `.evtx` bằng XPath lọc theo `EventRecordID` (`SavedLogStore.BuildRecordIdXPath`). Đã verify: chọn 4 dòng → mở lại file đúng 4 event.
- **Hai chế độ nguồn** cho 3 panel curated: "App đã bắt" (mảng client) và "Toàn bộ channel" (đọc live như Event Viewer).

---

## 7. Kiểm thử

**180 test**, `dotnet test`. Chạy trên **mẫu XML thật** lấy từ máy dev, nhúng vào assembly (`EmbeddedResource`) nên không phụ thuộc thư mục `samples/` vốn bị gitignore.

| Bộ test | File | Số test | Phủ gì |
|---|---|---|---|
| **Whitelist** | `InputPolicyTests.cs` | 37 | `..`, UNC, env var, exe ngoài danh sách, MULTI_SZ, tài khoản service |
| Parser | `WindowsEventParserTests.cs` | 29 | 10 mẫu XML thật, từng Event ID |
| Chấm rủi ro | `RiskScorerTests.cs` | 21 | Rule Low/Medium/High |
| Rào tên | `SafeNameGuardTests.cs` | 21 | Tiền tố, `..`, thư mục con |
| Lưu log | `SavedLogStoreTests.cs` | 20 | Path traversal, XPath theo RecordID |
| Map mã Win32/COM | `ManagementDescribeTests.cs` | 19 | Action type, run level, recovery, `ReadMultiSz` |
| **Dựng XML task** | `BuildTaskXmlTests.cs` | 10 | Nhiều trigger/action, Principal, chống XML injection |
| Field hiển thị | `EventDisplayFieldsTests.cs` | 9 | Level/Task/Keywords, nhánh `<RenderingInfo>` |
| Chuyển đổi jsonb | `EventDataJsonConverterTests.cs` | 6 | `Data` ↔ jsonb |
| Trạng thái channel | `ChannelStatusRegistryTests.cs` | 5 | **Bắt đúng lỗi đua đã sửa** |
| XPath filter | `MonitoredEventIdsTests.cs` | 3 | `BuildXPathFilter`, cursor |

**Ba lỗi thật do chính test/review phát hiện:**

1. **Lỗi đua trong `ChannelStatusRegistry`** — `TrySubscribe` bật watcher *rồi mới* gọi `MarkSubscribed`, mà hàm đó ghi đè với `EventsReceived: 0`. Event đọc bù bắn ngay tại `Enabled = true` trên thread khác → số đếm bị xoá sạch, panel báo "đã subscribe nhưng chưa có event" dù event đã vào DB. Sau khi sửa: `eventsReceived: 625, caughtUpCount: 625`.
2. **Mất dữ liệu khi sửa task** — `POST /api/tasks` là ghi đè toàn bộ, mà form sửa để trắng `arguments`/`startBoundary` → bấm "Cập nhật" là mất arguments và dời trigger sang 1 năm sau.
3. **Lỗ hổng whitelist** — mục 5.1.

---

## 8. Câu hỏi mentor có thể hỏi

**"Sao không dùng Python cho nhanh?"**
`EventLogWatcher` là API .NET native. Python phải qua `pywin32`, mà máy dev là Windows ARM64 — rủi ro wheel không có sẵn. Ngoài ra tái dùng được kinh nghiệm ASP.NET Core/SignalR/EF Core.

**"Sao Task dùng COM mà Service dùng P/Invoke?"**
Windows không expose Task Scheduler qua DLL phẳng — chỉ có COM `Schedule.Service`. Service thì ngược lại, `advapi32.dll` là API chính thống, đúng bộ hàm `services.msc` dùng.

**"Làm sao biết không mất event khi app tắt?"**
Cursor `EventRecordID` + đọc bù. Demo được: tắt app → tạo task → bật lại → badge `↺ khôi phục N` trong Log Summary. Kèm `IX_Events_Dedup` chặn ghi trùng. Kịch bản đầy đủ ở `docs/log-id-demo.md`.

**"Nếu có 100 máy thì sao?"**
Đổi `EventLog:Channels` thành `["ForwardedEvents"]`, **không sửa code**. WEF giải quyết bài toán nhiều máy ở tầng OS; app chỉ đọc một nguồn local. Chi tiết + phần còn thiếu: `docs/wef-mapping.md`.

**"Bảo mật thế nào?"**
Ba lớp rào ở mục 5. Và nói thẳng giới hạn: whitelist chặn đường dẫn chứ không chặn hành vi, và **chưa có xác thực người dùng** — đây là app lab một máy.

**"Sao Event Viewer có mô tả mà app lúc đầu không có?"**
Vì mô tả không nằm trong XML — nó là kết quả render message DLL của provider (`EvtFormatMessage`). Mất `EventRecord` là mất vĩnh viễn. Mục 3.6.

**"RecordID có phải Event ID không?"**
Không. Event ID là *loại*; EventRecordID là *số thứ tự duy nhất* — chính là "Log ID". Mục 3.3.

**"Test có chạy được trên máy khác không?"**
Có. Mẫu XML nhúng vào assembly; các test whitelist dùng `System32` qua `Environment.GetFolderPath` chứ không hardcode `C:\Windows`.

---

## 9. Giới hạn đã biết & hướng tiếp

| Giới hạn | Vì sao |
|---|---|
| **Chưa có xác thực người dùng** | App lab một máy. Việc đầu tiên phải làm nếu đưa ra thật |
| 7036/7034 chưa có nhánh parse riêng | Máy dev không phát 7036; không ép ra 7034 sạch sẽ. **Không đoán cấu trúc** — chờ mẫu thật |
| Nhánh `<RenderingInfo>` chưa verify | Chưa bật WEF nên chưa có mẫu thật; fixture là file **tự soạn**, đã ghi rõ |
| `ExportLogAndMessages` lỗi trên máy này | Bug Windows 11 ARM64: luôn ném "The directory name is invalid.". Đã fallback giữ file, báo rõ lên UI |
| Card Dashboard tính trên ~200 event client | Cố ý, để khớp số với nhau. Biểu đồ thì query toàn DB — có ghi chú phân biệt |
| Thiếu Event ID 142 | Baseline của Microsoft có; cần mẫu thật trước khi thêm |

**Mentor đã giao, chưa làm (cố ý hoãn):** phân tích tương quan hành vi (nhìn *chuỗi* event thay vì từng event rời — ví dụ "task vừa tạo đã bị xoá ngay"), và nghiên cứu signature virus.

---

## 10. Tra cứu nhanh — khái niệm → code

| Khái niệm | File | Method |
|---|---|---|
| Push subscription | `Monitoring/EventWatcherService.cs` | `TrySubscribe`, `OnEventRecordWritten` |
| Cursor / resume | `Monitoring/EventWatcherService.cs` | `LoadLastKnownRecordIdsAsync`, `ResolveCursor` |
| XPath filter | `Monitoring/MonitoredEventIds.cs` | `BuildXPathFilter`, `ByChannel` |
| XML → model | `Monitoring/WindowsEventParser.cs` | `Parse`, `EnrichScheduledTask`, `ApplyDisplayFields` |
| Description (`EvtFormatMessage`) | `Monitoring/EventRecordDescriber.cs` | `Apply` |
| Chấm rủi ro | `Monitoring/RiskScorer.cs` | `Score` |
| Hàng đợi + ghi DB | `Monitoring/EventQueue.cs`, `EventPersistenceService.cs` | `TryEnqueue`, `ExecuteAsync` |
| Chống trùng | `Data/MonitorDbContext.cs` | `OnModelCreating` → `IX_Events_Dedup` |
| Duyệt log bất kỳ / đọc `.evtx` | `Monitoring/AdHocLogReader.cs` | `ReadAsync`, `ReadByXPathAsync` |
| Lưu / mở `.evtx` | `Monitoring/SavedLogStore.cs` | `Export`, `ExportSelected`, `Resolve` |
| Trạng thái channel | `Monitoring/ChannelStatusRegistry.cs` | `MarkSubscribed`, `MarkEventReceived` |
| Rào tên | `Management/SafeNameGuard.cs` | `EnsureWritable` |
| **Whitelist đầu vào** | `Management/InputPolicy.cs` | `EnsureAllowedExecutable` |
| Service (P/Invoke) | `Management/ServiceManager.cs`, `Native/AdvApi32.cs` | `List`, `Detail`, `Create`, `ReadMultiSz` |
| Task (COM) | `Management/TaskManager.cs` | `Detail`, `CreateOrUpdate`, `BuildTaskXml`, `CreateViaObjectModel` |
| API event | `Api/EventEndpoints.cs` | `GetEvents`, `GetSummary` |
| API quản lý + overview | `Api/ManagementEndpoints.cs` | `GetOverview`, `RequireElevation` |
| API duyệt/lưu log | `Api/LogBrowseEndpoints.cs` | `Browse`, `ExportEvents` |
| Realtime | `Realtime/MonitorHub.cs`, `EventNotifier.cs` | — |

### Lệnh hay dùng khi demo

```powershell
dotnet test                                              # 180 test
dotnet run --project TaskServiceMonitor                  # chạy app (cần Administrator)
dotnet run --project TaskServiceMonitor -- --parse-samples   # test parser, không cần DB
dotnet run --project TaskServiceMonitor -- --backfill        # tính lại cột dẫn xuất

wevtutil sl "Microsoft-Windows-TaskScheduler/Operational" /e:true   # bật channel
wevtutil gl "<tên channel>"                                        # xem channel bật chưa
```

### Tài liệu kèm theo

| File | Nội dung |
|---|---|
| `CLAUDE.md` | Quyết định kiến trúc, bẫy đã dính, quy ước code |
| `docs/wef-mapping.md` | Đối chiếu với 2 tài liệu WEF/WES của Microsoft |
| `docs/log-id-demo.md` | Kịch bản demo Log ID / Log Summary |
