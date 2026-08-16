# Đối chiếu dự án với tài liệu Microsoft về WEF / Event Subscription

Trả lời câu hỏi: *dự án này nằm ở phần nào trong hai tài liệu Microsoft, code hiện
thực từng bước ra sao, và còn thiếu gì?*

Hai tài liệu được đối chiếu:

1. **[Use Windows Event Forwarding to help with intrusion detection][doc-wef]** —
   tài liệu vận hành (IT/SecOps): dựng hệ thống thu log nhiều máy.
2. **[Subscribing to Events (Windows Event Log API)][doc-wes]** — tài liệu lập
   trình (Win32/WES): API để nhận event.

[doc-wef]: https://learn.microsoft.com/en-us/windows/security/operating-system-security/device-management/use-windows-event-forwarding-to-assist-in-intrusion-detection
[doc-wes]: https://learn.microsoft.com/en-us/windows/win32/wes/subscribing-to-events

---

## 1. Bức tranh tổng: dự án nằm ở đâu

WEF chia thành hai vai. Tài liệu 1 nói về **cách dựng đường ống**, tài liệu 2 nói
về **cách viết phần mềm đọc đầu ra của đường ống đó**.

```
Máy nguồn (WEF Client)          Máy collector (WEC Server)
─────────────────────           ─────────────────────────────────────
Audit Policy sinh event   ──►   channel ForwardedEvents
WinRM đẩy/kéo đi                        │
                                        ▼
                                ★ WinSentinel đọc từ đây ★
                                  EventLogWatcher / EventLogReader
```

**Dự án nằm hoàn toàn ở nửa phải.** Nó là *một phần mềm chạy trên máy WEC*, không
phải bản thân cơ chế WEF. Toàn bộ tài liệu 1 mô tả cách bơm dữ liệu **vào**
channel `ForwardedEvents`; dự án chỉ quan tâm channel đó (hoặc `Security`/`System`
local như hiện nay) đã có dữ liệu và cần đọc ra.

> Đây cũng là lý do đổi sang WEF **không cần sửa code**: chỉ đổi
> `EventLog:Channels` trong `appsettings.json` thành `["ForwardedEvents"]`.
> Mentor đã hạ WEF xuống mức tuỳ chọn — chỉ đọc log máy local là đủ.

---

## 2. Đối chiếu với tài liệu 2 — "Subscribing to Events"

Tài liệu này mô tả **hai mô hình nhận event**. Dự án dùng **cả hai**, cho hai mục
đích khác nhau — đây là điểm dễ nhầm nhất nên tách bảng riêng:

| Mục trong tài liệu | Win32 API | Dự án dùng ở đâu | Dùng để làm gì |
|---|---|---|---|
| **Push Subscriptions** | `EvtSubscribe` + `EVT_SUBSCRIBE_CALLBACK` | `EventWatcherService` (`EventLogWatcher`) | Realtime — Windows tự gọi lại khi có event mới |
| **Pull Subscriptions** | `EvtSubscribe` + event object, `EvtNext` | *không dùng* | — |
| *(Querying, không phải subscription)* | `EvtQuery` + `EvtNext` | `AdHocLogReader` (`EventLogReader`) | Duyệt log theo yêu cầu, đọc file `.evtx` |
| **Bookmarking Events** | `EvtCreateBookmark` | **cố ý KHÔNG dùng** — xem 2.1 | Resume sau restart |
| **Rendering Events** | `EvtRender(EvtRenderEventXml)` | `EventRecord.ToXml()` | Lấy XML thô để parse |
| **Formatting Event Messages** | `EvtFormatMessage` | `EventRecord.FormatDescription()` | Câu mô tả tiếng người — **bổ sung ở bước 8** |

Điểm cần nhớ: **`AdHocLogReader` KHÔNG phải một subscription.** Nó là query một
lần rồi thôi. Tài liệu gộp chung một trang nên dễ tưởng cùng loại, nhưng
subscription thì Windows chủ động báo về, còn query thì mình chủ động hỏi.

### 2.1. Vì sao không dùng `EventBookmark`

Tài liệu khuyên dùng bookmark để "đọc tiếp từ chỗ đã dừng". Dự án **không** dùng:
class `EventBookmark` của .NET cần `BinaryFormatter` để serialize ra đĩa, mà
`BinaryFormatter` đã bị gỡ khỏi .NET 8 theo mặc định (lỗ hổng deserialize).

