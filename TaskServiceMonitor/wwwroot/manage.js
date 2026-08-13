"use strict";

// Tab Scheduled Tasks + Services: liet ke, loc, va thao tac - giong Task Scheduler
// va services.msc. Moi thao tac ghi deu sinh event Windows that; feed cuoi moi tab
// hien ngay event vua sinh ra de khoi phai chuyen sang tab Dashboard.

const FEED_LIMIT = 15;

let systemStatus = { isElevated: false, writablePrefix: "WinSentinel" };
let allTasks = [];
let allServices = [];

const $ = (id) => document.getElementById(id);

const mg = {
  elevation: $("elevation"),
  toast: $("toast"),

  tasksBody: $("tasks-body"),
  tasksCount: $("tasks-count"),
  tasksRefresh: $("tasks-refresh"),
  tasksForm: $("task-form"),
  tasksSearch: $("tasks-search"),
  tasksState: $("tasks-filter-state"),
  tasksAction: $("tasks-filter-action"),
  tasksOnlyWritable: $("tasks-only-writable"),
  tasksFeed: $("tasks-feed"),

  servicesBody: $("services-body"),
  servicesCount: $("services-count"),
  servicesRefresh: $("services-refresh"),
  servicesForm: $("service-form"),
  servicesSearch: $("services-search"),
  servicesState: $("services-filter-state"),
  servicesStartType: $("services-filter-starttype"),
  servicesOnlyWritable: $("services-only-writable"),
  servicesFeed: $("services-feed"),
};

// ---------------------------------------------------------------- Tien ich

function showToast(message, ok) {
  mg.toast.textContent = message;
  mg.toast.className = "toast " + (ok ? "toast--ok" : "toast--fail");
  mg.toast.hidden = false;
  setTimeout(() => { mg.toast.hidden = true; }, 6000);
}

/** Goi API, doc thong diep loi tu server thay vi nem loi HTTP tho. */
async function callApi(url, options) {
  const res = await fetch(url, options);
  let body = null;
  try { body = await res.json(); } catch { /* endpoint co the tra rong */ }

  if (!res.ok) {
    throw new Error(body?.error ?? `HTTP ${res.status}`);
  }
  return body;
}

async function runAction(fn, onDone) {
  try {
    const result = await fn();
    showToast(result?.message ?? "Xong.", true);
    if (onDone) await onDone();
  } catch (err) {
    showToast(err.message, false);
  }
}

function textCell(value) {
  const td = document.createElement("td");
  if (value === null || value === undefined || value === "") {
    td.textContent = "—";
    td.className = "muted";
  } else {
    td.textContent = value;
  }
  return td;
}

function actionButton(label, danger, onClick, blocked) {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "btn" + (danger ? " btn--danger" : "");
  btn.textContent = label;

  if (blocked) {
    btn.disabled = true;
    btn.title = blocked;
  } else {
    btn.addEventListener("click", onClick);
  }
  return btn;
}

/** Vi sao dong nay khong thao tac duoc. null = duoc phep. */
function blockedReason(isWritable) {
  if (!systemStatus.isElevated) {
    return "Cần chạy app bằng quyền Administrator.";
  }
  if (!isWritable) {
    return `Rào an toàn: chỉ thao tác được đối tượng có tên bắt đầu bằng "${systemStatus.writablePrefix}".`;
  }
  return null;
}

function formatDate(value) {
  return value ? new Date(value).toLocaleString("vi-VN") : null;
}

// ---------------------------------------------------------------- Feed trong tab

/** Tab nay quan tam toi loai doi tuong nao. */
const feeds = {
  ScheduledTask: { el: () => mg.tasksFeed, items: [] },
  Service: { el: () => mg.servicesFeed, items: [] },
};

function renderFeed(objectType) {
  const feed = feeds[objectType];
  const list = feed.el();
  list.replaceChildren();

  if (feed.items.length === 0) {
    const li = document.createElement("li");
    li.className = "feed__empty";
    li.textContent = "Chưa có event nào.";
    list.appendChild(li);
    return;
  }

  for (const evt of feed.items) {
    const li = document.createElement("li");
    li.className = "feed__item feed__item--" + evt.riskLevel;

    const time = document.createElement("span");
    time.className = "feed__time";
    time.textContent = new Date(evt.timeCreated).toLocaleTimeString("vi-VN");

    const id = document.createElement("span");
    id.className = "feed__id";
    id.textContent = evt.eventId;

    const text = document.createElement("span");
    text.textContent = `${evt.actionDescription} — ${evt.objectName ?? "(không tên)"}`;

    li.append(time, id, text);
    list.appendChild(li);
  }
}

