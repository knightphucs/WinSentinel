# Log ID (EventRecordID) — giải thích và kịch bản demo

Trả lời câu hỏi: *RecordID ở đây có phải "Log ID" mentor nói không, hay là gì khác?
Và phải thao tác thế nào để demo phần Log Summary?*

---

## 1. RecordID chính là Log ID

**Có.** `EventRecordID` trong XML của Windows chính là thứ mentor gọi là "Log ID".
Nó khác hoàn toàn với Event ID:

| | Là gì | Phạm vi duy nhất | Ví dụ |
|---|---|---|---|
| **Event ID** | *Loại* sự kiện | Lặp lại vô số lần | `4698` = "task created", xuất hiện 2190 lần trong DB |
| **EventRecordID**<br/>(= Log ID) | *Số thứ tự* của một bản ghi | Duy nhất trong **một channel trên một máy** | `83075` — chỉ đúng một bản ghi mang số này |

Nhìn trong XML thô (tab "Details" của khung chi tiết):

```xml
<System>
  <EventID>4698</EventID>          <!-- LOAI su kien -->
  <EventRecordID>83075</EventRecordID>   <!-- SO THU TU ban ghi = Log ID -->
  <Channel>Security</Channel>
</System>
```

**Vì sao dự án cần nó** — hai việc, đều quan trọng:

1. **Chống ghi trùng**: khoá `IX_Events_Dedup` là `(Hostname, Channel, RecordId)`.
   Chạy lại app không làm nhân đôi dữ liệu vì bộ ba này đã có trong DB.
2. **Resume sau restart**: app nhớ `MAX(RecordId)` của từng channel, lần sau
   subscribe với bộ lọc `EventRecordID > N` để đọc bù đúng phần đã lỡ.

> Lưu ý khi chuyển sang WEF: `RecordId` là số thứ tự của channel **trên máy
> collector**, không thuộc về máy nguồn nào — nên cursor nhóm theo `Channel`, KHÔNG
> theo `Hostname`. Xem [wef-mapping.md](wef-mapping.md) mục 3.4.

---

## 2. Đọc bảng "Log Summary"

6 cột, hai cột giữa hay bị nhầm nhất:

| Cột | Nghĩa | Có đổi theo thời gian không |
|---|---|---|
| Log Name | Tên channel | — |
| Trạng thái | subscribe được / lỗi đọc / đang nhận | có |
| Event đã nhận | Số event nhận **trong phiên chạy này** (nằm trong RAM, khởi động lại là về 0) | có |
| **Cursor lúc khởi động** | `MAX(RecordId)` đọc từ DB lúc app chạy lên, dùng làm mốc `EventRecordID > N` | **KHÔNG** — đóng băng cả phiên |
| **RecordId mới nhất** | RecordId của event vừa nhận gần nhất | có, tăng dần |
| Thời gian cập nhật | Lúc nhận event gần nhất | có |

Badge `↺ khôi phục N` xuất hiện khi app nhận được event **nằm trong phần đọc bù** —
tức `RecordId <= mốc đã có sẵn trong log lúc subscribe`. Đây chính là bằng chứng
nhìn được của cơ chế resume.

### "Đã subscribe (chưa có event)" — khi nào là ĐÚNG

Khi không có event mới nào phát sinh kể từ lần chạy trước. Cursor lọc
`EventRecordID > N`, mà `N` chính là bản ghi mới nhất đã lưu — nên **mọi event cũ bị
loại theo đúng thiết kế**. Không phải lỗi.

Cách phân biệt với lỗi thật: cột **Trạng thái**. Nếu là
`⚠ Subscribe được nhưng lỗi đọc: The handle is invalid.` thì đó là **thiếu quyền
Administrator** (hay gặp với channel `Security`), phải chạy lại app bằng
"Run as administrator".

---

## 3. Kịch bản demo cho mentor (5 phút)

Chạy app bằng **quyền Administrator**.

