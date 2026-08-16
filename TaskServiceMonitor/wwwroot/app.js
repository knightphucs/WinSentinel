"use strict";

/** Giu toi da bao nhieu event o client. Vuot thi bo bot cai cu nhat. */
const MAX_EVENTS = 200;

// Toan bo event dang giu o client, moi nhat dung dau.
let events = [];

/**
 * Kenh phat event cho cac tab khac (manage.js dang ky vao day).
 * Nho vay tab Tasks/Services thay ngay log vua sinh ra ma khong phai chuyen
 * sang tab Dashboard.
 */
window.eventBus = {
  handlers: [],
  subscribe(fn) { this.handlers.push(fn); },
  publish(evt) {
    for (const fn of this.handlers) {
      try { fn(evt); } catch (err) { console.error("Loi handler event bus:", err); }
    }
  },
};

const el = {
  body: document.getElementById("events-body"),
  status: document.getElementById("status"),
  count: document.getElementById("count"),
  empty: document.getElementById("empty"),
  filterHost: document.getElementById("filter-host"),
  filterType: document.getElementById("filter-type"),
  filterRisk: document.getElementById("filter-risk"),
  cardHosts: document.getElementById("card-hosts"),
  cardHigh: document.getElementById("card-high"),
  modal: document.getElementById("modal"),
  modalTitle: document.getElementById("modal-title"),
  modalMeta: document.getElementById("modal-meta"),
  modalXml: document.getElementById("modal-xml"),
  modalClose: document.getElementById("modal-close"),
  modalTabs: document.querySelectorAll(".modal__tab"),
};

// ---------------------------------------------------------------- Hien thi

/**
 * TimeCreated tu server luon la UTC (chuoi ket thuc bang 'Z').
 * Phai doi sang gio may xem, khong in thang chuoi ISO - nhieu may nguon
 * co the o mui gio khac nhau.
 */
function formatTime(isoUtc) {
  return new Date(isoUtc).toLocaleString("vi-VN", {
    day: "2-digit", month: "2-digit",
    hour: "2-digit", minute: "2-digit", second: "2-digit",
  });
}

function objectTypeLabel(type) {
  if (type === "ScheduledTask") return "Task";
  if (type === "Service") return "Service";
  return type || "?";
}

function cell(text, className) {
  const td = document.createElement("td");
  if (text === null || text === undefined || text === "") {
    td.textContent = "—";
    td.className = className ? className + " muted" : "muted";
  } else {
    td.textContent = text;
    if (className) td.className = className;
  }
  return td;
}

function riskCell(risk) {
  const td = document.createElement("td");
  const badge = document.createElement("span");
  badge.className = "risk risk--" + risk;
  badge.textContent = risk;
  td.appendChild(badge);
  return td;
}

function buildRow(evt, isNew) {
  const tr = document.createElement("tr");
  tr.className = [isNew ? "is-new" : "", "row--" + evt.riskLevel].join(" ").trim();
  tr.addEventListener("click", () => openDetail(evt));

  tr.appendChild(riskCell(evt.riskLevel));
  tr.appendChild(cell(formatTime(evt.timeCreated), "col-time"));
  tr.appendChild(cell(evt.hostname));
  tr.appendChild(cell(objectTypeLabel(evt.objectType)));
  tr.appendChild(cell(evt.objectName, "col-name"));
  tr.appendChild(cell(evt.actionDescription));
  tr.appendChild(cell(evt.actorAccount));

  return tr;
}

/** Ba filter ket hop voi nhau bang AND. */
function passesFilter(evt) {
  const host = el.filterHost.value;
  const type = el.filterType.value;
  const risk = el.filterRisk.value;
  if (host && evt.hostname !== host) return false;
  if (type && evt.objectType !== type) return false;
  if (risk && evt.riskLevel !== risk) return false;
  return true;
}