function onRealtimeEvent(evt) {
  const feed = feeds[evt.objectType];
  if (!feed) return;

  feed.items.unshift(evt);
  if (feed.items.length > FEED_LIMIT) {
    feed.items.length = FEED_LIMIT;
  }
  renderFeed(evt.objectType);
}

// ---------------------------------------------------------------- Tasks

function taskPasses(t) {
  const term = mg.tasksSearch.value.trim().toLowerCase();
  if (term && !t.name.toLowerCase().includes(term) && !t.path.toLowerCase().includes(term)) {
    return false;
  }

  const state = mg.tasksState.value;
  if (state === "disabled" && t.enabled) return false;
  if (state && state !== "disabled" && t.state !== state) return false;

  const action = mg.tasksAction.value;
  if (action && t.actionType !== action) return false;

  if (mg.tasksOnlyWritable.checked && !t.isWritable) return false;

  return true;
}

function renderTasks() {
  const visible = allTasks.filter(taskPasses);
  mg.tasksBody.replaceChildren();

  for (const t of visible) {
    const tr = document.createElement("tr");
    if (!t.isWritable) tr.className = "is-readonly";
    const blocked = blockedReason(t.isWritable);

    tr.appendChild(textCell(t.name));
    tr.appendChild(textCell(t.path));
    tr.appendChild(textCell(t.enabled ? t.state : `${t.state} (tắt)`));
    tr.appendChild(textCell(t.actionType === "ComHandler" ? "ComHandler (COM)" : t.command));
    tr.appendChild(textCell(formatDate(t.lastRunTime)));

    const actions = document.createElement("td");
    actions.className = "actions";

    // Xem XML dung duoc cho MOI task, ke ca task he thong - chi la doc.
    actions.appendChild(actionButton("XML", false, () => showTaskXml(t), null));

    actions.appendChild(actionButton(t.enabled ? "Tắt" : "Bật", false,
      () => runAction(
        () => callApi(`/api/tasks/${encodeURIComponent(t.name)}/${t.enabled ? "disable" : "enable"}`,
          { method: "POST" }),
        loadTasks),
      blocked));

    actions.appendChild(actionButton("Chạy", false,
      () => runAction(
        () => callApi(`/api/tasks/${encodeURIComponent(t.name)}/run`, { method: "POST" }),
        loadTasks),
      blocked));

    actions.appendChild(actionButton("Sửa lệnh", false, () => editTask(t), blocked));

    actions.appendChild(actionButton("Xoá", true,
      () => runAction(
        () => callApi(`/api/tasks?name=${encodeURIComponent(t.name)}`, { method: "DELETE" }),
        loadTasks),
      blocked));

    tr.appendChild(actions);
    mg.tasksBody.appendChild(tr);
  }

  const writable = allTasks.filter((t) => t.isWritable).length;
  mg.tasksCount.textContent =
    `${visible.length} / ${allTasks.length} task (${writable} thao tác được)`;
}

/** Ghi de task da co -> sinh event 4702. */
function editTask(t) {
  const command = prompt(`Lệnh mới cho task "${t.name}":`, t.command ?? "cmd.exe");
  if (command === null) return;

  const args = prompt("Tham số (để trống nếu không có):", "");
  if (args === null) return;

  runAction(
    () => callApi("/api/tasks", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: t.name, command, arguments: args }),
    }),
    loadTasks);
}

