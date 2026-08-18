# Trình bày bước 11 — Phân rã hành vi và tầng Cảnh báo

Bản tóm tắt để đọc trước khi gặp mentor. Tài liệu kỹ thuật đầy đủ:
[hanh-vi-mapping.md](hanh-vi-mapping.md).

---

## 1. Mentor giao gì

> "List các hành vi cần phân tích để lấy log cho các hành vi này. Phân rã nó ra
> các hành vi của từng gạch đầu dòng này sẽ có trong các log ID và event log nào
> → từ đó gom những log này và **alert lên webapp**."

Hai việc: **(1)** phân rã hành vi → Event ID, **(2)** gom lại và cảnh báo.

---

## 2. Câu chốt mở đầu

> "Em phân rã 10 hành vi mentor nêu ra Event ID. Trong đó **7 hành vi Windows có
> event sẵn**, và **3 hành vi Windows KHÔNG phát event nào cả** — em phải làm
> thêm đường khác để bắt. Đó là phần em muốn báo cáo kỹ nhất."

Đây là điểm khác biệt so với việc chỉ tra bảng Event ID trên mạng.

---

## 3. Phát hiện chính — 3 hành vi không có Event ID

| Hành vi mentor nêu | Sự thật | Cách lấp |
|---|---|---|
| Service: đổi **ImagePath / binPath** | SCM **không phát event nào**. `7040` chỉ báo đổi start type | `4657` (audit registry) **+** poll `QueryServiceConfig` |
| Service: đổi **tài khoản khởi chạy** | Tương tự, không có event | như trên |
| **Service crash** | `7034` **chưa từng phát** trên máy dev; `7036` cũng vậy | Thêm `7031` (máy dev **có** phát), `7024`, `7000`, `7009` |

**Vì sao điều này đáng nói về mặt an ninh:** đổi `binPath` của một service sẵn có
là kỹ thuật duy trì truy cập kinh điển — **không sinh `7045`**, thừa hưởng luôn
quyền và start type có sẵn. Nếu chỉ nghe `7045`/`7040` thì hành vi này đi qua
hoàn toàn im lặng.

**Cách lấp — cố ý làm hai đường độc lập:**

- **Đường log thật:** bật SACL trên `HKLM\SYSTEM\CurrentControlSet\Services` +
  `auditpol /set /subcategory:"Registry" /success:enable` → sinh `4657`, mang sẵn
  `OldValue` và `NewValue`, tức có luôn "đổi từ gì sang gì".
- **Đường lưới an toàn:** `ServiceConfigWatcher` poll 60 giây một lần bằng
  `EnumServicesStatusEx` + `QueryServiceConfig`, chụp snapshot rồi so lệch. Chạy
  được ngay, không cần cấu hình OS.

Snapshot lưu xuống DB nên restart app không mất mốc so sánh. Lần chạy đầu **chỉ
lập baseline, không cảnh báo** — nếu không sẽ có ~200 cảnh báo giả ngay lần bật
đầu tiên.

---

## 4. Bảng phân rã (bản rút gọn để trình bày)

### Scheduled Task

| Hành vi | Event ID | Field mang bằng chứng |
|---|---|---|
| Tạo mới | `4698` (Security), `106` (Operational) | `TaskName`, `TaskContent` |
| Sửa (đổi exe / tham số) | `4702`, `140` | **`TaskContentNew`** |
| Lệnh đáng ngờ | `4698`/`4702` | `TaskCommand`, `TaskArguments` |
| Chạy từ thư mục bất thường | `4698`/`4702`, `200` | `TaskCommand`, `ActionName` |
| Chạy quyền cao | `4698`/`4702` | `TaskRunLevel`, `TaskRunAsUser` |
| Xoá | `4699`, `141` | `TaskName` |

### Service

