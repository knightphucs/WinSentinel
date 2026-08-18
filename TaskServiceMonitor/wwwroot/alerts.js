"use strict";

/**
 * Tab "Cảnh báo" (bước 11) — trả lời trực tiếp yêu cầu mentor: "gom những log này
 * và alert lên webapp".
 *
 * Khác tab Dashboard ở chỗ: Dashboard liệt kê EVENT (dòng nhật ký), tab này liệt kê
 * KẾT LUẬN — mỗi dòng nói rõ TÊN HÀNH VI và BẰNG CHỨNG. Một event có thể sinh nhiều
 * cảnh báo.
 *
 * Nạp SAU app.js: dùng window.alertBus, window.formatTime, window.onTabShown.
 *
 * ⚠️ BẮT BUỘC bọc trong IIFE — đừng gỡ ra.
 * Đây là <script> thường, không phải module, nên MỌI khai báo top-level đều vào
 * chung một global scope với tất cả file JS khác. File này nạp SAU app.js, nên khi
 * để trần thì `function render()`, `buildRow()`, `cell()` ở đây GHI ĐÈ lên hàm cùng
 * tên của app.js. Hậu quả đã gặp thật: `loadInitial()` của app.js chạy ở cuối trang
 * gọi phải `render()` của file này → bảng Dashboard trống trơn và card "Máy đang gửi
 * event" đứng ở 0, trong khi mảng `events` vẫn có đủ 200 dòng và KHÔNG có lỗi nào
 * hiện ra ở console (hàm vẫn chạy trót lọt, chỉ là vẽ nhầm bảng).
 *
 * File này không cần xuất gì ra ngoài — nó chỉ đăng ký lắng nghe — nên đóng kín
 * hoàn toàn là đúng nhất.
 */

