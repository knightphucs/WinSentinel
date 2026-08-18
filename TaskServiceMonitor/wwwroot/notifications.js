"use strict";

/**
 * Chuông thông báo (trên header) + tab "Thông báo".
 *
 * Khác gì tab "Cảnh báo"? Hai thứ trả lời hai câu hỏi khác nhau:
 *   Cảnh báo  — kho tra cứu: lọc theo rule/mức/máy/thời gian, đánh dấu đã XỬ LÝ.
 *   Thông báo — dòng thời gian "có gì mới từ lúc tôi rời mắt", đánh dấu đã ĐỌC.
 * Một cảnh báo đã đọc vẫn đang chờ xử lý; hai trạng thái đó không thay thế nhau nên
 * không gộp chung được. Thông báo cũng KHÔNG chỉ có cảnh báo — việc app khôi phục
 * event sau restart cũng là thứ cần báo, mà nó chẳng phải hành vi đáng ngờ nào cả.
 *
 * "Đã đọc" lưu bằng MỘT mốc thời gian trong localStorage, không phải danh sách id:
 * danh sách id sẽ phình vô hạn theo số cảnh báo (DB đang có hàng chục nghìn), còn
 * một mốc thì đủ để trả lời đúng câu hỏi "cái này có mới hơn lần cuối tôi xem không".
 *
 * ⚠️ Bọc IIFE, chỉ xuất window.pushNotification (quy ước ghi ở đầu manage.js).
 */