Thay thế: dùng `EventRecordID` — số thứ tự duy nhất của bản ghi trong một channel
— vốn đã được lưu DB sẵn làm khoá chống trùng `IX_Events_Dedup`. Lúc khởi động,
`EventWatcherService` đọc `MAX(RecordId)` theo channel rồi nhúng vào XPath:

```
*[System[(EventID=4698 or ...) and (EventRecordID>12345)]]
```

Xem `MonitoredEventIds.BuildXPathFilter()` và `EventWatcherService.ResolveCursor()`.

### 2.2. `EvtFormatMessage` — lỗ hổng bước 8 vừa lấp

Đây là phần **quan trọng nhất** rút ra từ tài liệu 2, và là câu trả lời cho
"vì sao dự án không có Description như Event Viewer".

Event Viewer hiện hai loại thông tin hoàn toàn khác nguồn:

| Loại | Nguồn | Có trong XML không |
|---|---|---|
| Event ID, Level (số), Task (số), Keywords (hex), Computer, thời gian, `<EventData>` | Bản thân bản ghi | **Có** |
| **Description**, tên Level/Task Category/Opcode/Keywords | Render từ **message DLL của provider** | **KHÔNG** |

Nhóm thứ hai là kết quả của `EvtFormatMessage`: Windows lấy template message trong
DLL của provider rồi thay các giá trị `<EventData>` vào chỗ trống. Nó **không tồn
tại trong XML**, chỉ lấy được khi còn giữ handle event sống.

Trước bước 8, cả hai đường đọc log của dự án đều làm đúng một việc gây mất dữ liệu:

```csharp
var parsed = parser.Parse(record.ToXml());   // <-- record bi vut bo ngay sau day
```

Bước 8 sửa bằng `EventRecordDescriber` — gọi `FormatDescription()` **trước khi**
record bị dispose. Chi tiết ở `Monitoring/EventRecordDescriber.cs`.

---

## 3. Đối chiếu với tài liệu 1 — "WEF to assist in intrusion detection"

### 3.1. Push hay Pull

Tài liệu có hẳn mục *"Is WEF Push or Pull?"*:

| Kiểu | Cấu hình ở đâu | Yêu cầu |
|---|---|---|
| **Source Initiated (push)** | GPO đẩy xuống máy nguồn | Cần AD domain |
| **Collector Initiated (pull)** | Khai danh sách máy trên WEC | Máy nguồn cho phép đọc log từ xa (thêm tài khoản vào nhóm **Event Log Readers**) |

Dự án chọn **Collector Initiated** vì môi trường lab không có AD domain — Source
Initiated cần GPO push qua domain, không khả thi.

Lưu ý cách đọc từ "push/pull" cho đúng: nó nói về **cách WEF chuyển log giữa hai
máy**, hoàn toàn tách biệt với "push/pull subscription" của tài liệu 2 (nói về
API trong một tiến trình). Dự án là **Collector Initiated** ở tầng WEF, mà lại
dùng **push subscription** ở tầng API. Hai chữ "pull/push" ở hai tầng khác nhau,
không mâu thuẫn.

### 3.2. Baseline vs Targeted, và dự án tương ứng phần nào

Tài liệu chia hai subscription: **Baseline** (mọi máy) và **Targeted/Suspect**
(máy đang nghi ngờ). Bộ Event ID của dự án nằm gọn trong **Baseline**:

| Query trong Appendix E | Event ID | Dự án |
|---|---|---|
| `Query Id=3` — Task Scheduler | 106, 141, **142** | Có 106, 141 (+140, 200, 201 tự thêm). **Thiếu 142** |
| `Query Id=5` — Service install | 7000, 7045, 4697 | Có 7045, 4697. Thiếu 7000 |
| — | 4698-4702 (task audit) | Dự án **có thêm**, Baseline của Microsoft không lấy |

Nhận xét: dự án **hẹp hơn nhiều** so với Baseline (Baseline có ~40 query, phủ
process create, AppLocker, logon, PowerShell…), nhưng **sâu hơn ở nhánh Scheduled
Task** — 4698-4702 không có trong Baseline mà dự án lại parse chi tiết tới tận
`TaskContent` lồng bên trong. Đây là chỗ dự án đi xa hơn tài liệu, đúng phạm vi
mentor giao.

