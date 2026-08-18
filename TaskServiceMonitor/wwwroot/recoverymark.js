"use strict";

/**
 * Đánh dấu "dòng này là event ĐỌC BÙ" trên mọi bảng log.
 *
 * Trang Khôi phục trả lời "app đã đọc bù được những gì" khi bạn chủ động vào xem.
 * Còn cái này trả lời câu ngược lại, ngay tại chỗ đang đọc: "dòng tôi đang nhìn có
 * phải thứ vừa được vá lại sau khi mất kết nối không?" — quan trọng vì event đọc bù
 * nằm lẫn giữa event realtime theo đúng thứ tự thời gian, nhìn bảng không tài nào
 * phân biệt được.
 *
 * KHÔNG cần thêm cột nào vào DB. Mỗi channel có sẵn hai mốc từ
 * GET /api/system/recovered — cursor (RecordId lớn nhất đã lưu lúc khởi động) và
 * mốc đích (RecordId mới nhất đang có trong log lúc đó). Event nào rơi vào khoảng
 * nửa mở (cursor, target] chính là phần đọc bù. Chỉ cần `channel` + `recordId` của
 * dòng, cả hai đều đã có sẵn trong payload.
 *
 * ⚠️ Phạm vi: chỉ đúng cho PHIÊN CHẠY HIỆN TẠI, vì hai mốc đó tính lại mỗi lần khởi
 * động. Event đọc bù của lần chạy trước sẽ KHÔNG có badge — đúng như trang Khôi phục
 * đã ghi, không phải thiếu sót.
 *
 * ⚠️ Bọc IIFE, chỉ xuất window.recoveryMarks (quy ước ghi ở đầu manage.js).
 * PHẢI nạp trước app.js.
 */
(function () {

/** Mỗi phần tử: { channel, cursor, target }. Chỉ giữ channel THỰC SỰ có đọc bù. */
let ranges = [];

let summary = null;
let ready = false;

const waiting = [];

function isRecovered(evt) {
  if (!evt || evt.recordId === null || evt.recordId === undefined) return false;

  const range = ranges.find((r) => r.channel === evt.channel);
  if (!range) return false;

  return evt.recordId > range.cursor && evt.recordId <= range.target;
}

function badge() {
  const span = document.createElement("span");
  span.className = "badge badge--info badge--tiny";
  span.textContent = "↺ đọc bù";
  span.title =
    "Event này sinh ra trong lúc app tắt / mất kết nối, được đọc bù khi app khởi động lại. " +
    "Xem đầy đủ ở tab Khôi phục.";
  return span;
}

async function load() {
  try {
    // take=1 vì ở đây chỉ cần khối `channels` (các mốc), không cần danh sách event —
    // trang Khôi phục mới là chỗ nạp đầy đủ.
    const res = await fetch("/api/system/recovered?take=1");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    summary = await res.json();

    ranges = summary.channels
      .filter((c) => c.recovered > 0 &&
        c.resumeFromRecordId !== null && c.catchUpTargetRecordId !== null)
      .map((c) => ({
        channel: c.channel,
        cursor: c.resumeFromRecordId,
        target: c.catchUpTargetRecordId,
      }));
  } catch (err) {
    // Hỏng thì đơn giản là không có badge nào — KHÔNG được để nó làm vỡ bảng log.
    console.error("Khong doc duoc moc khoi phuc de danh dau dong:", err);
  } finally {
    ready = true;
    for (const fn of waiting.splice(0)) {
      try { fn(); } catch (e) { console.error("Loi handler recoveryMarks:", e); }
    }
  }
}

window.recoveryMarks = {
  isRecovered,
  badge,

  /** Payload thô của /api/system/recovered (chỉ phần tóm tắt) — notifications.js dùng lại. */
  summary: () => summary,

  /**
   * Gọi `fn` khi đã có mốc. Các bảng vẽ xong TRƯỚC khi fetch này về, nên phải vẽ lại
   * một lần nữa — nếu không thì mở trang lên sẽ không thấy badge nào cho tới lúc có
   * event mới đẩy render.
   */
  whenReady(fn) {
    if (ready) fn();
    else waiting.push(fn);
  },
};

load();

})();