(function () {

const SEEN_KEY = "notifications.lastSeenUtc";
const PANEL_LIMIT = 8;
const MAX_ITEMS = 300;

const nf = {
  bell: document.getElementById("bell"),
  bellBadge: document.getElementById("bell-badge"),
  panel: document.getElementById("bell-panel"),
  panelList: document.getElementById("bell-list"),
  panelEmpty: document.getElementById("bell-empty"),
  markPanel: document.getElementById("bell-mark-read"),
  seeAll: document.getElementById("bell-see-all"),

  body: document.getElementById("notifications-body"),
  empty: document.getElementById("notifications-empty"),
  count: document.getElementById("notifications-count"),
  filterKind: document.getElementById("notifications-filter-kind"),
  filterSeverity: document.getElementById("notifications-filter-severity"),
  onlyUnread: document.getElementById("notifications-only-unread"),
  markAll: document.getElementById("notifications-mark-read"),
  refresh: document.getElementById("notifications-refresh"),
  tabBadge: document.getElementById("notifications-badge"),
};

/** Mới nhất đứng đầu. */
let items = [];

/**
 * Lần đầu vào app thì lấy mốc "bây giờ" chứ KHÔNG lấy 0: nếu không, toàn bộ lịch sử
 * cảnh báo (sau `--rebuild-alerts` là hàng chục nghìn) sẽ hiện là "chưa đọc" ngay
 * lần mở đầu tiên — chuông báo 999+ mà chẳng có gì thực sự mới.
 */
function readLastSeen() {
  try {
    const saved = localStorage.getItem(SEEN_KEY);
    if (saved) return new Date(saved).getTime();

    const now = new Date().toISOString();
    localStorage.setItem(SEEN_KEY, now);
    return new Date(now).getTime();
  } catch {
    return Date.now();
  }
}

let lastSeen = readLastSeen();

function writeLastSeen(iso) {
  lastSeen = new Date(iso).getTime();
  try {
    localStorage.setItem(SEEN_KEY, iso);
  } catch {
    /* localStorage co the bi chan - van chay duoc trong phien nay. */
  }
}

function isUnread(item) {
  return new Date(item.timeIso).getTime() > lastSeen;
}

function unreadCount() {
  return items.filter(isUnread).length;
}

// ---------------------------------------------------------------- Nguồn dữ liệu

/** Cảnh báo -> thông báo. Giữ nguyên `alert` để bấm vào còn mở được event gốc. */
function fromAlert(alert) {
  return {
    id: `alert:${alert.id}`,
    kind: "alert",
    severity: alert.severity,
    title: alert.ruleName,
    body: alert.evidence,
    // Dùng EventTime (lúc hành vi xảy ra) chứ không phải DetectedAt: đọc bù sau
    // restart làm hai mốc lệch hẳn nhau, và người xem quan tâm lúc nó XẢY RA.
    timeIso: alert.eventTime,
    hostname: alert.hostname,
    objectName: alert.objectName,
    alert,
  };
}

function insert(item) {
  // Cùng một cảnh báo có thể tới hai đường (nạp ban đầu + SignalR realtime).
  if (items.some((existing) => existing.id === item.id)) return false;

  items.push(item);
  items.sort((a, b) => new Date(b.timeIso) - new Date(a.timeIso));
  if (items.length > MAX_ITEMS) items.length = MAX_ITEMS;
  return true;
}

async function loadAlerts() {
  try {
    // Mức Medium trở lên: Low là "ghi nhận hành vi" (tạo task, cài service) - đẩy
    // hết lên chuông thì chuông kêu suốt và mất hẳn ý nghĩa. Cùng ngưỡng mặc định
    // với tab Cảnh báo.
    const res = await fetch("/api/alerts?severity=Medium&acknowledged=false&take=100");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    for (const alert of await res.json()) insert(fromAlert(alert));
  } catch (err) {
    console.error("Khong nap duoc canh bao cho thong bao:", err);
  }
}

/**
 * Thông báo hệ thống cho việc đọc bù sau restart.
 *
 * Dùng lại mốc mà recoverymark.js đã nạp — KHÔNG gọi /api/system/recovered lần nữa:
 * ba chỗ (badge trên dòng log, thông báo này, trang Khôi phục) cùng hỏi một câu, để
 * mỗi chỗ tự fetch thì vừa thừa request vừa có nguy cơ ba chỗ nói ba con số khác nhau
 * nếu đang đọc bù dở.
 */
function buildRecoveryNotice() {
  const payload = window.recoveryMarks.summary();
  if (!payload) return;

  const recovered = payload.channels.filter((c) => c.recovered > 0);
  if (recovered.length === 0) return;

  const total = payload.totalRecovered ?? recovered.reduce((sum, c) => sum + c.recovered, 0);

  insert({
    // Gắn mốc phiên chạy vào id: khởi động lại lần nữa là một thông báo KHÁC,
    // không bị chèn trùng với lần trước.
    id: `recovery:${payload.sessionStartedUtc}`,
    kind: "system",
    severity: "Medium",
    title: `Đã khôi phục ${total} event bỏ lỡ lúc app tắt`,
    body: recovered.map((c) => `${c.channel}: ${c.recovered}`).join(" · "),
    timeIso: payload.sessionStartedUtc,
    action: { label: "Xem đã khôi phục gì", tab: "recovery" },
  });
}

// ---------------------------------------------------------------- Vẽ

function severityBadge(severity) {
  const span = document.createElement("span");
  span.className = `risk risk--${severity}`;
  span.textContent = severity;
  return span;
}

function openItem(item) {
  if (item.action?.tab) {
    window.activateTab(document.querySelector(`.tab[data-tab="${item.action.tab}"]`));
    return;
  }

  if (item.alert?.sourceEventId) {
    window.openEventDetail({
      id: item.alert.sourceEventId,
      actionDescription: item.alert.eventId ? `Event ${item.alert.eventId}` : item.title,
      objectName: item.objectName,
    });
    return;
  }

  // Cảnh báo từ ServiceConfigWatcher không có event gốc (Windows không phát event
  // nào cho việc đổi binPath) - đưa sang tab Cảnh báo để xem trong ngữ cảnh đầy đủ.
  window.activateTab(document.querySelector('.tab[data-tab="alerts"]'));
}

/** Một dòng trong dropdown của chuông — gọn, không phải bảng. */
function buildPanelItem(item) {
  const li = document.createElement("li");
  li.className = "bell-item" + (isUnread(item) ? " bell-item--unread" : "");

  const head = document.createElement("div");
  head.className = "bell-item__head";
  head.append(severityBadge(item.severity));

  const title = document.createElement("span");
  title.className = "bell-item__title";
  title.textContent = item.title;
  head.appendChild(title);

  const time = document.createElement("time");
  time.className = "bell-item__time";
  time.textContent = window.formatTime(item.timeIso);

  const body = document.createElement("p");
  body.className = "bell-item__body";
  body.textContent = item.body ?? "";

  li.append(head, body, time);
  li.addEventListener("click", () => {
    closePanel();
    openItem(item);
  });

  return li;
}

function renderPanel() {
  const latest = items.slice(0, PANEL_LIMIT);

  nf.panelList.replaceChildren();
  for (const item of latest) nf.panelList.appendChild(buildPanelItem(item));

  nf.panelEmpty.hidden = latest.length > 0;
}

function passesPageFilters(item) {
  if (nf.filterKind.value && item.kind !== nf.filterKind.value) return false;

  const minimum = nf.filterSeverity.value;
  const order = { Low: 0, Medium: 1, High: 2 };
  if (minimum && order[item.severity] < order[minimum]) return false;

  if (nf.onlyUnread.checked && !isUnread(item)) return false;
  return timeRange.matches(item.timeIso);
}

function buildPageRow(item) {
  const tr = document.createElement("tr");
  tr.className = `row--${item.severity}` + (isUnread(item) ? " row--unread" : "");
  tr.addEventListener("click", () => openItem(item));

  const severity = document.createElement("td");
  severity.appendChild(severityBadge(item.severity));

  const state = document.createElement("td");
  const dot = document.createElement("span");
  dot.className = isUnread(item) ? "badge badge--info" : "badge";
  dot.textContent = isUnread(item) ? "Mới" : "Đã đọc";
  state.appendChild(dot);

  const time = document.createElement("td");
  time.className = "col-time";
  time.textContent = window.formatTime(item.timeIso);

  const kind = document.createElement("td");
  kind.textContent = item.kind === "system" ? "Hệ thống" : "Cảnh báo";

  const title = document.createElement("td");
  title.textContent = item.title;

  const host = document.createElement("td");
  host.textContent = item.hostname ?? "—";

  const body = document.createElement("td");
  body.className = "cell--wide";
  body.textContent = item.body ?? "";

  tr.append(state, severity, time, kind, title, host, body);
  return tr;
}

function renderPage() {
  if (!nf.body) return;

  const visible = items.filter(passesPageFilters);

  nf.body.replaceChildren();
  for (const item of visible) nf.body.appendChild(buildPageRow(item));

  nf.empty.hidden = visible.length > 0;
  nf.count.textContent = visible.length === items.length
    ? `${items.length} thông báo`
    : `${visible.length} / ${items.length} thông báo`;
}

function renderBadges() {
  const count = unreadCount();
  const text = count > 99 ? "99+" : count > 0 ? String(count) : "";

  nf.bellBadge.textContent = text;
  nf.bellBadge.hidden = count === 0;
  nf.bell.setAttribute(
    "aria-label",
    count > 0 ? `Thông báo — ${count} chưa đọc` : "Thông báo",
  );

  if (nf.tabBadge) {
    nf.tabBadge.textContent = text;
    nf.tabBadge.classList.toggle(
      "tab-badge--high",
      items.some((item) => isUnread(item) && item.severity === "High"),
    );
  }
}

function renderAll() {
  renderBadges();
  renderPanel();
  renderPage();
}

// ---------------------------------------------------------------- Chuông

function closePanel() {
  nf.panel.hidden = true;
  nf.bell.setAttribute("aria-expanded", "false");
}

function togglePanel() {
  const open = nf.panel.hidden;
  nf.panel.hidden = !open;
  nf.bell.setAttribute("aria-expanded", String(open));
  if (open) renderPanel();
}

function markAllRead() {
  // Lấy mốc từ thông báo MỚI NHẤT chứ không phải Date.now(): nếu đồng hồ máy nguồn
  // chạy nhanh hơn máy này, event tương lai gần sẽ vẫn là "chưa đọc" sau khi bấm
  // "đánh dấu đã đọc" — bấm xong mà badge không về 0 thì trông như nút hỏng.
  const newest = items[0]?.timeIso;
  const now = new Date().toISOString();

  writeLastSeen(
    newest && new Date(newest).getTime() > Date.now() ? newest : now,
  );

  renderAll();
}

nf.bell.addEventListener("click", (e) => {
  e.stopPropagation();
  togglePanel();
});

nf.panel.addEventListener("click", (e) => e.stopPropagation());

document.addEventListener("click", () => {
  if (!nf.panel.hidden) closePanel();
});

document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && !nf.panel.hidden) closePanel();
});