| Hành vi | Event ID | Field |
|---|---|---|
| Cài mới | `7045` (System) **+** `4697` (Security) | `ImagePath`, `AccountName`, `StartType` |
| Đổi start type | `7040` | `param2` (cũ) → `param3` (mới) |
| Đổi binPath / tài khoản | **không có** → `4657` hoặc poll | `OldValue`, `NewValue` |
| Chạy từ vị trí không chuẩn | `7045`/`4697` | `ImagePath` |
| Crash / dừng đột ngột | `7031`, `7024`, `7000`, `7009`, `7034` | `param1` |

### Ba chi tiết nên nói ra, chứng minh đã đọc log thật

1. **`4702` dùng `TaskContentNew`, không phải `TaskContent`** như 4698-4701. Đọc
   chung một tên là âm thầm mất dữ liệu đúng ở event "task bị sửa" — event nhạy
   cảm nhất.
2. **`4702` chỉ mang bản MỚI, không mang bản cũ.** Tự nó không trả lời được "đổi
   từ gì sang gì" → phải so với bản ghi gần nhất trong DB. Đây là lý do cần tầng
   tương quan.
3. **Cài một service sinh HAI event ở HAI channel, định dạng giá trị khác nhau**:
   `4697` trả mã số (`'3'`, `'0x10'`), `7045` trả chữ (`'demand start'`). Parser
   phải chuẩn hoá mới gộp được.

---

## 5. App cảnh báo thế nào

```
Event đã parse
   ├─► RuleCatalog        (15 rule thuần hàm trên 1 event)
   ├─► CorrelationRules   (2 rule cần tra DB nhiều event)
   └─► ServiceConfigWatcher (poll WinAPI — không cần event)
                    ↓
              bảng Alerts → /api/alerts → tab "Cảnh báo" + SignalR
```

**Điểm thiết kế nên nêu:**

- Một event có thể sinh **nhiều** cảnh báo (task vừa chạy từ `%TEMP%`, vừa dùng
  PowerShell mã hoá, vừa chạy SYSTEM = 3 cảnh báo) → phải là **bảng riêng**,
  không phải thêm cột vào bảng Events.
- Mỗi cảnh báo có **tên hành vi** + **câu bằng chứng** trích thẳng từ event, không
  phải chỉ một chữ "High" không giải thích được.
- `RiskScorer` cũ nay **chỉ uỷ quyền** cho `RuleCatalog` → dashboard tô màu và tab
  Cảnh báo không thể nói hai chuyện khác nhau.
- Tầng tương quan (`TASK_CREATE_THEN_DELETE`, `TASK_COMMAND_CHANGED`) chính là
  phần "phân tích tương quan hành vi" mentor từng nhắc.

---

## 6. Phần mạnh nhất khi trình bày — tinh chỉnh trên dữ liệu thật

Chạy `--rebuild-alerts` trên **15.059 event thật** đã thu, đếm cảnh báo theo từng
rule, rồi rà tay.

| | Lần chấm đầu | Sau khi tinh chỉnh |
|---|---|---|
| Cảnh báo mức **High** | **4.430** | **14** |
| `TASK_CREATE_THEN_DELETE` | 4.419 | 9 |
| `TASK_LOLBIN` | 6 | 0 |

**Hai dương tính giả tìm ra và cách xử lý:**

**a) 4.415/4.419 cảnh báo "task tạo rồi xoá ngay" là của driver âm thanh Nahimic**
(`NahimicTask32`/`NahimicTask64`) tự tạo rồi tự xoá liên tục.
→ Sửa bằng **ngưỡng lặp**: task đã bị xoá quá 3 lần trước đó thì đó là thói quen
của phần mềm, không phải sự cố. Lọc theo **mẫu lặp**, không theo tên hãng — thêm
phần mềm "hay quên" khác cũng không phải sửa code.