/** Tai su dung modal cua tab Dashboard de hien XML dinh nghia task. */
async function showTaskXml(t) {
  const modal = $("modal");
  $("modal-title").textContent = `XML định nghĩa — ${t.path}`;
  $("modal-meta").replaceChildren();
  $("modal-xml").textContent = "Đang tải…";
  modal.hidden = false;

  try {
    const res = await fetch(`/api/tasks/xml?path=${encodeURIComponent(t.path)}`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    $("modal-xml").textContent = await res.text();
  } catch (err) {
    $("modal-xml").textContent = "Không đọc được XML: " + err.message;
  }
}

async function loadTasks() {
  try {
    allTasks = await callApi("/api/tasks");
    renderTasks();
  } catch (err) {
    showToast("Không tải được danh sách task: " + err.message, false);
  }
}

// ---------------------------------------------------------------- Services

function servicePasses(s) {
  const term = mg.servicesSearch.value.trim().toLowerCase();
  if (term && !s.name.toLowerCase().includes(term) && !s.displayName.toLowerCase().includes(term)) {
    return false;
  }

  const state = mg.servicesState.value;
  if (state && s.state !== state) return false;

  const startType = mg.servicesStartType.value;
  if (startType && s.startType !== startType) return false;

  if (mg.servicesOnlyWritable.checked && !s.isWritable) return false;

  return true;
}

function renderServices() {
  const visible = allServices.filter(servicePasses);
  mg.servicesBody.replaceChildren();

  for (const s of visible) {
    const tr = document.createElement("tr");
    if (!s.isWritable) tr.className = "is-readonly";
    const blocked = blockedReason(s.isWritable);

    tr.appendChild(textCell(s.name));
    tr.appendChild(textCell(s.displayName));
    tr.appendChild(textCell(s.state));
    tr.appendChild(textCell(s.startType));
    tr.appendChild(textCell(s.imagePath));

    const actions = document.createElement("td");
    actions.className = "actions";

    actions.appendChild(actionButton("Start", false,
      () => runAction(() => callApi(`/api/services/${encodeURIComponent(s.name)}/start`,
        { method: "POST" }), loadServices), blocked));

    actions.appendChild(actionButton("Stop", false,
      () => runAction(() => callApi(`/api/services/${encodeURIComponent(s.name)}/stop`,
        { method: "POST" }), loadServices), blocked));

    const nextType = s.startType === "auto start" ? "demand start" : "auto start";
    actions.appendChild(actionButton(`→ ${nextType}`, false,
      () => runAction(() => callApi(`/api/services/${encodeURIComponent(s.name)}/starttype`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ startType: nextType }),
      }), loadServices), blocked));

    actions.appendChild(actionButton("Xoá", true,
      () => runAction(() => callApi(`/api/services?name=${encodeURIComponent(s.name)}`,
        { method: "DELETE" }), loadServices), blocked));

    tr.appendChild(actions);
    mg.servicesBody.appendChild(tr);
  }

  const writable = allServices.filter((s) => s.isWritable).length;
  mg.servicesCount.textContent =
    `${visible.length} / ${allServices.length} service (${writable} thao tác được)`;
}

async function loadServices() {
  try {
    allServices = await callApi("/api/services");
    renderServices();
  } catch (err) {
    showToast("Không tải được danh sách service: " + err.message, false);
  }
}

// ---------------------------------------------------------------- Su kien UI

mg.tasksForm.addEventListener("submit", (e) => {
  e.preventDefault();
  const data = Object.fromEntries(new FormData(mg.tasksForm));
  runAction(
    () => callApi("/api/tasks", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(data),
    }),
    loadTasks);
});

mg.servicesForm.addEventListener("submit", (e) => {
  e.preventDefault();
  const data = Object.fromEntries(new FormData(mg.servicesForm));
  runAction(
    () => callApi("/api/services", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(data),
    }),
    loadServices);
});

for (const control of [mg.tasksSearch, mg.tasksState, mg.tasksAction, mg.tasksOnlyWritable]) {
  control.addEventListener("input", renderTasks);
}
for (const control of [mg.servicesSearch, mg.servicesState, mg.servicesStartType, mg.servicesOnlyWritable]) {
  control.addEventListener("input", renderServices);
}

mg.tasksRefresh.addEventListener("click", loadTasks);
mg.servicesRefresh.addEventListener("click", loadServices);

// ---------------------------------------------------------------- Khoi tao

async function loadSystemStatus() {
  try {
    systemStatus = await callApi("/api/system/status");
  } catch {
    // Giu mac dinh khong co quyen - an toan hon.
  }

  if (!systemStatus.isElevated) {
    mg.elevation.hidden = false;
    mg.elevation.textContent = "Không chạy quyền Administrator — thao tác ghi bị khoá";
  }
}

// Tab duoc mo lan dau moi nap du lieu, tranh goi API thua luc vao trang.
const loaded = { tasks: false, services: false };

window.onTabShown = (tab) => {
  if (tab === "tasks" && !loaded.tasks) { loaded.tasks = true; loadTasks(); }
  if (tab === "services" && !loaded.services) { loaded.services = true; loadServices(); }
};

window.eventBus.subscribe(onRealtimeEvent);

loadSystemStatus();
