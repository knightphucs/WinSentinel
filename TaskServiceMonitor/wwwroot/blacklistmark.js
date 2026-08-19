"use strict";

/**
 * Đánh dấu "dòng này khớp một dấu hiệu trong blacklist" trên mọi bảng log, và cho bấm
 * thẳng sang tab Blacklist để xem vì sao.
 *
 * Cùng vai trò với recoverymark.js: tab Blacklist trả lời "đang chặn những gì" khi bạn
 * chủ động vào xem; badge này trả lời câu ngược lại ngay tại chỗ đang đọc log — "dòng
 * tôi đang nhìn có dính dấu hiệu nào không?".
 *
 * ⚠️ ĐÂY LÀ GỢI Ý HIỂN THỊ, KHÔNG PHẢI QUYẾT ĐỊNH BẢO MẬT.
 * Quyết định thật nằm ở server (`BlacklistMatcher` → cảnh báo `BLACKLIST_HIT`). Phần
 * so khớp dưới đây là bản rút gọn chạy trên trình duyệt để khỏi phải hỏi server cho
 * từng dòng; nó có thể lệch nhẹ ở các dạng đường dẫn hiếm (tiền tố `\??\`,
 * `\SystemRoot\`). Lệch thì mất/thừa một cái badge — KHÔNG bao giờ làm mất cảnh báo,
 * vì cảnh báo do server sinh độc lập.
 *
 * ⚠️ Bọc IIFE, chỉ xuất window.blacklistMarks (quy ước ghi ở đầu manage.js).
 * PHẢI nạp trước app.js — app.js gọi trong timeCell.
 */
(function () {

/** Các dòng blacklist ĐANG BẬT. Rỗng khi chưa nạp xong hoặc khi API lỗi. */
let entries = [];
let ready = false;

const waiting = [];

/**
 * Ranh giới đường dẫn / tham số khi dòng lệnh KHÔNG có nháy.
 * Phải giữ khớp với `ExecutablePathParser.ExecutableBoundary` phía server.
 */
const EXE_BOUNDARY = /^(.*?\.(?:exe|com|bat|cmd|scr|sys|dll|ps1|vbs|js|msi|msc))(?:\s|$)/i;

/** Bản rút gọn của ExecutablePathParser.Normalize + ExtractExecutable phía server. */
function extractExe(raw) {
  if (!raw) return "";

  let text = String(raw).trim().replace(/\//g, "\\");

  // Co nhay -> phan trong nhay la duong dan, ke ca khi chua khoang trang.
  if (text.startsWith('"')) {
    const closing = text.indexOf('"', 1);
    text = closing > 1 ? text.slice(1, closing) : text.slice(1);
  } else {
    // 🪤 KHONG duoc cat tai dau cach dau tien: "C:\Program Files (x86)\...\App.exe"
    // se thanh "C:\Program" va moi so khop deu truot. Tim ranh gioi theo DUOI FILE.
    const boundary = EXE_BOUNDARY.exec(text);
    if (boundary) {
      text = boundary[1];
    } else {
      const space = text.indexOf(" ");
      if (space > 0) text = text.slice(0, space);
    }
  }

  // Tien to NT namespace / SystemRoot - hay gap o ImagePath cua driver.
  if (text.toLowerCase().startsWith("\\??\\")) text = text.slice(4);
  if (text.toLowerCase().startsWith("\\systemroot")) text = "C:\\Windows" + text.slice(11);

  return text.trim().replace(/^"|"$/g, "").toLowerCase();
}

function fileNameOf(raw) {
  const exe = extractExe(raw);
  const slash = exe.lastIndexOf("\\");
  return slash >= 0 ? exe.slice(slash + 1) : exe;
}

/** Dòng blacklist đầu tiên khớp event, hoặc null. Đối chiếu BlacklistMatcher.Match. */
function match(evt) {
  if (!evt || entries.length === 0) return null;

  const taskExe = extractExe(evt.taskCommand);
  const serviceExe = extractExe(evt.imagePath);
  const taskFile = fileNameOf(evt.taskCommand);
  const serviceFile = fileNameOf(evt.imagePath);

  const fragmentHaystacks = [evt.taskCommand, evt.taskArguments, evt.imagePath]
    .filter(Boolean)
    .map((v) => String(v).toLowerCase());

  const accounts = [evt.serviceAccount, evt.taskRunAsUser]
    .filter(Boolean)
    .map((v) => String(v).trim().toLowerCase());

  for (const entry of entries) {
    const value = entry.value;

    switch (entry.kind) {
      case "ExecutablePath":
        if (taskExe === value || serviceExe === value) return entry;
        break;
      case "FileName":
        if (taskFile === value || serviceFile === value) return entry;
        break;
      case "CommandFragment":
        if (fragmentHaystacks.some((h) => h.includes(value))) return entry;
        break;
      case "Account":
        if (accounts.includes(value)) return entry;
        break;
    }
  }

  return null;
}

function badge(entry) {
  const span = document.createElement("span");
  // badge--fail (khong phai badge--danger, class do KHONG ton tai) - dung bo mau
  // danger sẵn có, cùng cỡ tiny với badge "↺ đọc bù" ngồi cạnh trong ô Thời gian.
  span.className = "badge badge--fail badge--tiny";
  span.textContent = "⛔ blacklist";
  span.title =
    `Khớp dấu hiệu đã đóng dấu xấu: "${entry.value}".\n` +
    "Bấm để mở tab Blacklist và lọc đúng dòng này.";
  span.style.cursor = "pointer";

  span.addEventListener("click", (e) => {
    // Dong da co click handler rieng (mo khung chi tiet) - khong de no chay theo.
    e.stopPropagation();
    if (window.showBlacklist) window.showBlacklist(entry.value);
  });

  return span;
}

async function load() {
  try {
    const res = await fetch("/api/blacklist");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    const data = await res.json();
    entries = (data.entries ?? []).filter((e) => e.enabled);
  } catch (err) {
    // Hong thi don gian la khong co badge nao - KHONG duoc lam vo bang log.
    console.error("Khong doc duoc blacklist de danh dau dong:", err);
    entries = [];
  } finally {
    ready = true;
    for (const fn of waiting.splice(0)) {
      try { fn(); } catch (e) { console.error("Loi handler blacklistMarks:", e); }
    }
  }
}

window.blacklistMarks = {
  match,
  badge,

  /**
   * Gọi `fn` khi đã nạp xong. Bảng vẽ xong TRƯỚC khi fetch này về, nên phải vẽ lại —
   * nếu không mở trang lên sẽ không thấy badge nào (cùng bẫy với recoveryMarks).
   */
  whenReady(fn) {
    if (ready) fn();
    else waiting.push(fn);
  },

  /** Nạp lại sau khi blacklist đổi (tự học, hoặc người dùng thêm/xoá). */
  refresh: load,
};

load();

})();