### 3.3. Định dạng chuyển tiếp — cái bẫy lớn nhất khi bật WEF

Tài liệu, mục *"What format is used for forwarded events?"*:

| Format | Nội dung | Kích thước |
|---|---|---|
| **Rendered Text** (mặc định) | XML **kèm `<RenderingInfo>`** — có sẵn câu Description và tên Level/Task/Opcode/Keywords đã dịch | Gấp 2-3 lần |
| **Events** (binary) — `wecutil ss "<sub>" /cf:Events` | Chỉ XML thuần | Nhỏ, gấp đôi sức chứa WEC |

**Hệ quả trực tiếp với dự án:** máy collector **không có message DLL của máy
nguồn**, nên `FormatDescription()` trên event forwarded sẽ trả null. Cách duy nhất
để có Description là `<RenderingInfo>` mà máy nguồn đã render sẵn — tức là **phải
giữ format mặc định Rendered Text**.

Chọn `/cf:Events` để tiết kiệm dung lượng = mất Description trên toàn bộ event
forwarded, và không có cách nào lấy lại.

Vì vậy `WindowsEventParser.ApplyDisplayFields()` đọc `<RenderingInfo>` trước, chỉ
khi không có mới hỏi tới record sống. Cùng một parser phục vụ được cả hai nguồn.

> ⚠️ **Nhánh `<RenderingInfo>` chưa xác minh bằng mẫu thật.** Máy dev chưa bật WEF
> nên chưa capture được XML forwarded. Code viết theo schema Microsoft công bố;
> fixture test là file **tự soạn** `renderinginfo_synthetic.xml`. Khi có máy WEF
> thật: capture một event từ `ForwardedEvents`, so với file đó, thay bằng mẫu thật
> rồi bỏ hậu tố `_synthetic`.

### 3.4. Bookmark: WEC và dự án giữ hai thứ khác nhau

Dễ nhầm, nên ghi rõ:

| Ai giữ | Giữ cái gì | Ở đâu |
|---|---|---|
| **WEC server** (Windows lo) | Vị trí đã đọc tới **của từng máy nguồn** | Registry |
| **WinSentinel** (dự án lo) | `MAX(RecordId)` **của từng channel trên máy collector** | PostgreSQL |

Hai tầng khác nhau, không xung đột. Cụ thể: `RecordId` là số thứ tự trong channel
`ForwardedEvents` **trên máy collector**, không thuộc về máy nguồn nào cả — nên
cursor phải nhóm theo `Channel`, **không** theo `Hostname`. Code đã làm đúng
(`EventWatcherService.LoadLastKnownRecordIdsAsync`), ghi lại đây để lần sau không
ai "sửa cho hợp lý" thành nhóm theo máy.

---

## 4. Còn thiếu gì — danh sách việc

Xếp theo mức đáng làm:

| # | Thiếu | Vì sao đáng quan tâm | Sửa thế nào |
|---|---|---|---|
| 1 | **Event ID 142** (Task registration deleted) | Nằm trong Baseline của Microsoft; app hiện bỏ sót một kiểu xoá task | Thêm vào `MonitoredEventIds.TaskSchedulerOperationalEventIds` — cần mẫu XML thật trước |
| 2 | **Channel `Eventlog-ForwardingPlugin/Operational`** | Channel báo sức khoẻ WEF. Không theo dõi thì WEF chết âm thầm mà không ai biết | Thêm vào `Channels` + `ChannelStatusRegistry` |
| 3 | **Nhóm `Event Log Readers`** (Appendix D) | Không thêm `Network Service` vào nhóm này thì WEF **không đọc được channel Security** của máy nguồn | Việc vận hành, không phải code |
| 4 | **Channel ACL** (Appendix C) | Vài channel (vd `Microsoft-Windows-CAPI2/Operational`) cần sửa ACL mới đọc được từ xa | `wevtutil sl <ch> /ca:"O:BAG:SYD:(A;;0x7;;;BA)..."` |
| 5 | **Audit policy còn hẹp** (Appendix A) | Dự án bật 2 subcategory; Appendix A liệt kê ~20 | Mở rộng dần theo nhu cầu, không cần làm hết |
| 6 | **Kích thước log máy nguồn** | Log máy nguồn CHÍNH LÀ vùng đệm khi mất kết nối. Để mặc định nhỏ thì mất event | `wevtutil sl <ch> /ms:102432768` |
| 7 | **Mất event âm thầm** | Tài liệu nói rõ: log máy nguồn bị ghi đè lúc mất kết nối thì **không có cảnh báo nào**, cũng không có dấu hiệu thủng chuỗi | Không sửa được ở tầng app — phải biết mà theo dõi kích thước log |
| 8 | ~~`TolerateQueryErrors`~~ | ~~Query nhiều channel dừng ở lỗi đầu tiên~~ | ✅ **Đã bật ở bước 8** (`AdHocLogReader`, `SavedLogStore`) |