nf.markPanel.addEventListener("click", markAllRead);

nf.seeAll.addEventListener("click", () => {
  closePanel();
  window.activateTab(document.querySelector('.tab[data-tab="notifications"]'));
});

// ---------------------------------------------------------------- Trang Thông báo

const timeRange = window.createTimeRange("notifications-time", renderPage);

nf.markAll?.addEventListener("click", markAllRead);
nf.refresh?.addEventListener("click", async () => {
  await loadAlerts();
  renderAll();
});

for (const control of [nf.filterKind, nf.filterSeverity, nf.onlyUnread]) {
  control?.addEventListener("change", renderPage);
}

// Keo-resize cot: PHAI doi toi luc panel thuc su hien (het hidden), doc offsetWidth
// luc dang hidden se ra 0 - xem ghi chu trong colresize.js.
window.onTabShown.subscribe((tab) => {
  if (tab !== "notifications") return;
  makeColumnsResizable(document.getElementById("notifications-table"), "notifications");
});

// ---------------------------------------------------------------- Nối dây

/** Cho file khác đẩy thông báo hệ thống vào (recovery.js dùng khi tải lại thủ công). */
window.pushNotification = (item) => {
  if (insert(item)) renderAll();
};

window.alertBus.subscribe((alert) => {
  if (alert.severity === "Low") return; // cùng ngưỡng với lúc nạp ban đầu
  if (insert(fromAlert(alert))) renderAll();
});

(async () => {
  await loadAlerts();
  renderAll();
})();

// Mốc khôi phục do recoverymark.js nạp, về theo nhịp riêng — chờ nó rồi mới dựng
// thông báo hệ thống, thay vì fetch lại lần nữa.
window.recoveryMarks.whenReady(() => {
  buildRecoveryNotice();
  renderAll();
});

})();