function render(newestId) {
  const visible = events.filter(passesFilter);

  el.body.replaceChildren();
  for (const evt of visible) {
    el.body.appendChild(buildRow(evt, evt.id === newestId));
  }

  el.empty.style.display = visible.length === 0 ? "block" : "none";
  el.count.textContent =
    visible.length === events.length
      ? `${events.length} event`
      : `${visible.length} / ${events.length} event`;

  updateCards();
}

/**
 * Detail cua Security tron chung ca Task (4698-4702) lan Service (4697) -
 * TaskActionType/TaskCommand chi co o event Task, ImagePath chi co o event
 * Service. Lay cai nao co, giong cach modal chi tiet (openDetail) dang lam.
 */
function securityDetail(evt) {
  if (evt.taskActionType === "ComHandler") {
    return `ComHandler: ${evt.taskCommand ?? "—"}`;
  }
  return evt.taskCommand ?? evt.imagePath ?? null;
}

/**
 * Leaf channel trong "Nhat ky su kien": id prefix trong HTML -> channel that +
 * bo cot RIENG cua channel do (field ma Dashboard KHONG hien, dung theo huong
 * da chon: giu cay nhung lam no khac Dashboard that su, khong chi loc lai
 * cung 7 cot). Doc lai mang `events` da co san tren client - khong goi API
 * rieng, giong Dashboard.
 */
// Moi cot: `value(evt)` tra ve gia tri THO de loc/liet ke distinct (null = cot
// khong loc duoc, vd Thoi gian lien tuc hoac Chi tiet la text tu do khong hop
// de liet ke); `render(evt)` dung de ve <td> hien thi, co the khac gia tri loc
// (vd Rui ro can badge, khong phai text tho).
// Bo cot chuan cua Event Viewer, dung chung cho ca 3 leaf de doi chieu hai man
// hinh canh nhau.
const eventViewerColumns = [
  { label: "Rủi ro", value: (evt) => evt.riskLevel, render: (evt) => riskCell(evt.riskLevel) },
  { label: "Level", value: (evt) => evt.levelDisplayName, render: (evt) => cell(evt.levelDisplayName) },
  { label: "Thời gian", value: null, render: (evt) => cell(formatTime(evt.timeCreated), "col-time") },
  { label: "Source", value: (evt) => evt.providerName, render: (evt) => cell(evt.providerName) },
  { label: "Event ID", value: (evt) => String(evt.eventId), render: (evt) => cell(evt.eventId) },
  {
    label: "Task Category",
    value: (evt) => evt.taskCategoryName ?? (evt.taskCategoryId != null ? String(evt.taskCategoryId) : null),
    render: (evt) => cell(evt.taskCategoryName ?? evt.taskCategoryId),
  },
];