**b) 6/6 cảnh báo `rundll32.exe` là của một task Microsoft**
(`PcaPatchDbTask` gọi `%windir%\system32\rundll32.exe`).
→ Sửa bằng cách **tách LOLBin làm 2 tầng**: nhóm "chỉ cần gọi là đáng ngờ"
(`mshta`, `regsvr32`, `certutil`, `bitsadmin`, `wscript`, `cscript`, `curl`) và
nhóm "cần ngữ cảnh" (`rundll32`, `msiexec`) — nhóm sau chỉ báo khi tham số có dấu
hiệu chạy từ xa (`http://`, UNC `\\`, `.hta`, `scrobj.dll`, `javascript:`).

**c) `7040` đổi sang auto start giữ Medium, không nâng High.** Mẫu `7040` thật duy
nhất trên máy dev là **BITS đi `demand start` → `auto start`** — hành vi bình
thường của Windows, lặp rất thường xuyên.

> **Câu nên nói:** "Em không chốt danh sách từ khoá bằng cảm tính. Em chạy trên
> 15 nghìn event thật rồi đếm theo từng rule, thấy 4.415 cảnh báo đến từ đúng một
> driver âm thanh nên phải sửa lại rule."

**14 cảnh báo High còn lại** (0,09% trên 15.059 event):
- 5 — Opera GX chạy auto-update từ `AppData` (đúng rule; analyst tự loại)
- 6 — Nahimic, phần dư trước khi ngưỡng lặp kích hoạt
- 3 — `WinSentinelSampleCapture`, chính task test của dự án

---

## 7. Số liệu

| | |
|---|---|
| Event ID theo dõi | **20** (trước bước 11: 15) |
| Rule trong danh mục | **17** |
| Event thật đã chấm | **15.059** |
| Cảnh báo sinh ra | **11.547** (High 14 · Medium 2.494 · Low 9.039) |
| Unit test | **299** (trước bước 11: 180) |

Test quan trọng nhất: `RuleCatalogTests.MauThat_KhongSinhCanhBaoHigh` chạy trên
**cả 14 mẫu XML thật** — nới rule đến mức gây dương tính giả thì test đổ.

---

## 8. Kịch bản demo

**Chuẩn bị:** app phải chạy bằng **Administrator** (bắt buộc để đọc channel
`Security`). Kiểm tra ở tab Dashboard → Log Summary, hoặc `GET /api/system/status`
phải trả `isElevated: true`.

1. **Mở tab "Cảnh báo"** — đã có sẵn 11.547 cảnh báo từ lịch sử. Lọc "Chỉ High"
   → 14 dòng, mỗi dòng có tên hành vi và câu bằng chứng.
2. **Mở "Bảng hành vi đang theo dõi"** ở cuối tab — chính là bảng phân rã hành vi
   → Event ID, hiện ngay trong app chứ không chỉ nằm trong file markdown.
3. **Giả lập kẻ tấn công** (dùng công cụ ngoài, vì whitelist của app cố tình chặn
   không cho tự tạo task như vậy):
   ```
   schtasks /create /tn WinSentinelEvilDemo /tr "C:\Users\Public\payload.cmd" /sc once /st 23:00 /f
   ```
   → cảnh báo `TASK_WRITABLE_DIR` mức High.
4. **Đổi binPath của service** — hành vi Windows không phát event:
   ```
   sc.exe create WinSentinelDemoSvc binPath= "C:\Windows\System32\cmd.exe"
   sc.exe config WinSentinelDemoSvc binPath= "C:\Users\Public\evil.exe"
   ```
   → trong 60 giây, `ServiceConfigWatcher` báo `SERVICE_IMAGEPATH_CHANGED`.
5. **Dọn dẹp:** `schtasks /delete /tn WinSentinelEvilDemo /f` và
   `sc.exe delete WinSentinelDemoSvc`.

---

## 9. Nói đúng mức — đừng nói quá

Mentor thuộc team Network Security nên sẽ hỏi đúng chỗ yếu. Chủ động nêu trước:

- **Rule là so khớp chuỗi và đường dẫn, không phải sandbox, không phải AV.**
  `cmd.exe /c <bất cứ gì>` vẫn lọt nếu tham số không khớp từ khoá nào.
- **Whitelist và detection là hai lớp khác nhau.** Whitelist (`InputPolicy`) chặn
  *app này* làm bậy; detection (`RuleCatalog`) phát hiện *bất kỳ ai* làm bậy, kể
  cả bằng `schtasks.exe`/`sc.exe` ngoài app.
- **Nhánh `4657` chưa verify bằng mẫu XML thật** (máy dev chưa bật SACL). Rule đọc
  phòng thủ qua dictionary `Data` nên tên field khác dự đoán thì chỉ đơn giản
  không khớp, không sinh dữ liệu sai.
- **`7031`/`7024`/`7000`/`7009` đã theo dõi nhưng chưa có nhánh parse riêng** — mới
  rơi vào nhánh dự phòng. Rule `SERVICE_CRASH` chỉ cần Event ID nên vẫn chạy.
  Đúng quy tắc dự án: **không đoán cấu trúc XML khi chưa có mẫu thật.**
- **Thiếu Audit Policy thì cả nhóm event tương ứng biến mất mà không báo lỗi** —
  đó là lý do có tab Log Summary để nhìn channel nào thực sự nhận được event.
- **Quyền Administrator là điều kiện cần cho realtime, không chỉ cho việc đọc
  `Security`.** Chạy không có quyền admin thì channel
  `Microsoft-Windows-TaskScheduler/Operational` vẫn *đọc bù* được phần lỡ lúc app
  tắt nhưng **không nhận event mới theo thời gian thực** — trông y hệt như app
  đang chạy bình thường mà thực chất đã điếc. Chạy bằng Administrator thì đúng, đã
  xác nhận: Log Summary báo "Đang nhận", Security 5.487 event và Operational 9.446
  event. Đây là lý do bước kiểm tra `isElevated: true` nằm ngay đầu kịch bản demo.

---

## 10. Việc còn lại

- Thu mẫu XML thật cho `7031`/`7024`/`7000`/`7009`/`4657` rồi viết nhánh parse riêng.
- Bật SACL + audit Registry để chạy thử đường `4657`.
- Signature virus — chờ mentor chốt phạm vi.

---

## 11. Câu hỏi mentor có thể hỏi

**"Sao không dùng luôn Sysmon?"**
Sysmon là agent bên thứ ba phải cài thêm. Đề bài là giám sát qua Windows Event
Log sẵn có. Ngoài ra dự án cần cả đường *ghi* (tự tạo/sửa task và service qua
WinAPI) để chứng minh vòng khép kín, cái đó Sysmon không làm.

**"Vì sao đổi binPath lại không có event?"**
SCM chỉ ghi `7040` cho start type. Cấu hình service nằm trong registry nên phải
audit chính khoá registry (`4657`) mới có log — đó là lý do em làm cả hai đường.

**"Rule này có chặn được tấn công không?"**
Không. Nó **phát hiện**, không chặn. Và nó so khớp đường dẫn/chuỗi nên thu hẹp bề
mặt chứ không đảm bảo bắt hết.

**"Sao `rundll32` lại không báo?"**
Vì đo trên dữ liệu thật thấy 6/6 cảnh báo là dương tính giả từ một task Microsoft.
Em chuyển nó sang nhóm "cần ngữ cảnh" — chỉ báo khi tham số có dấu hiệu tải/chạy
từ xa. Có test khoá lại cả hai chiều.

**"Nếu có nhiều máy thì sao?"**
Đổi `EventLog:Channels` thành `["ForwardedEvents"]`, không phải sửa code. Cursor
`EventRecordID` đã cố ý tính theo **channel** chứ không theo hostname, đúng cho
trường hợp WEF gộp nhiều máy vào một chuỗi số.