(function () {

const al = {
  body: document.getElementById("alerts-body"),
  empty: document.getElementById("alerts-empty"),
  count: document.getElementById("alerts-count"),
  badge: document.getElementById("alerts-badge"),
  filterSeverity: document.getElementById("alerts-filter-severity"),
  filterRule: document.getElementById("alerts-filter-rule"),
  filterAck: document.getElementById("alerts-filter-ack"),
  refresh: document.getElementById("alerts-refresh"),
  ackAll: document.getElementById("alerts-ack-all"),
  rulesBody: document.getElementById("alerts-rules-body"),
  rulesPanel: document.getElementById("alerts-rules-panel"),
};

/** Cảnh báo đang hiển thị, mới nhất đứng đầu. */
let alerts = [];

/** Danh mục rule, nạp một lần rồi dùng lại cho dropdown + bảng rule. */
let rules = [];

let loadedOnce = false;

// ---------------------------------------------------------------- Tiện ích

function cell(text, className) {
  const td = document.createElement("td");
  td.textContent = text ?? "";
  if (className) td.className = className;
  return td;
}

function severityCell(severity) {
  const td = document.createElement("td");
  const span = document.createElement("span");
  // Dung lai đúng bộ class rủi ro sẵn có (.risk--High/Medium/Low) - severity của
  // cảnh báo CHÍNH LÀ RiskLevel, không phải bộ từ vựng thứ hai.
  span.className = `risk risk--${severity}`;
  span.textContent = severity;
  td.appendChild(span);
  return td;
}

// ---------------------------------------------------------------- Nạp dữ liệu

function buildQuery() {
  const params = new URLSearchParams();

  const severity = al.filterSeverity.value;
  if (severity) params.set("severity", severity);

  const rule = al.filterRule.value;
  if (rule) params.set("ruleId", rule);

  const ack = al.filterAck.value;
  if (ack !== "") params.set("acknowledged", ack);

  // Lọc thời gian chạy Ở SERVER, không lọc mảng đã tải: `take` cắt ở 300 dòng mới
  // nhất, nên lọc client cho khung "7 ngày" thực chất chỉ lọc trong 300 dòng đó.
  // Server lọc trước rồi mới cắt → đúng 300 dòng mới nhất TRONG khung đã chọn.
  timeRange.applyTo(params);

  params.set("take", "300");
  return params.toString();
}

async function loadAlerts() {
  try {
    const res = await fetch(`/api/alerts?${buildQuery()}`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    alerts = await res.json();
    render();
  } catch (err) {
    console.error("Khong nap duoc canh bao:", err);
    al.empty.hidden = false;
    al.empty.textContent = "Không tải được /api/alerts — xem console.";
  }
}

async function loadRules() {
  if (rules.length > 0) return;

  try {
    const res = await fetch("/api/alerts/rules");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    rules = await res.json();
    renderRules();
    fillRuleFilter();
  } catch (err) {
    console.error("Khong nap duoc danh muc rule:", err);
  }
}

/**
 * Badge trên nút tab = số cảnh báo CHƯA XỬ LÝ. Hỏi riêng /api/alerts/summary chứ
 * không đếm mảng đang hiển thị: mảng đó đã bị bộ lọc cắt bớt nên đếm sẽ sai.
 */
async function refreshBadge() {
  try {
    const res = await fetch("/api/alerts/summary");
    if (!res.ok) return;

    const summary = await res.json();
    const count = summary.unacknowledged ?? 0;

    // Chan tren 999+: sau khi chay --rebuild-alerts tren toan bo lich su thi con so
    // that co the len toi hang chuc nghin, nhet nguyen vao badge tron o sidebar la
    // vo layout. Con so day du van xem duoc o /api/alerts/summary.
    al.badge.textContent =
      count > 999 ? "999+" : count > 0 ? String(count) : "";
    al.badge.title = count > 0 ? `${count} cảnh báo chưa xử lý` : "";
    al.badge.classList.toggle(
      "tab-badge--high",
      (summary.unacknowledgedHigh ?? 0) > 0,
    );
  } catch (err) {
    console.error("Khong nap duoc tong hop canh bao:", err);
  }
}

// ---------------------------------------------------------------- Vẽ bảng

function render() {
  al.body.replaceChildren();

  for (const alert of alerts) {
    al.body.appendChild(buildRow(alert));
  }

  const window_ = timeRange.label();
  al.count.textContent = alerts.length
    ? `${alerts.length} cảnh báo${window_ ? ` · ${window_}` : ""}`
    : "";
  al.empty.hidden = alerts.length > 0;

  if (!alerts.length) {
    al.empty.textContent =
      "Không có cảnh báo nào khớp bộ lọc hiện tại.";
  }
}

function buildRow(alert) {
  const tr = document.createElement("tr");
  tr.className = `row--${alert.severity}`;
  if (alert.acknowledged) tr.classList.add("row--acked");

  // Bam vao dong = mo modal event goc, giong bang event o Dashboard.
  // Canh bao tu ServiceConfigWatcher KHONG co event goc (Windows khong phat event
  // nao cho viec doi binPath) -> danh dau de bo con tro pointer mac dinh cua
  // 'tbody tr', khong thi dong do trong nhu bam duoc ma bam khong ra gi.
  if (alert.sourceEventId) {
    tr.addEventListener("click", () => openSourceEvent(alert));
  } else {
    tr.classList.add("row--noevent");
  }

  tr.appendChild(severityCell(alert.severity));
  tr.appendChild(cell(window.formatTime(alert.eventTime)));
  tr.appendChild(cell(alert.ruleName));
  tr.appendChild(cell(alert.hostname));
  tr.appendChild(cell(alert.objectName));
  tr.appendChild(cell(alert.evidence, "cell--wide"));

  // Cảnh báo từ ServiceConfigWatcher không có event gốc (Windows không phát event
  // nào cho việc đổi binPath) - hiện dấu gạch thay vì ô trống khó hiểu.
  tr.appendChild(cell(alert.eventId ? String(alert.eventId) : "—"));

  tr.appendChild(buildActionCell(alert));
  return tr;
}

function buildActionCell(alert) {
  const td = document.createElement("td");

  // Bo nut "Xem event" cu: ca dong da bam duoc roi, giu lai chi la thua.
  if (!alert.acknowledged) {
    const ack = document.createElement("button");
    ack.type = "button";
    ack.className = "btn btn--small";
    ack.textContent = "Đã xử lý";

    ack.addEventListener("click", (e) => {
      // KHONG de click noi len <tr> - neu khong thi bam "Da xu ly" se vua danh dau
      // vua mo modal event, rat kho hieu.
      e.stopPropagation();
      acknowledge(alert);
    });

    td.appendChild(ack);
  }

  return td;
}

/**
 * Mở modal chi tiết event gốc. Dùng lại window.openEventDetail của app.js.
 *
 * Nó đặt tiêu đề modal từ `actionDescription` + `objectName` TRƯỚC khi fetch, nên
 * phải truyền cả hai — chỉ đưa mỗi `id` thì tiêu đề hiện "undefined" trong lúc chờ.
 */
function openSourceEvent(alert) {
  window.openEventDetail({
    id: alert.sourceEventId,
    actionDescription: alert.eventId ? `Event ${alert.eventId}` : alert.ruleName,
    objectName: alert.objectName,
  });
}

async function acknowledge(alert) {
  try {
    const res = await fetch(`/api/alerts/${alert.id}/acknowledge`, { method: "POST" });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    // Bộ lọc mặc định là "Chưa xử lý" nên nạp lại cho dòng vừa xử lý biến mất.
    await loadAlerts();
    await refreshBadge();
  } catch (err) {
    console.error("Khong danh dau duoc canh bao:", err);
  }
}

async function acknowledgeAll() {
  const severity = al.filterSeverity.value;
  const rule = al.filterRule.value;
  const window_ = timeRange.label();

  const label = rule ? `hành vi "${rule}"` : `mức ${severity || "tất cả"}`;
  if (!confirm(
    `Đánh dấu đã xử lý toàn bộ cảnh báo thuộc ${label}` +
    `${window_ ? `, trong khoảng ${window_}` : " (mọi thời điểm)"}?`,
  )) return;

  const params = new URLSearchParams();
  if (severity) params.set("severity", severity);
  if (rule) params.set("ruleId", rule);

  // PHẢI gửi cả khoảng thời gian: nút nằm ngay cạnh bộ lọc nên người dùng hiểu là
  // "hết những gì đang thấy". Bỏ qua from/to ở đây là âm thầm đánh dấu cả những
  // cảnh báo ngoài màn hình — thao tác không hoàn tác được.
  timeRange.applyTo(params);

  try {
    const res = await fetch(`/api/alerts/acknowledge-all?${params}`, { method: "POST" });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    await loadAlerts();
    await refreshBadge();
  } catch (err) {
    console.error("Khong danh dau hang loat duoc:", err);
  }
}

// ---------------------------------------------------------------- Bảng rule

function fillRuleFilter() {
  for (const rule of rules) {
    const option = document.createElement("option");
    option.value = rule.id;
    option.textContent = rule.name;
    al.filterRule.appendChild(option);
  }
}

function renderRules() {
  al.rulesBody.replaceChildren();

  for (const rule of rules) {
    const tr = document.createElement("tr");

    tr.appendChild(cell(rule.id));
    tr.appendChild(cell(rule.name));
    tr.appendChild(severityCell(rule.typicalSeverity));
    tr.appendChild(cell(rule.objectType));
    tr.appendChild(cell(rule.relatedEventIds.join(", ") || "—"));
    tr.appendChild(cell(rule.description, "cell--wide"));

    al.rulesBody.appendChild(tr);
  }
}

// ---------------------------------------------------------------- Banner realtime

/**
 * Hàng đợi banner cho cảnh báo mới.
 *
 * CỐ Ý không dùng lại showToast() của manage.js: chỗ đó chỉ có MỘT thẻ #toast, gọi
 * cái thứ hai là đè cái thứ nhất và timer 6 giây của lần gọi trước vẫn tắt nhầm cái
 * mới. Một event có thể sinh nhiều cảnh báo cùng lúc nên phải xếp chồng được.
 */
function pushAlertBanner(alert) {
  let stack = document.getElementById("alert-stack");

  if (!stack) {
    stack = document.createElement("div");
    stack.id = "alert-stack";
    stack.className = "alert-stack";
    document.body.appendChild(stack);
  }

  const item = document.createElement("div");
  item.className = `alert-banner alert-banner--${alert.severity}`;

  const title = document.createElement("div");
  title.className = "alert-banner__title";
  title.textContent = `${alert.severity} · ${alert.ruleName}`;

  const body = document.createElement("div");
  body.className = "alert-banner__body";
  body.textContent = alert.evidence;

  item.appendChild(title);
  item.appendChild(body);

  item.addEventListener("click", () => {
    item.remove();
    const tab = document.querySelector('.tab[data-tab="alerts"]');
    if (tab) tab.click();
  });

  stack.appendChild(item);

  // Giới hạn 5 banner: cảnh báo dồn dập không được che hết màn hình.
  while (stack.childElementCount > 5) {
    stack.firstElementChild.remove();
  }

  setTimeout(() => item.remove(), 10000);
}

// ---------------------------------------------------------------- Nối dây

const timeRange = window.createTimeRange("alerts-time", loadAlerts);

al.refresh.addEventListener("click", () => {
  loadAlerts();
  refreshBadge();
});

al.ackAll.addEventListener("click", acknowledgeAll);

for (const control of [al.filterSeverity, al.filterRule, al.filterAck]) {
  control.addEventListener("change", loadAlerts);
}

// Nạp lười: chỉ gọi API lần đầu người dùng mở tab.
window.onTabShown.subscribe((tab) => {
  if (tab !== "alerts") return;

  // Cung ly do voi cac bang khac: chi keo-resize duoc khi panel da hien.
  makeColumnsResizable(document.getElementById("alerts-table"), "alerts");
  makeColumnsResizable(document.getElementById("alerts-rules-table"), "alerts-rules");

  loadRules();

  if (!loadedOnce) {
    loadedOnce = true;
    loadAlerts();
  }
});

window.alertBus.subscribe((alert) => {
  pushAlertBanner(alert);
  refreshBadge();

  // Đang mở tab Cảnh báo thì chèn thẳng vào bảng cho thấy ngay, không phải bấm
  // "Tải lại". Chỉ chèn khi khớp bộ lọc đang chọn.
  const panel = document.getElementById("panel-alerts");
  if (!panel || panel.hidden) return;

  const minimum = al.filterSeverity.value;
  const order = { Low: 0, Medium: 1, High: 2 };

  if (minimum && order[alert.severity] < order[minimum]) return;
  if (al.filterRule.value && alert.ruleId !== al.filterRule.value) return;
  if (al.filterAck.value === "true") return;

  // Cảnh báo realtime KHÔNG đi qua /api/alerts nên server chưa lọc giúp - đang xem
  // một cửa sổ thời gian trong quá khứ mà chèn thẳng vào là sai hẳn khung đang xem.
  if (!timeRange.matches(alert.eventTime)) return;

  alerts.unshift(alert);
  render();
});

// Badge phải đúng ngay từ lúc mở trang, không chờ tới khi bấm vào tab.
refreshBadge();

})();