const logLeaves = {
  "logs-security": {
    channel: "Security",
    columns: [
      ...eventViewerColumns,
      { label: "Máy", value: (evt) => evt.hostname, render: (evt) => cell(evt.hostname) },
      { label: "Loại", value: (evt) => objectTypeLabel(evt.objectType), render: (evt) => cell(objectTypeLabel(evt.objectType)) },
      { label: "Tên", value: (evt) => evt.objectName, render: (evt) => cell(evt.objectName, "col-name") },
      { label: "Hành vi", value: (evt) => evt.actionDescription, render: (evt) => cell(evt.actionDescription) },
      { label: "Chi tiết", value: null, render: (evt) => cell(securityDetail(evt)) },
      { label: "Người thực hiện", value: (evt) => evt.actorAccount, render: (evt) => cell(evt.actorAccount) },
    ],
  },
  "logs-system": {
    channel: "System",
    columns: [
      ...eventViewerColumns,
      { label: "Máy", value: (evt) => evt.hostname, render: (evt) => cell(evt.hostname) },
      { label: "Tên", value: (evt) => evt.objectName, render: (evt) => cell(evt.objectName, "col-name") },
      { label: "Hành vi", value: (evt) => evt.actionDescription, render: (evt) => cell(evt.actionDescription) },
      { label: "Binary", value: (evt) => evt.imagePath, render: (evt) => cell(evt.imagePath) },
      {
        label: "Start type", value: (evt) => evt.startType,
        render: (evt) => cell(evt.previousStartType ? `${evt.previousStartType} → ${evt.startType ?? "?"}` : evt.startType),
      },
      { label: "Người thực hiện", value: (evt) => evt.actorAccount, render: (evt) => cell(evt.actorAccount) },
    ],
  },
  "logs-taskscheduler": {
    channel: "Microsoft-Windows-TaskScheduler/Operational",
    columns: [
      ...eventViewerColumns,
      { label: "Máy", value: (evt) => evt.hostname, render: (evt) => cell(evt.hostname) },
      { label: "Tên", value: (evt) => evt.objectName, render: (evt) => cell(evt.objectName, "col-name") },
      { label: "Hành vi", value: (evt) => evt.actionDescription, render: (evt) => cell(evt.actionDescription) },
      { label: "Lệnh", value: (evt) => evt.taskCommand, render: (evt) => cell(evt.taskCommand) },
      { label: "Instance ID", value: null, render: (evt) => cell(evt.taskInstanceId) },
      { label: "Kết quả", value: (evt) => evt.taskActionResultCode, render: (evt) => cell(evt.taskActionResultCode) },
      { label: "Người thực hiện", value: (evt) => evt.actorAccount, render: (evt) => cell(evt.actorAccount) },
    ],
  },
};

/**
 * Hai che do nguon du lieu cho moi leaf curated:
 *   captured — mang `events` (realtime SignalR, co RiskLevel tu DB, chi 15 Event ID)
 *   channel  — doc live qua /api/logs/browse, thay MOI Event ID nhu Event Viewer
 * Cung mot bo cot cho ca hai (LogBrowseEventDto da co du field enrichment).
 */
const leafModes = {};
const leafLiveRows = {};

for (const [prefix, leaf] of Object.entries(logLeaves)) {
  leafModes[prefix] = "captured";
  leafLiveRows[prefix] = [];

  leaf.rows = () =>
    leafModes[prefix] === "channel"
      ? leafLiveRows[prefix]
      : events.filter((evt) => evt.channel === leaf.channel);

  leaf.onApply = () => renderLogLeaves();
}

// loadDetail: mang `events` la EventSummaryDto, khong kem rawXml (co y, xem
// EventDtos.cs) nen tab "Details" phai goi lai dung endpoint modal cu van dung.
// O che do "channel" payload da co san rawXml nen bo qua.
const leafDetailPanes = Object.fromEntries(
  Object.keys(logLeaves).map((prefix) => [
    prefix,
    window.attachDetailPane(prefix, {
      loadDetail: async (evt) => {
        const res = await fetch(`/api/events/${evt.id}`);
        if (!res.ok) throw new Error("HTTP " + res.status);
        return res.json();
      },
    }),
  ]),
);

function buildLeafRow(evt, columns, isNew, prefix, tbody) {
  const tr = document.createElement("tr");
  tr.className = [isNew ? "is-new" : "", "row--" + evt.riskLevel].join(" ").trim();

  // Can cho "luu event dang chon": XPath loc theo EventRecordID.
  if (evt.recordId != null) tr.dataset.recordId = evt.recordId;

  tr.addEventListener("click", (e) => {
    window.selectRow(tbody, tr, e);
    leafDetailPanes[prefix].show(evt);
  });

  for (const col of columns) {
    tr.appendChild(col.render(evt));
  }

  return tr;
}

/** O tim kiem tu do: quet cac field hay dung nhat, khong phan biet hoa thuong. */
function matchesLeafSearch(evt, needle) {
  if (!needle) return true;

  return [
    evt.objectName, evt.actionDescription, evt.description,
    evt.actorAccount, evt.providerName, evt.hostname, String(evt.eventId),
  ].some((v) => v && String(v).toLowerCase().includes(needle));
}

