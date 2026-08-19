"use strict";

// Tab Blacklist (bước 14): xem / thêm / bật-tắt / xoá các dấu hiệu đã bị đóng dấu xấu.
//
// KHÁC tab Cảnh báo: chỗ đó hiện việc ĐÃ XẢY RA, chỗ này hiện CẤU HÌNH đang có hiệu
// lực. Vì vậy badge ở đây đếm số dòng đang bật, không đếm việc cần làm.

/* ⚠️ BẮT BUỘC bọc IIFE — xem ghi chú dài ở manage.js. <script> thường dùng chung
 * global scope, file nạp sau ghi đè lặng lẽ lên hàm cùng tên của file trước. */
(function () {

const $ = (id) => document.getElementById(id);

const el = {
  body: $("blacklist-body"),
  count: $("blacklist-count"),
  badge: $("blacklist-badge"),
  empty: $("blacklist-empty"),
  refresh: $("blacklist-refresh"),

  addBtn: $("blacklist-add"),
  addKind: $("blacklist-add-kind"),
  addValue: $("blacklist-add-value"),
  addSeverity: $("blacklist-add-severity"),
  addReason: $("blacklist-add-reason"),

  fSearch: $("blacklist-filter-search"),
  fKind: $("blacklist-filter-kind"),
  fSource: $("blacklist-filter-source"),
  fSeverity: $("blacklist-filter-severity"),
  fStatus: $("blacklist-filter-status"),
  fHit: $("blacklist-filter-hit"),
  fReset: $("blacklist-filter-reset"),
};

const KIND_LABELS = {
  ExecutablePath: "Đường dẫn",
  FileName: "Tên file",
  CommandFragment: "Chuỗi lệnh",
  Account: "Tài khoản",
};

const SOURCE_LABELS = {
  AutoLearned: "Tự học",
  Manual: "Nhập tay",
};

let loaded = false;

/** Toàn bộ dòng lấy từ server. Lọc làm ở CLIENT — danh sách này nhỏ (vài chục dòng). */
let allEntries = [];

function cell(text, className) {
  const td = document.createElement("td");
  if (text === null || text === undefined || text === "") {
    td.textContent = "—";
    td.className = "muted";
  } else {
    td.textContent = text;
    if (className) td.className = className;
  }
  return td;
}

/**
 * Cột "Mức" là badge viên thuốc, dùng LẠI đúng bộ class .risk--High/Medium/Low của
 * tab Cảnh báo — severity của blacklist chính là RiskLevel, không phải bộ từ vựng thứ
 * hai. Class .risk (nền bo tròn) phải nằm trên <span> BÊN TRONG <td>, đặt lên chính
 * <td> thì mất phần tạo hình và chỉ còn chữ đổi màu.
 */
function severityCell(severity) {
  const td = document.createElement("td");
  const span = document.createElement("span");
  span.className = `risk risk--${severity}`;
  span.textContent = severity;
  td.appendChild(span);
  return td;
}

function formatDate(value) {
  return value ? new Date(value).toLocaleString("vi-VN") : null;
}

/**
 * "Số lần khớp" bấm được → mở tab Cảnh báo lọc theo BLACKLIST_HIT.
 *
 * CẦN THIẾT vì badge "⛔ blacklist" trên bảng log chỉ hiện được khi event khớp nằm
 * trong 200 dòng mới nhất. Dấu hiệu khớp từ hôm trước thì hitCount vẫn đếm nhưng
 * không có dòng nào trên màn hình để gắn badge — không có nút này thì người xem thấy
 * "4 lần khớp" mà không có cách nào đi tới 4 lần đó.
 */
function hitCountCell(entry) {
  const td = document.createElement("td");

  if (entry.hitCount === 0) {
    td.textContent = "0";
    td.className = "muted";
    return td;
  }

  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "badge badge--fail badge--link";
  btn.textContent = String(entry.hitCount);
  btn.title = "Xem các cảnh báo BLACKLIST_HIT đã sinh ra";

  btn.addEventListener("click", () => {
    if (window.showAlertsForRule) window.showAlertsForRule("BLACKLIST_HIT");
  });

  td.appendChild(btn);
  return td;
}

async function callApi(url, options) {
  const res = await fetch(url, options);
  let body = null;
  try { body = await res.json(); } catch { /* endpoint co the tra rong */ }

  if (!res.ok) {
    throw new Error(body?.error ?? `HTTP ${res.status}`);
  }
  return body;
}

/** Bao ket qua qua #toast dung chung - xem window.showToast o manage.js. */
function toast(message, ok) {
  if (window.showToast) {
    window.showToast(message, ok);
  }
}

function buildRow(entry) {
  const tr = document.createElement("tr");
  if (!entry.enabled) tr.className = "is-readonly";

  tr.appendChild(cell(entry.enabled ? "Đang bật" : "Đã tắt"));
  tr.appendChild(cell(KIND_LABELS[entry.kind] ?? entry.kind));
  tr.appendChild(cell(entry.value));
  tr.appendChild(severityCell(entry.severity));
  tr.appendChild(cell(SOURCE_LABELS[entry.source] ?? entry.source));
  tr.appendChild(hitCountCell(entry));
  tr.appendChild(cell(formatDate(entry.lastHitAt)));
  tr.appendChild(cell(entry.reason));

  const actions = document.createElement("td");
  actions.className = "actions";

  const toggle = document.createElement("button");
  toggle.type = "button";
  toggle.className = "btn";
  toggle.textContent = entry.enabled ? "Tắt" : "Bật";
  toggle.addEventListener("click", async () => {
    try {
      await callApi(
        `/api/blacklist/${entry.id}/toggle?enabled=${!entry.enabled}`, { method: "POST" });
      toast(entry.enabled ? "Đã tắt dấu hiệu." : "Đã bật lại dấu hiệu.", true);
      await reloadEverywhere();
    } catch (err) {
      toast(err.message, false);
    }
  });

  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "btn btn--danger";
  remove.textContent = "Xoá";
  remove.addEventListener("click", async () => {
    // Xoa la mat luon dau vet viec app da tung hoc dau hieu nay - hoi truoc.
    if (!confirm(`Xoá hẳn dấu hiệu "${entry.value}" khỏi blacklist?`)) return;
    try {
      await callApi(`/api/blacklist/${entry.id}`, { method: "DELETE" });
      toast("Đã xoá khỏi blacklist.", true);
      await reloadEverywhere();
    } catch (err) {
      toast(err.message, false);
    }
  });

  actions.append(toggle, remove);
  tr.appendChild(actions);
  return tr;
}

function passes(entry) {
  const term = el.fSearch.value.trim().toLowerCase();
  if (term &&
      !entry.value.toLowerCase().includes(term) &&
      !(entry.reason ?? "").toLowerCase().includes(term)) {
    return false;
  }

  if (el.fKind.value && entry.kind !== el.fKind.value) return false;
  if (el.fSource.value && entry.source !== el.fSource.value) return false;
  if (el.fSeverity.value && entry.severity !== el.fSeverity.value) return false;

  const status = el.fStatus.value;
  if (status === "enabled" && !entry.enabled) return false;
  if (status === "disabled" && entry.enabled) return false;

  // "Da tung khop" la cach ra duong tinh gia: dong khop hang nghin lan gan nhu chac
  // chan la hoc nham mot binary hop le.
  if (el.fHit.checked && entry.hitCount === 0) return false;

  return true;
}

function render() {
  const visible = allEntries.filter(passes);

  el.body.replaceChildren();
  for (const entry of visible) {
    el.body.appendChild(buildRow(entry));
  }

  const total = allEntries.length;
  const enabled = allEntries.filter((e) => e.enabled).length;
  const auto = allEntries.filter((e) => e.source === "AutoLearned").length;

  el.count.textContent = visible.length === total
    ? `${total} dấu hiệu (${enabled} đang bật, ${auto} tự học)`
    : `${visible.length} / ${total} dấu hiệu (${enabled} đang bật, ${auto} tự học)`;

  // Badge tab dem dong DANG BAT tren TOAN BO danh sach, khong theo bo loc: badge o
  // sidebar phai noi ve thuc te he thong, khong phu thuoc vao thu nguoi dung dang loc.
  el.badge.textContent = enabled > 0 ? String(enabled) : "";
  el.badge.title = enabled > 0 ? `${enabled} dấu hiệu đang có hiệu lực` : "";

  el.empty.hidden = visible.length > 0;
  if (visible.length === 0) {
    el.empty.textContent = total === 0
      ? "Chưa có dấu hiệu nào. App sẽ tự thêm khi gặp cảnh báo High trên một đường dẫn cụ thể."
      : "Không có dòng nào khớp bộ lọc.";
  }
}

async function load() {
  try {
    const data = await callApi("/api/blacklist");
    allEntries = data.entries ?? [];
    render();
  } catch (err) {
    el.empty.hidden = false;
    el.empty.textContent = "Không tải được blacklist: " + err.message;
  }
}

/**
 * Nạp lại danh sách VÀ báo cho bảng log vẽ lại badge "⛔ blacklist".
 * Gọi sau mọi thao tác đổi blacklist — nếu không thì badge ở tab Nhật ký vẫn theo dữ
 * liệu cũ cho tới khi F5.
 */
async function reloadEverywhere() {
  await load();
  if (window.blacklistChanged) window.blacklistChanged();
}

async function add() {
  const value = el.addValue.value.trim();

  if (!value) {
    toast("Chưa nhập giá trị.", false);
    return;
  }

  el.addBtn.disabled = true;
  try {
    await callApi("/api/blacklist", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        kind: el.addKind.value,
        value,
        severity: el.addSeverity.value,
        reason: el.addReason.value.trim() || null,
      }),
    });

    el.addValue.value = "";
    el.addReason.value = "";
    toast("Đã thêm vào blacklist.", true);
    await reloadEverywhere();
  } catch (err) {
    toast(err.message, false);
  } finally {
    el.addBtn.disabled = false;
  }
}

