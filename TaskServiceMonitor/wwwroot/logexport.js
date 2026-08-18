"use strict";

// "Lưu log" (cả channel) và "Lưu event đang chọn" — dùng chung cho cả 4 panel log.
// Tách riêng vì app.js (3 leaf curated) và logsbrowse.js đều cần, mà hai file đó
// không gọi lẫn nhau được.

/** Xuất cả channel ra .evtx trên máy chạy app. */
async function exportChannelLog(channel, eventId) {
  if (!channel) return;

  try {
    const res = await fetch("/api/logs/export", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ channel, eventId: eventId ? Number(eventId) : null }),
    });

    const payload = await res.json().catch(() => null);
    if (!res.ok) throw new Error(payload?.error ?? `HTTP ${res.status}`);

    const kb = Math.round((payload.sizeBytes ?? 0) / 1024);
    const note = payload.messagesEmbedded
      ? ""
      : " — không nhúng được mô tả, mở trên máy khác sẽ thiếu Description.";

    window.showToast(`Đã lưu "${payload.fileName}" (${kb} KB).${note}`, payload.messagesEmbedded);
    window.refreshSavedLogs?.();
  } catch (err) {
    window.showToast("Không lưu được log: " + err.message, false);
  }
}

/**
 * Xuất các dòng đang chọn. evtx nhờ Windows xuất ra thư mục saved-logs; xml/csv do
 * app tự dựng và trả thẳng về trình duyệt (không để lại file trên server).
 */
async function exportSelectedEvents({ channel, savedFile, tbody, format }) {
  const recordIds = window.selectedRecordIds(tbody);

  if (recordIds.length === 0) {
    window.showToast("Chưa chọn dòng nào — bấm vào một dòng, Ctrl+click để chọn thêm.", false);
    return;
  }

  const body = JSON.stringify({ channel, savedFile, recordIds, format });

  try {
    const res = await fetch("/api/logs/export-events", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body,
    });

    if (!res.ok) {
      const payload = await res.json().catch(() => null);
      throw new Error(payload?.error ?? `HTTP ${res.status}`);
    }

    if (format === "evtx") {
      const payload = await res.json();
      window.showToast(`Đã lưu ${recordIds.length} event vào "${payload.fileName}".`, true);
      window.refreshSavedLogs?.();
      return;
    }

    // xml/csv tra ve file - tu tao link tam de trinh duyet tai xuong.
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = res.headers.get("Content-Disposition")?.match(/filename=([^;]+)/)?.[1]
      ?? `events.${format}`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);

    window.showToast(`Đã tải ${recordIds.length} event dạng ${format.toUpperCase()}.`, true);
  } catch (err) {
    window.showToast("Không lưu được event đã chọn: " + err.message, false);
  }
}

window.exportChannelLog = exportChannelLog;
window.exportSelectedEvents = exportSelectedEvents;