/** Dong co khop voi moi cot filter dang bat cua leaf nay khong (AND). */
function passesColumnFilters(evt, leaf) {
  if (!leaf.filters) return true;

  for (const [colIndex, allowed] of Object.entries(leaf.filters)) {
    const col = leaf.columns[colIndex];
    const value = col.value(evt) ?? "(trống)";
    if (!allowed.has(value)) return false;
  }

  return true;
}

function renderLogLeaves(newestId) {
  for (const [prefix, leaf] of Object.entries(logLeaves)) {
    const tbody = document.getElementById(`${prefix}-body`);
    if (!tbody) continue;

    const emptyEl = document.getElementById(`${prefix}-empty`);
    const countEl = document.getElementById(`${prefix}-count`);
    const channelEvents = leaf.rows();
    const visible = channelEvents
      .filter((evt) => passesColumnFilters(evt, leaf))
      .filter((evt) => matchesLeafSearch(evt, leaf.search));

    tbody.replaceChildren();
    for (const evt of visible) {
      tbody.appendChild(buildLeafRow(evt, leaf.columns, evt.id === newestId, prefix, tbody));
    }

    if (emptyEl) {
      emptyEl.style.display = visible.length === 0 ? "block" : "none";
    }
    if (countEl) {
      countEl.textContent = visible.length === channelEvents.length
        ? `${channelEvents.length} event`
        : `${visible.length} / ${channelEvents.length} event`;
    }
  }
}

// ---------------------------------------------------------------- Loc theo header cot

let openColumnFilterPopover = null;

function closeColumnFilterPopover() {
  if (openColumnFilterPopover) {
    openColumnFilterPopover.remove();
    openColumnFilterPopover = null;
  }
}

function updateColumnFilterIndicator(prefix, leaf, colIndex) {
  const th = document.getElementById(`${prefix}-table`)?.querySelectorAll("thead th")[colIndex];
  th?.querySelector(".col-filter-toggle")?.classList.toggle("is-active", Boolean(leaf.filters?.[colIndex]));
}

function openColumnFilterPopoverFor(prefix, leaf, colIndex, anchorTh) {
  const reopening = openColumnFilterPopover?.dataset.anchorFor === `${prefix}-${colIndex}`;
  closeColumnFilterPopover();
  if (reopening) return; // bam lai chinh nut do -> dong (toggle)

  const col = leaf.columns[colIndex];
  // Danh sach gia tri lay tu TOAN BO nguon, khong loc theo cac cot khac dang bat -
  // de bo chon roi chon lai khong bi mat lua chon.
  const values = [...new Set(leaf.rows().map((evt) => col.value(evt) ?? "(trống)"))]
    .sort((a, b) => a.localeCompare(b));
  const active = leaf.filters?.[colIndex];

  const popover = document.createElement("div");
  popover.className = "col-filter-popover";
  popover.dataset.anchorFor = `${prefix}-${colIndex}`;
  popover.addEventListener("click", (e) => e.stopPropagation());

  const list = document.createElement("div");
  list.className = "col-filter-list";

  for (const value of values) {
    const label = document.createElement("label");
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.value = value;
    checkbox.checked = !active || active.has(value);
    label.append(checkbox, document.createTextNode(" " + value));
    list.appendChild(label);
  }

  const applyBtn = document.createElement("button");
  applyBtn.type = "button";
  applyBtn.className = "btn btn--primary";
  applyBtn.textContent = "Áp dụng";
  applyBtn.addEventListener("click", () => {
    const checked = [...list.querySelectorAll("input:checked")].map((i) => i.value);
    leaf.filters ??= {};

    if (checked.length === values.length) {
      delete leaf.filters[colIndex]; // chon het = giong nhu khong loc
    } else {
      leaf.filters[colIndex] = new Set(checked);
    }

    updateColumnFilterIndicator(prefix, leaf, colIndex);
    leaf.onApply();
    closeColumnFilterPopover();
  });

  const clearBtn = document.createElement("button");
  clearBtn.type = "button";
  clearBtn.className = "btn";
  clearBtn.textContent = "Xoá lọc";
  clearBtn.addEventListener("click", () => {
    if (leaf.filters) delete leaf.filters[colIndex];
    updateColumnFilterIndicator(prefix, leaf, colIndex);
    leaf.onApply();
    closeColumnFilterPopover();
  });

  const actions = document.createElement("div");
  actions.className = "col-filter-actions";
  actions.append(applyBtn, clearBtn);

  popover.append(list, actions);
  anchorTh.appendChild(popover);
  openColumnFilterPopover = popover;
}