---

## 5. Vì sao "System" và "Applications and Services Logs" trong app chưa có dữ liệu

Câu hỏi hay gặp nhất, và thực ra là **ba nguyên nhân hoàn toàn khác nhau** hay bị
gộp làm một:

### (a) Panel `System` gần như rỗng — ĐÚNG THIẾT KẾ, không phải lỗi

Pipeline curated **cố ý** chỉ lọc 15 Event ID trên 3 channel. Channel `System`
thật có hàng chục nghìn event; app chỉ nhận 4 cái: 7045, 7040, 7036, 7034. Máy dev
lại không phát 7036 (đã kiểm tra kỹ), nên panel rỗng cho tới khi thật sự có ai
cài/sửa service.

Bỏ bộ lọc đi thì DB và feed realtime ngập ngay lập tức — đó chính là lý do có bộ
lọc. Muốn xem mọi thứ thì dùng tab **"Duyệt log khác…"**, vốn sinh ra cho đúng việc
này (đọc theo yêu cầu, không lưu DB).

### (b) Applications and Services Logs — phần lớn channel MẶC ĐỊNH TẮT

Khác `System`/`Security` (luôn bật). Channel tắt thì đọc ra rỗng, không báo lỗi gì.

```powershell
# Xem trang thai
wevtutil gl "Microsoft-Windows-PowerShell/Operational" | Select-String enabled

# Bat (can quyen Administrator)
wevtutil sl "Microsoft-Windows-PowerShell/Operational" /e:true

# Tang kich thuoc (mac dinh nhieu channel chi 1MB, quay vong rat nhanh)
wevtutil sl "Microsoft-Windows-PowerShell/Operational" /ms:52428800

# Liet ke moi channel dang bat
wevtutil el | ForEach-Object { $_ } | Select-Object -First 20
```

Từ bước 8, dropdown chọn channel **ghi rõ "(đang TẮT)"** ngay cạnh tên, nên không
còn phải đoán.

Channel `Microsoft-Windows-TaskScheduler/Operational` mà dự án đang dùng cũng thuộc
loại này — đã ghi trong CLAUDE.md là phải bật tay.

### (c) Không có Description — nguyên nhân kỹ thuật, bước 8 đã sửa

Xem mục 2.2. Tóm tắt: `FormatDescription()` chưa từng được gọi, mà câu mô tả không
nằm trong XML nên không có cách nào lấy lại từ dữ liệu đã lưu.

**Giới hạn còn lại:** event lưu **trước** bước 8 sẽ mãi mãi `Description = null`.
`--backfill` tính lại được `Level`/`TaskCategoryId`/`Keywords` (những thứ có trong
`RawXml`) nhưng không dựng lại được Description. Chỉ event từ giờ trở đi mới có.

---

## 6. Bảng tra nhanh: khái niệm trong tài liệu → file trong repo

| Khái niệm | File |
|---|---|
| Push subscription, resume sau restart | `Monitoring/EventWatcherService.cs` |
| Query (`EvtQuery`), đọc `.evtx` | `Monitoring/AdHocLogReader.cs` |
| Cursor thay cho bookmark | `Monitoring/MonitoredEventIds.cs` → `BuildXPathFilter` |
| `EvtRender` → XML → model | `Monitoring/WindowsEventParser.cs` |
| `EvtFormatMessage` (Description) | `Monitoring/EventRecordDescriber.cs` |
| `<RenderingInfo>` của WEF | `WindowsEventParser.ApplyDisplayFields` |
| `EvtExportLog` (Save/Open Saved Log) | `Monitoring/SavedLogStore.cs` |
| Sức khoẻ subscription từng channel | `Monitoring/ChannelStatusRegistry.cs` |