el.refresh.addEventListener("click", reloadEverywhere);
el.addBtn.addEventListener("click", add);
el.addValue.addEventListener("keydown", (e) => {
  if (e.key === "Enter") add();
});

// Loc lam o CLIENT tren mang da tai (vai chuc dong) nen chi can ve lai, khong goi API.
for (const control of [
  el.fSearch, el.fKind, el.fSource, el.fSeverity, el.fStatus, el.fHit,
]) {
  control.addEventListener("input", render);
}

el.fReset.addEventListener("click", () => {
  el.fSearch.value = "";
  el.fKind.value = "";
  el.fSource.value = "";
  el.fSeverity.value = "";
  el.fStatus.value = "";
  el.fHit.checked = false;
  render();
});

window.onTabShown.subscribe((tab) => {
  if (tab !== "blacklist") return;

  if (!loaded) {
    loaded = true;
    load();
  }

  // makeColumnsResizable doc getBoundingClientRect().width, ma phan tu dang `hidden`
  // tra ve 0 - phai goi khi panel DA hien, khong goi luc nap file.
  makeColumnsResizable($("blacklist-table"), "blacklist", { resizeLast: false });
});

/**
 * Mở tab Blacklist và lọc sẵn đúng một giá trị.
 *
 * Dùng bởi badge "⛔ blacklist" trên bảng log (blacklistmark.js) — nhờ vậy từ chỗ đang
 * đọc log bấm thẳng sang được lý do dòng đó bị chấm, không phải tự đi tìm trong danh
 * sách.
 */