document.addEventListener("click", closeColumnFilterPopover);
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") closeColumnFilterPopover();
});

/**
 * Gan nut loc vao header cua cac cot co `value` (bo qua cot value:null - khong
 * hop de liet ke distinct, vd Thoi gian). Chi goi MOT LAN cho moi bang, y het
 * nguyen tac cua makeColumnsResizable.
 */
function initColumnFilters(prefix, leaf) {
  const table = document.getElementById(`${prefix}-table`);
  if (!table || table.dataset.filterInit) return;
  table.dataset.filterInit = "1";

  const ths = [...table.querySelectorAll("thead th")];

  leaf.columns.forEach((col, index) => {
    if (!col.value) return;

    const th = ths[index];
    if (!th) return;

    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "col-filter-toggle";
    toggle.title = `Lọc theo "${col.label}"`;
    toggle.textContent = "▾";
    toggle.addEventListener("click", (e) => {
      e.stopPropagation();
      openColumnFilterPopoverFor(prefix, leaf, index, th);
    });

    th.appendChild(toggle);
  });
}

// Cho logsbrowse.js dung lai nguyen co che loc theo cot. Leaf cua no khong doc mang
// `events` ma doc payload /api/logs/browse - do la ly do leaf phai co rows()/onApply()
// thay vi hardcode nguon nhu truoc.
window.initColumnFilters = initColumnFilters;
window.passesColumnFilters = passesColumnFilters;

// ---------------------------------------------------------------- Thanh cong cu 3 leaf curated

/** Doc live toan bo channel qua API duyet log (che do "Toan bo channel"). */
async function loadLeafChannel(prefix, leaf) {
  const eventId = document.getElementById(`${prefix}-eventid`)?.value.trim();
  const count = document.getElementById(`${prefix}-count-input`)?.value.trim();

  const params = new URLSearchParams({ channel: leaf.channel });
  if (eventId) params.set("eventId", eventId);
  if (count) params.set("count", count);

  const emptyEl = document.getElementById(`${prefix}-empty`);
  if (emptyEl) {
    emptyEl.textContent = "Đang tải…";
    emptyEl.style.display = "block";
  }

  try {
    const res = await fetch(`/api/logs/browse?${params.toString()}`);
    const payload = await res.json().catch(() => null);
    if (!res.ok) throw new Error(payload?.error ?? `HTTP ${res.status}`);

    leafLiveRows[prefix] = payload;
    if (emptyEl) emptyEl.textContent = "Không có event nào khớp.";
  } catch (err) {
    leafLiveRows[prefix] = [];
    if (emptyEl) emptyEl.textContent = "Lỗi: " + err.message;
  }

  renderLogLeaves();
}