### Bước 1 — Chỉ ra Log ID trên một event thật

1. Sidebar → **Nhật ký sự kiện → TaskScheduler**.
2. Bấm một dòng bất kỳ → khung chi tiết bên dưới → tab **Details**.
3. Chỉ vào `<EventRecordID>` trong XML, so với `<EventID>` ngay bên trên.
   Nói rõ: *"EventID là loại, EventRecordID là số thứ tự duy nhất — đây là Log ID."*

### Bước 2 — Chỉ ra cursor đang được dùng

4. Về **Dashboard** → xoè ô **Log Summary**.
5. Chỉ cột **Cursor lúc khởi động**: đó là `MAX(RecordId)` app đọc từ PostgreSQL lúc
   khởi động. Cột **RecordId mới nhất** đang lớn hơn — chứng tỏ app vẫn đang nhận.

### Bước 3 — Chứng minh cơ chế resume (phần đắt giá nhất)

6. **Ghi lại** số ở cột "RecordId mới nhất" của channel
   `Microsoft-Windows-TaskScheduler/Operational`.
7. **Tắt app** (Ctrl+C).
8. Trong lúc app đang tắt, sang tab **Scheduled Tasks**… không được — app đã tắt.
   Thay vào đó tạo vài task bằng PowerShell **quyền Administrator**:

   ```powershell
   for ($i = 1; $i -le 3; $i++) {
     schtasks /create /tn "WinSentinelDemo$i" /tr "cmd.exe /c echo hi" /sc once /st 23:59 /f
   }
   ```

9. **Bật lại app**, mở Dashboard → Log Summary.
10. Chỉ ra: channel TaskScheduler hiện badge **`↺ khôi phục N`** và **Event đã nhận > 0**
    — app đã đọc bù đúng những event sinh ra trong lúc nó tắt, nhờ cursor.
11. Mở tab TaskScheduler, xác nhận 3 task vừa tạo có trong danh sách log.

Dọn dẹp:

```powershell
for ($i = 1; $i -le 3; $i++) { schtasks /delete /tn "WinSentinelDemo$i" /f }
```

### Bước 4 — Chứng minh không nhân đôi

12. Tắt và bật lại app một lần nữa **mà không tạo gì thêm**.
13. Log Summary sẽ hiện "Đã subscribe (chưa có event)" — đúng như mục 2 giải thích.
14. Tổng số event trong ô Overview **không tăng**: `IX_Events_Dedup` chặn ghi trùng,
    và cursor cũng không cho đọc lại phần cũ.

---

## 4. Lỗi đã sửa ở bước 9 (nên nói nếu mentor hỏi sâu)

Trước bước 9, bảng này **báo sai**: nhiều lúc hiện "đã subscribe nhưng chưa có event"
dù event đã vào DB.

Nguyên nhân là một lỗi đua (race) trong `EventWatcherService.TrySubscribe`:

```csharp
watcher.Enabled = true;                    // Windows bat dau ban event doc bu NGAY
_statusRegistry.MarkSubscribed(channel, ...);  // ... roi dong nay GHI DE, EventsReceived = 0
```

Khi có cursor thì `readExistingEvents = true`, nên Windows bắn loạt event đọc bù ngay
tại dòng `Enabled = true` — trên thread khác. Số đếm vừa tăng bị dòng sau xoá sạch.

Đã sửa hai lớp:
1. Gọi `MarkSubscribed` **trước** khi bật watcher.
2. `MarkSubscribed` đổi từ ghi đè sang `AddOrUpdate` **giữ nguyên** số đếm.

Có test riêng bắt đúng ca này: `ChannelStatusRegistryTests
.MarkSubscribed_SauKhiDaNhanEvent_KhongDuocXoaSoDem`.

Bằng chứng sau khi sửa (số thật từ máy dev): channel TaskScheduler hiện
`eventsReceived: 625, caughtUpCount: 625, lastRecordId: 83075` — trước đó cả ba đều là 0.