window.showBlacklist = (value) => {
  const tab = document.querySelector('.tab[data-tab="blacklist"]');
  if (tab) tab.click();

  if (value) {
    el.fSearch.value = value;
    // Xoa cac bo loc khac de dong can tim chac chan hien ra, khong bi bo loc cu giau di.
    el.fKind.value = "";
    el.fSource.value = "";
    el.fSeverity.value = "";
    el.fStatus.value = "";
    el.fHit.checked = false;
  }

  // Tab vua duoc mo lan dau thi load() dang chay do dang - render() se duoc goi lai
  // trong load(), nen goi o day chi de truong hop tab da nap tu truoc.
  render();
};

// Cảnh báo realtime tới -> blacklist có thể vừa đổi (app tự học khi gặp hit High).
// Nạp lại để badge tab và bảng log khớp thực tế ngay, không phải đợi F5.
//
// PHẢI debounce: một event có thể sinh nhiều cảnh báo cùng lúc, và lúc đọc bù sau
// restart thì cảnh báo nổ hàng loạt — subscribe trần sẽ bắn một request /api/blacklist
// cho MỖI cảnh báo. Gộp lại thành một lần gọi sau khi loạt đó lắng xuống.
let reloadTimer = null;

window.alertBus?.subscribe(() => {
  clearTimeout(reloadTimer);
  reloadTimer = setTimeout(reloadEverywhere, 1500);
});

// Nap ngay mot lan de badge co so dung tu luc vao trang, khong doi mo tab.
load();

})();