function initLeafToolbars() {
  for (const [prefix, leaf] of Object.entries(logLeaves)) {
    const mode = document.getElementById(`${prefix}-mode`);
    const search = document.getElementById(`${prefix}-search`);
    const reload = document.getElementById(`${prefix}-reload`);

    mode?.addEventListener("change", () => {
      leafModes[prefix] = mode.value;

      // Doi che do = doi tap gia tri cua moi cot -> bo loc cu di, khong thi bang co
      // the rong tron ma khong hieu tai sao.
      leaf.filters = {};
      for (let i = 0; i < leaf.columns.length; i++) updateColumnFilterIndicator(prefix, leaf, i);

      if (mode.value === "channel") {
        loadLeafChannel(prefix, leaf);
      } else {
        renderLogLeaves();
      }
    });

    // customviews.js ap view bang cach ban ca 'input' lan 'change' - nghe 'input' la du.
    search?.addEventListener("input", () => {
      leaf.search = search.value.trim().toLowerCase();
      renderLogLeaves();
    });

    reload?.addEventListener("click", () => {
      if (leafModes[prefix] === "channel") loadLeafChannel(prefix, leaf);
      else renderLogLeaves();
    });

    document.getElementById(`${prefix}-export`)?.addEventListener("click", () =>
      window.exportChannelLog(leaf.channel, document.getElementById(`${prefix}-eventid`)?.value.trim()));

    document.getElementById(`${prefix}-export-selected`)?.addEventListener("click", () =>
      window.exportSelectedEvents({
        channel: leaf.channel,
        tbody: document.getElementById(`${prefix}-body`),
        format: document.getElementById(`${prefix}-format`)?.value ?? "evtx",
      }));
  }
}

/**
 * Hai card nay tinh tu mang `events` (<=200 event tren client) nen la con so XAP XI.
 * Card "Tong event" va "Channel dang nhan" do insight.js dien tu /api/system/overview
 * (query DB that). Ghi chu duoi hang card noi ro cho nao la cai nao.
 */
function updateCards() {
  const startOfDay = new Date();
  startOfDay.setHours(0, 0, 0, 0);

  let highToday = 0;
  const hosts = new Set();

  for (const evt of events) {
    const t = new Date(evt.timeCreated).getTime();
    if (evt.riskLevel === "High" && t >= startOfDay.getTime()) highToday++;
    hosts.add(evt.hostname);
  }

  el.cardHosts.textContent = hosts.size;
  el.cardHigh.textContent = highToday;
}

/** Dropdown may duoc sinh tu chinh du lieu dang co. */
function refreshHostOptions() {
  const hosts = [...new Set(events.map((e) => e.hostname))].sort();
  const current = el.filterHost.value;

  el.filterHost.replaceChildren();
  const all = document.createElement("option");
  all.value = "";
  all.textContent = "Tất cả";
  el.filterHost.appendChild(all);

  for (const h of hosts) {
    const o = document.createElement("option");
    o.value = h;
    o.textContent = h;
    el.filterHost.appendChild(o);
  }

  // Giu nguyen lua chon cu neu may do van con trong danh sach.
  if (hosts.includes(current)) el.filterHost.value = current;
}

function setStatus(text, kind) {
  el.status.textContent = text;
  el.status.className = "status status--" + kind;
}

// ---------------------------------------------------------------- Modal

/** Tab "Tong quan"/"Chi tiet" cua modal: chuyen hidden giua modalMeta/modalXml. */
function showModalTab(name) {
  for (const btn of el.modalTabs) {
    btn.classList.toggle("is-active", btn.dataset.modalTab === name);
  }
  el.modalMeta.hidden = name !== "general";
  el.modalXml.hidden = name !== "details";
}

/** Ve lai "Tong quan" moi lan mo modal, du lan truoc dong o tab nao. */
function resetModalTabs() {
  showModalTab("general");
}
window.resetModalTabs = resetModalTabs;
// Cho cac file khac (manage.js) nhay thang sang tab "Chi tiet" khi can, vd nut
// "XML" cua Scheduled Task - khong the tu gan click listener vi showModalTab
// von la ham noi bo cua file nay.
window.setModalTab = showModalTab;

for (const btn of el.modalTabs) {
  btn.addEventListener("click", () => showModalTab(btn.dataset.modalTab));
}

