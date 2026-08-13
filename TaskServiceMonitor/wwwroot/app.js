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
  cardHour: document.getElementById("card-hour"),
  cardHosts: document.getElementById("card-hosts"),
  cardHigh: document.getElementById("card-high"),
  modal: document.getElementById("modal"),
  modalTitle: document.getElementById("modal-title"),
  modalMeta: document.getElementById("modal-meta"),
  modalXml: document.getElementById("modal-xml"),
  modalClose: document.getElementById("modal-close"),
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

function updateCards() {
  const now = Date.now();
  const oneHourAgo = now - 60 * 60 * 1000;

  const startOfDay = new Date();
  startOfDay.setHours(0, 0, 0, 0);

  let lastHour = 0;
  let highToday = 0;
  const hosts = new Set();

  for (const evt of events) {
    const t = new Date(evt.timeCreated).getTime();
    if (t >= oneHourAgo) lastHour++;
    if (evt.riskLevel === "High" && t >= startOfDay.getTime()) highToday++;
    hosts.add(evt.hostname);
  }

  el.cardHour.textContent = lastHour;
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

async function openDetail(evt) {
  el.modal.hidden = false;
  el.modalTitle.textContent = `${evt.actionDescription} — ${evt.objectName ?? "(không tên)"}`;
  el.modalMeta.replaceChildren();
  el.modalXml.textContent = "Đang tải…";

  try {
    // RawXml khong nam trong payload SignalR (qua nang) -> lay rieng khi can.
    const res = await fetch(`/api/events/${evt.id}`);
    if (!res.ok) throw new Error("HTTP " + res.status);
    const detail = await res.json();

    const meta = [
      ["Event ID", detail.eventId],
      ["Máy", detail.hostname],
      ["Thời gian", formatTime(detail.timeCreated)],
      ["Rủi ro", detail.riskLevel],
      ["Người thực hiện", detail.actorAccount ?? "—"],
      ["Channel", detail.channel],
      ["Binary / lệnh", detail.imagePath ?? detail.taskCommand ?? "—"],
      ["Kiểu action", detail.taskActionType ?? "—"],
    ];

    for (const [label, value] of meta) {
      const div = document.createElement("div");
      div.textContent = `${label}: ${value}`;
      el.modalMeta.appendChild(div);
    }

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

for (const button of document.querySelectorAll(".tab")) {
  button.addEventListener("click", () => {
    const target = button.dataset.tab;

    for (const b of document.querySelectorAll(".tab")) {
      b.classList.toggle("is-active", b === button);
    }
    for (const panel of document.querySelectorAll(".panel")) {
      panel.hidden = panel.id !== "panel-" + target;
    }

    // manage.js dang ky ham nay de nap du lieu lan dau mo tab.
    if (typeof window.onTabShown === "function") {
      window.onTabShown(target);
    }
  });
}

loadInitial().then(connectRealtime);