/**
 * Ve danh sach field kieu EventViewer (nhan trai / gia tri phai, xuong dong
 * duoc) vao #modal-meta. Dung chung cho ca 3 nguon dang mo modal: app.js
 * (openDetail - event tu DB that), logsbrowse.js (openLogDetail - event doc
 * tho), manage.js (chi tiet Task/Service - khong phai event).
 */
function renderModalMeta(rows) {
  el.modalMeta.replaceChildren();
  for (const [label, value] of rows) {
    const row = document.createElement("div");
    row.className = "modal__meta-row";

    const labelEl = document.createElement("span");
    labelEl.className = "modal__meta-label";
    labelEl.textContent = label;

    const valueEl = document.createElement("span");
    valueEl.className = "modal__meta-value";
    valueEl.textContent = value === null || value === undefined || value === "" ? "—" : value;

    row.append(labelEl, valueEl);
    el.modalMeta.appendChild(row);
  }
}
window.renderModalMeta = renderModalMeta;

async function openDetail(evt) {
  el.modal.hidden = false;
  resetModalTabs();
  el.modalTitle.textContent = `${evt.actionDescription} — ${evt.objectName ?? "(không tên)"}`;
  el.modalMeta.replaceChildren();
  el.modalXml.textContent = "Đang tải…";

  try {
    // RawXml khong nam trong payload SignalR (qua nang) -> lay rieng khi can.
    const res = await fetch(`/api/events/${evt.id}`);
    if (!res.ok) throw new Error("HTTP " + res.status);
    const detail = await res.json();

    renderModalMeta([
      ["Event ID", detail.eventId],
      ["Máy", detail.hostname],
      ["Thời gian", formatTime(detail.timeCreated)],
      ["Rủi ro", detail.riskLevel],
      ["Người thực hiện", detail.actorAccount ?? "—"],
      ["Channel", detail.channel],
      ["Binary / lệnh", detail.imagePath ?? detail.taskCommand ?? "—"],
      ["Kiểu action", detail.taskActionType ?? "—"],
    ]);

    el.modalXml.textContent = detail.rawXml;
  } catch (err) {
    console.error("Khong lay duoc chi tiet event:", err);
    el.modalXml.textContent = "Không tải được chi tiết event — xem console.";
  }
}

function closeDetail() {
  el.modal.hidden = true;
}

el.modalClose.addEventListener("click", closeDetail);
el.modal.addEventListener("click", (e) => {
  if (e.target === el.modal) closeDetail();
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") closeDetail();
});

// ---------------------------------------------------------------- Du lieu

function addEvent(evt) {
  events.unshift(evt);
  if (events.length > MAX_EVENTS) {
    events.length = MAX_EVENTS;
  }
  refreshHostOptions();
  render(evt.id);
  renderLogLeaves(evt.id);

  // Bao cho tab Tasks/Services biet de tu cap nhat feed cua no.
  window.eventBus.publish(evt);
}

/** Nap san du lieu da co trong DB de mo trang ra la thay ngay, khong cho event moi. */
async function loadInitial() {
  try {
    const res = await fetch(`/api/events?take=${MAX_EVENTS}`);
    if (!res.ok) throw new Error("HTTP " + res.status);
    events = await res.json();
    refreshHostOptions();
    render();
    renderLogLeaves();
    makeColumnsResizable(document.getElementById("events"), "dashboard");
  } catch (err) {
    console.error("Khong nap duoc du lieu ban dau:", err);
    el.empty.textContent = "Không tải được dữ liệu từ /api/events — xem console.";
  }
}

async function connectRealtime() {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl("/monitorHub")
    .withAutomaticReconnect()
    .build();

  connection.on("ReceiveEvent", addEvent);

  connection.onreconnecting(() => setStatus("Đang kết nối lại…", "connecting"));
  connection.onreconnected(() => {
    setStatus("Đang nhận realtime", "online");
    // Mat ket noi mot luc co the da bo lo event -> nap lai cho khop DB.
    loadInitial();
  });
  connection.onclose(() => setStatus("Mất kết nối", "offline"));

  try {
    await connection.start();
    setStatus("Đang nhận realtime", "online");
  } catch (err) {
    console.error("Khong ket noi duoc SignalR:", err);
    setStatus("Mất kết nối", "offline");
  }
}

el.filterHost.addEventListener("change", () => render());
el.filterType.addEventListener("change", () => render());
el.filterRisk.addEventListener("change", () => render());

// ---------------------------------------------------------------- Tabs

/**
 * Kenh phat "vua chuyen sang tab X" - nhieu file (manage.js, logsbrowse.js,
 * chinh app.js) deu can nghe de nap du lieu/khoi tao lan dau mo tab, nen dung
 * dang subscribe nhieu handler (giong window.eventBus o tren) thay vi gan
 * thang mot ham nhu truoc - gan thang se bi file nao load SAU de lai.
 */
window.onTabShown = {
  handlers: [],
  subscribe(fn) { this.handlers.push(fn); },
  publish(tab) {
    for (const fn of this.handlers) {
      try { fn(tab); } catch (err) { console.error("Loi handler onTabShown:", err); }
    }
  },
};

/**
 * Chuyen sang tab cua `button` (an/hien panel + bao onTabShown). Tach rieng
 * thanh ham va gan vao window vi logsbrowse.js can goi lai cho cac leaf channel
 * ghim dong vao sidebar - nhung nut do sinh ra SAU khi vong lap duoi day da
 * chay xong nen khong tu duoc gan click listener nhu cac nut "tab" tinh.
 */
function activateTab(button) {
  const target = button.dataset.tab;

  for (const b of document.querySelectorAll(".tab")) {
    b.classList.toggle("is-active", b === button);
  }
  for (const panel of document.querySelectorAll(".panel")) {
    panel.hidden = panel.id !== "panel-" + target;
  }

  window.onTabShown.publish(target);
}
window.activateTab = activateTab;

for (const button of document.querySelectorAll(".tab")) {
  button.addEventListener("click", () => activateTab(button));
}

// Cot cua 3 leaf channel cung loc-theo-header va keo-resize duoc nhu Dashboard.
// Phai doi toi luc tab do THUC SU duoc mo (panel het hidden) moi goi -
// offsetWidth doc luc dang hidden se ra 0 (xem ghi chu trong colresize.js).
// Thu tu QUAN TRONG: gan nut loc TRUOC roi moi resize - de resize do duoc be
// rong tu nhien co tinh luon nut loc vua them, khong bi hut mat sau do.
window.onTabShown.subscribe((tab) => {
  if (logLeaves[tab]) {
    initColumnFilters(tab, logLeaves[tab]);
    // Bump key khi doi so cot (tien le "summary-v2" trong summary.js).
    makeColumnsResizable(document.getElementById(`${tab}-table`), tab + "-v2");
  }
});

initLeafToolbars();

// ---------------------------------------------------------------- Cau noi cho insight.js
// Panel "Phan tich" doc dung mang `events` ma 3 card dang dem, nen con so luon khop.
window.getEvents = () => events;
window.formatTime = formatTime;
window.openEventDetail = openDetail;

/** Bam mot may o panel Phan tich -> loc Dashboard theo may do roi quay lai. */
window.focusDashboardHost = (host) => {
  el.filterHost.value = host;
  render();
  window.activateTab(document.querySelector('.tab[data-tab="dashboard"]'));
};

/** Nut "Xem chi tiet" tren card: sang panel Phan tich o dung che do. */
for (const btn of document.querySelectorAll("[data-insight]")) {
  btn.addEventListener("click", () => {
    window.activateTab(document.querySelector('.tab[data-tab="insight"]'));
    window.renderInsight(btn.dataset.insight);
  });
}

loadInitial().then(connectRealtime);
