"use strict";

// Tab Scheduled Tasks + Services: liet ke, loc, va thao tac - giong Task Scheduler
// va services.msc. Moi thao tac ghi deu sinh event Windows that; feed cuoi moi tab
// hien ngay event vua sinh ra de khoi phai chuyen sang tab Dashboard.

/* ⚠️ BẮT BUỘC bọc trong IIFE — đừng gỡ ra.
 *
 * Đây là <script> thường, không phải module, nên MỌI khai báo top-level đều rơi vào
 * CHUNG một global scope với tất cả file JS khác. File nạp sau ghi đè lặng lẽ lên
 * hàm cùng tên của file nạp trước — không lỗi, không cảnh báo, chỉ là chạy nhầm hàm.
 * Đã gặp thật một lần với alerts.js (`render`/`buildRow`/`cell` đè lên app.js làm
 * bảng Dashboard trống trơn), và file này với logsbrowse.js đang cùng khai báo
 * `textCell` — hai bản cài đặt tình cờ giống nhau nên chưa gây hại, nhưng sửa một
 * bên là bên kia âm thầm đổi theo.
 *
 * Quy ước từ nay: file JS nào cũng đóng kín, thứ cần chia sẻ thì gán tường minh vào
 * `window.` — thấy được ngay ai xuất cái gì. Xem `window.showToast` ở cuối file.
 */
(function () {

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
  tasksSearch: $("tasks-search"),
  tasksState: $("tasks-filter-state"),
  tasksAction: $("tasks-filter-action"),
  tasksOnlyWritable: $("tasks-only-writable"),
  tasksSort: $("tasks-sort"),
  tasksFeed: $("tasks-feed"),

  servicesBody: $("services-body"),
  servicesCount: $("services-count"),
  servicesRefresh: $("services-refresh"),
  servicesSearch: $("services-search"),
  servicesState: $("services-filter-state"),
  servicesStartType: $("services-filter-starttype"),
  servicesOnlyWritable: $("services-only-writable"),
  servicesSort: $("services-sort"),
  servicesFeed: $("services-feed"),
};

// ObjectName -> thoi diem event gan nhat (tu /api/events/latest-by-object).
// Windows khong co field "ngay tao" cho task/service nen dung lich su event
// app da bat duoc de sap "moi nhat". Cap nhat them realtime qua onRealtimeEvent
// de khong phai goi lai API moi khi co event moi.
let taskLatest = {};
let serviceLatest = {};

async function loadLatestByObject(objectType) {
  try {
    const rows = await callApi(`/api/events/latest-by-object?type=${objectType}`);
    const map = {};
    for (const r of rows) map[r.objectName] = r.latest;
    return map;
  } catch {
    return {};
  }
}

/** Sap theo thoi diem trong `latest` (moi nhat truoc), item khong co event roi xuong cuoi theo ten. */
function sortByRecent(items, latest, nameKey) {
  return [...items].sort((a, b) => {
    const ta = latest[a[nameKey]];
    const tb = latest[b[nameKey]];
    if (ta && tb) return new Date(tb) - new Date(ta);
    if (ta) return -1;
    if (tb) return 1;
    return a[nameKey].localeCompare(b[nameKey]);
  });
}

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
  // Cap nhat "moi nhat" ngay khi co event moi, khoi phai goi lai API.
  if (evt.objectType === "ScheduledTask" && evt.objectName) {
    taskLatest[evt.objectName] = evt.timeCreated;
    if (loaded.tasks && mg.tasksSort.value === "recent") renderTasks();
  } else if (evt.objectType === "Service" && evt.objectName) {
    serviceLatest[evt.objectName] = evt.timeCreated;
    if (loaded.services && mg.servicesSort.value === "recent") renderServices();
  }

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
  let visible = allTasks.filter(taskPasses);
  if (mg.tasksSort.value === "recent") {
    // Events.ObjectName cho task la FULL PATH co dau "\" dau (vd "\WinSentinelDemo"),
    // giong task.Path cua COM - KHONG giong task.Name (chi la ten la, khong co
    // "\"). Join sai key nay se khien moi task khong khop duoc voi lich su event
    // cua no, roi tut ve sap theo ten (day chinh la bug da gap).
    visible = sortByRecent(visible, taskLatest, "path");
  }
  mg.tasksBody.replaceChildren();

  for (const t of visible) {
    const tr = document.createElement("tr");
    if (!t.isWritable) tr.className = "is-readonly";
    const blocked = blockedReason(t.isWritable);

    // Bam vao dong (tru vung nut "Thao tac") -> hien thong tin, giong bam vao
    // mot dong event o tab Dashboard.
    tr.addEventListener("click", (e) => {
      if (e.target.closest(".actions")) return;
      openTaskDetail(t);
    });

    tr.appendChild(textCell(t.name));
    tr.appendChild(textCell(t.path));
    tr.appendChild(textCell(t.enabled ? t.state : `${t.state} (tắt)`));
    tr.appendChild(textCell(t.actionType === "ComHandler" ? "ComHandler (COM)" : t.command));
    tr.appendChild(textCell(formatDate(t.lastRunTime)));

    const actions = document.createElement("td");
    actions.className = "actions";

    // Xem XML dung duoc cho MOI task, ke ca task he thong - chi la doc.
    // Nhay thang vao tab "Chi tiet" cua modal chung (openTaskDetail).
    actions.appendChild(actionButton("XML", false, () => openTaskDetail(t, "details"), null));

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

// ---------------------------------------------------------------- Hop thoai Create Task

let taskDialog = null;

function ensureTaskDialog() {
  if (taskDialog) return taskDialog;

  taskDialog = window.buildTaskDialog();
  taskDialog.close.addEventListener("click", closeTaskDialog);
  taskDialog.submit.addEventListener("click", submitTaskDialog);

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && !taskDialog.root.hidden) closeTaskDialog();
  });

  return taskDialog;
}

function closeTaskDialog() {
  if (taskDialog) taskDialog.root.hidden = true;
}

function $d(id) {
  return document.getElementById(id);
}

/**
 * Mo hop thoai. `detail` la ban day du tu /api/tasks/detail - truyen vao khi SUA de
 * nap lai dung dinh nghia dang co.
 *
 * QUAN TRONG: POST la GHI DE TOAN BO, nen khong nap lai thi bam "Cap nhat" se xoa sach
 * arguments va doi trigger - dung loi da co truoc buoc 10.
 */
function openTaskDialog(detail) {
  const d = ensureTaskDialog();
  d.root.hidden = false;
  d.showTab("general");
  d.status.textContent = "";

  const editing = Boolean(detail);
  d.title.textContent = editing ? `Sửa task — ${detail.name}` : "Create Task";

  $d("td-name").value = detail?.name ?? "";
  $d("td-name").readOnly = editing;
  $d("td-author").value = detail?.author ?? "";
  $d("td-description").value = detail?.description ?? "";
  $d("td-hidden").checked = Boolean(detail?.hidden);
  $d("td-userid").value = detail?.userId ?? "";
  $d("td-groupid").value = detail?.groupId ?? "";
  $d("td-logontype").value = detail?.logonType ?? "InteractiveToken";
  $d("td-runlevel").value = detail?.runLevel ?? "LeastPrivilege";

  d.triggerList.replaceChildren();
  for (const t of detail?.triggers ?? []) {
    d.triggerList.appendChild(window.taskDialogRows.triggerRow(t));
  }
  if (d.triggerList.children.length === 0) {
    d.triggerList.appendChild(window.taskDialogRows.triggerRow());
  }

  d.actionList.replaceChildren();
  for (const a of detail?.actions ?? []) {
    d.actionList.appendChild(window.taskDialogRows.actionRow(a));
  }
  if (d.actionList.children.length === 0) {
    d.actionList.appendChild(window.taskDialogRows.actionRow(
      { command: "C:\\Windows\\System32\\cmd.exe", arguments: "/c echo hello" }));
  }
}

function collectTaskDialog() {
  const d = ensureTaskDialog();

  const triggers = [...d.triggerList.children].map((row) => ({
    type: row.querySelector('[data-role="trigger-type"]').value,
    startBoundary: row.querySelector('[data-role="trigger-start"]').value || null,
    enabled: row.querySelector('[data-role="trigger-enabled"]').checked,
  }));

  const actions = [...d.actionList.children]
    .map((row) => ({
      type: "Exec",
      command: row.querySelector('[data-role="action-command"]').value.trim(),
      arguments: row.querySelector('[data-role="action-arguments"]').value.trim() || null,
      workingDirectory: row.querySelector('[data-role="action-workdir"]').value.trim() || null,
    }))
    .filter((a) => a.command);

  return {
    name: $d("td-name").value.trim(),
    author: $d("td-author").value.trim() || null,
    description: $d("td-description").value.trim() || null,
    hidden: $d("td-hidden").checked,
    userId: $d("td-userid").value.trim() || null,
    groupId: $d("td-groupid").value.trim() || null,
    logonType: $d("td-logontype").value,
    runLevel: $d("td-runlevel").value,
    allowStartOnDemand: $d("td-allowdemand").checked,
    stopIfGoingOnBatteries: $d("td-battery").checked,
    multipleInstancesPolicy: $d("td-instances").value,
    executionTimeLimit: $d("td-timelimit").value.trim() || null,
    triggers,
    actions,
  };
}

async function submitTaskDialog() {
  const d = ensureTaskDialog();
  const body = collectTaskDialog();

  if (!body.name) {
    d.status.textContent = "Chưa nhập tên task.";
    return;
  }
  if (body.actions.length === 0) {
    d.status.textContent = "Cần ít nhất một action có chương trình.";
    return;
  }

  d.submit.disabled = true;
  d.status.textContent = "Đang gửi…";

  try {
    const result = await callApi(`/api/tasks/full?api=${d.apiMode.value}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });

    showToast(result?.message ?? "Đã ghi task.", true);
    d.status.textContent = "";
    closeTaskDialog();
    await loadTasks();
  } catch (err) {
    // Giu hop thoai mo de sua lai, chi bao loi tai cho.
    d.status.textContent = err.message;
    showToast(err.message, false);
  } finally {
    d.submit.disabled = false;
  }
}

/** Nut "Sửa": nap dinh nghia hien co roi mo hop thoai. */
async function editTask(t) {
  try {
    const res = await fetch(`/api/tasks/detail?path=${encodeURIComponent(t.path)}`);
    openTaskDialog(res.ok ? await res.json() : { name: t.name });
  } catch {
    openTaskDialog({ name: t.name });
  }
}

document.getElementById("task-dialog-open")
  ?.addEventListener("click", () => openTaskDialog(null));

// ---------------------------------------------------------------- Hop thoai Service

let serviceDialog = null;

function ensureServiceDialog() {
  if (serviceDialog) return serviceDialog;

  serviceDialog = window.buildServiceDialog();
  serviceDialog.close.addEventListener("click", () => { serviceDialog.root.hidden = true; });
  serviceDialog.submit.addEventListener("click", submitServiceDialog);

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && !serviceDialog.root.hidden) serviceDialog.root.hidden = true;
  });

  return serviceDialog;
}

async function submitServiceDialog() {
  const d = ensureServiceDialog();

  const body = {
    name: $d("sd-name").value.trim(),
    displayName: $d("sd-displayname").value.trim() || null,
    binaryPath: $d("sd-binarypath").value.trim(),
    description: $d("sd-description").value.trim() || null,
    startType: $d("sd-starttype").value,
    dependencies: $d("sd-dependencies").value
      .split(",").map((s) => s.trim()).filter(Boolean),
    account: $d("sd-account").value,
  };

  if (!body.name || !body.binaryPath) {
    d.status.textContent = "Cần nhập tên service và đường dẫn binary.";
    return;
  }

  d.submit.disabled = true;
  d.status.textContent = "Đang gửi…";

  try {
    const result = await callApi("/api/services", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });

    showToast(result?.message ?? "Đã tạo service.", true);
    d.status.textContent = "";
    d.root.hidden = true;
    await loadServices();
  } catch (err) {
    // Giu hop thoai mo de sua lai.
    d.status.textContent = err.message;
    showToast(err.message, false);
  } finally {
    d.submit.disabled = false;
  }
}

document.getElementById("service-dialog-open")?.addEventListener("click", () => {
  const d = ensureServiceDialog();
  d.root.hidden = false;
  d.showTab("general");
  d.status.textContent = "";
});

/**
 * Event gan nhat cua MOT doi tuong. latestByObject chi co thoi diem, khong co Event ID
 * (xem GetLatestForObject) nen phai hoi rieng - chi khi mo modal.
 */
async function fetchLatestEvent(type, objectName) {
  try {
    const res = await fetch(
      `/api/events/latest?type=${type}&objectName=${encodeURIComponent(objectName)}`);
    return res.status === 204 || !res.ok ? null : await res.json();
  } catch {
    return null;
  }
}

function latestEventRows(evt) {
  if (!evt) {
    return [["Event ID gần nhất", "— (chưa có event nào trong DB)"]];
  }

  return [
    ["Event ID gần nhất", `${evt.eventId} — ${evt.actionDescription}`],
    ["Thời gian event", formatDate(evt.timeCreated) ?? "—"],
    ["Mức rủi ro", evt.riskLevel],
  ];
}

function taskDetailRows(t) {
  return [
    ["Tên", t.name],
    ["Đường dẫn", t.path],
    ["Trạng thái", t.enabled ? t.state : `${t.state} (đang tắt)`],
    ["Kiểu action", t.actionType ?? "—"],
    ["Lệnh", t.command ?? "—"],
    ["Lần chạy cuối", formatDate(t.lastRunTime) ?? "—"],
    ["Lần chạy kế tiếp", formatDate(t.nextRunTime) ?? "—"],
    ["Thao tác được", t.isWritable ? "Có" : `Không — ngoài rào an toàn (tiền tố "${systemStatus.writablePrefix}")`],
  ];
}

/** Field chi co o /api/tasks/detail, ghep vao sau khi ban day du ve. */
function taskDetailExtraRows(d) {
  const triggers = d.triggers?.length
    ? d.triggers.map((tr) => `${tr.type}${tr.enabled ? "" : " (tắt)"}${tr.startBoundary ? " — " + tr.startBoundary : ""}`).join("\n")
    : "— (không có trigger)";

  const actions = d.actions?.length
    ? d.actions.map((a) => [a.type, a.path, a.arguments].filter(Boolean).join(" ")).join("\n")
    : "— (không đọc được action)";

  return [
    ["Author", d.author ?? "—"],
    ["Mô tả", d.description ?? "—"],
    ["Ngày đăng ký", d.registrationDate ?? "—"],
    // '||' chu khong phai '??': task chay theo nhom tra userId = chuoi RONG (khong
    // phai null) nen '??' se khong roi sang groupId.
    ["User (Principal)", d.userId || d.groupId || "—"],
    ["Logon type", d.logonType ?? "—"],
    ["Run level", d.runLevel ?? "—"],
    ["Ẩn (Hidden)", d.hidden ? "Có" : "Không"],
    ["Kết quả lần chạy cuối", d.lastTaskResult ?? "—"],
    ["Số lần lỡ chạy", d.numberOfMissedRuns ?? "—"],
    [`Trigger (${d.triggers?.length ?? 0})`, triggers],
    [`Action (${d.actions?.length ?? 0})`, actions],
  ];
}

/**
 * Modal thong tin dung chung voi tab Dashboard: tab "Tong quan" la cac field
 * cua task (khong phai cua mot event), tab "Chi tiet" la XML dinh nghia task
 * (thay cho RawXml vi task khong phai la mot event). `initialTab` cho nut
 * "XML" nhay thang vao tab do thay vi luon mo "Tong quan" truoc.
 */
async function openTaskDetail(t, initialTab) {
  const modal = $("modal");
  $("modal-title").textContent = `Task — ${t.name}`;

  // Ve ngay phan da co trong dong danh sach, roi bo sung dan khi 2 request ve -
  // modal khong bao gio hien rong.
  window.renderModalMeta(taskDetailRows(t));
  $("modal-xml").textContent = "Đang tải…";
  modal.hidden = false;
  window.resetModalTabs();
  if (initialTab === "details") window.setModalTab("details");

  const path = encodeURIComponent(t.path);

  const detail = Promise.all([
    fetch(`/api/tasks/detail?path=${path}`).then((res) => (res.ok ? res.json() : null)),
    fetchLatestEvent("ScheduledTask", t.path),
  ])
    .then(([d, evt]) => {
      window.renderModalMeta([
        ...taskDetailRows(t),
        ...latestEventRows(evt),
        ...(d ? taskDetailExtraRows(d) : []),
      ]);
    })
    .catch((err) => console.error("Khong lay duoc chi tiet task:", err));

  const xml = fetch(`/api/tasks/xml?path=${path}`)
    .then(async (res) => {
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      $("modal-xml").textContent = await res.text();
    })
    .catch((err) => {
      $("modal-xml").textContent = "Không đọc được XML: " + err.message;
    });

  await Promise.all([detail, xml]);
}

function serviceDetailRows(s) {
  return [
    ["Tên", s.name],
    ["Tên hiển thị", s.displayName],
    ["Trạng thái", s.state],
    ["Start type", s.startType],
    ["Binary", s.imagePath],
    ["Tài khoản chạy", s.account ?? "—"],
    ["Thao tác được", s.isWritable ? "Có" : `Không — ngoài rào an toàn (tiền tố "${systemStatus.writablePrefix}")`],
  ];
}

/** Field chi co o /api/services/{name}. */
function serviceDetailExtraRows(d) {
  return [
    ["Delayed auto start", d.delayedAutoStart ? "Có" : "Không"],
    ["Mô tả", d.description ?? "—"],
    ["Kiểu service", d.serviceTypeText],
    ["Error control", d.errorControl],
    ["Load order group", d.loadOrderGroup ?? "—"],
    ["Process ID", d.processId ?? "— (đang dừng)"],
    ["Nhận lệnh", [d.canStop ? "Stop" : null, d.canPauseContinue ? "Pause/Continue" : null].filter(Boolean).join(", ") || "—"],
    [`Phụ thuộc (${d.dependencies?.length ?? 0})`, d.dependencies?.length ? d.dependencies.join("\n") : "— (không phụ thuộc service nào)"],
    // Recovery la field dang chu y nhat ve bao mat: malware dat recovery de service
    // tu song lai sau khi bi kill.
    [`Recovery (${d.recoveryActions?.length ?? 0})`, d.recoveryActions?.length ? d.recoveryActions.join("\n") : "— (không cấu hình)"],
  ];
}

/** Service khong co dinh nghia XML nhu Task - tab "Chi tiet" chi ghi ro dieu do. */
async function openServiceDetail(s) {
  const modal = $("modal");
  $("modal-title").textContent = `Service — ${s.name}`;
  window.renderModalMeta(serviceDetailRows(s));
  $("modal-xml").textContent = "Service không có định nghĩa XML như Scheduled Task — dùng advapi32 (QueryServiceConfig/QueryServiceConfig2), không phải COM Schedule.Service.";
  modal.hidden = false;
  window.resetModalTabs();

  try {
    const [res, evt] = await Promise.all([
      fetch(`/api/services/${encodeURIComponent(s.name)}`),
      fetchLatestEvent("Service", s.name),
    ]);

    const d = res.ok ? await res.json() : null;
    window.renderModalMeta([
      ...serviceDetailRows(s),
      ...latestEventRows(evt),
      ...(d ? serviceDetailExtraRows(d) : []),
    ]);
  } catch (err) {
    console.error("Khong lay duoc chi tiet service:", err);
  }
}

async function loadTasks() {
  try {
    const [tasks, latest] = await Promise.all([
      callApi("/api/tasks"),
      loadLatestByObject("ScheduledTask"),
    ]);
    allTasks = tasks;
    taskLatest = latest;
    renderTasks();
    // Cot cuoi la "Thao tac" (nut bam) - khong can keo.
    makeColumnsResizable($("tasks-table"), "tasks", { resizeLast: false });
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
  let visible = allServices.filter(servicePasses);
  if (mg.servicesSort.value === "recent") {
    visible = sortByRecent(visible, serviceLatest, "name");
  }
  mg.servicesBody.replaceChildren();

  for (const s of visible) {
    const tr = document.createElement("tr");
    if (!s.isWritable) tr.className = "is-readonly";
    const blocked = blockedReason(s.isWritable);

    tr.addEventListener("click", (e) => {
      if (e.target.closest(".actions")) return;
      openServiceDetail(s);
    });

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
    const [services, latest] = await Promise.all([
      callApi("/api/services"),
      loadLatestByObject("Service"),
    ]);
    allServices = services;
    serviceLatest = latest;
    renderServices();
    // Cot cuoi la "Thao tac" (nut bam) - khong can keo.
    makeColumnsResizable($("services-table"), "services", { resizeLast: false });
  } catch (err) {
    showToast("Không tải được danh sách service: " + err.message, false);
  }
}

// ---------------------------------------------------------------- Su kien UI

// Form tao nam san trong trang da bo - viec tao/sua chuyen het sang hop thoai
// modeless (openTaskDialog / ensureServiceDialog o tren). Giu hai duong nhap lieu
// cho cung mot viec chi lam nguoi dung phan van dung cai nao.

for (const control of [mg.tasksSearch, mg.tasksState, mg.tasksAction, mg.tasksOnlyWritable, mg.tasksSort]) {
  control.addEventListener("input", renderTasks);
}
for (const control of [mg.servicesSearch, mg.servicesState, mg.servicesStartType, mg.servicesOnlyWritable, mg.servicesSort]) {
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

window.onTabShown.subscribe((tab) => {
  if (tab === "tasks" && !loaded.tasks) { loaded.tasks = true; loadTasks(); }
  if (tab === "services" && !loaded.services) { loaded.services = true; loadServices(); }
});

window.eventBus.subscribe(onRealtimeEvent);

// Xuất tường minh: logexport.js và logsbrowse.js đều báo kết quả qua thẻ #toast này.
// CHỈ có một thẻ #toast trên trang, nên đây là loại thông báo "một dòng, thay thế
// nhau" — cảnh báo bảo mật cần xếp chồng thì dùng #alert-stack của alerts.js.
window.showToast = showToast;

loadSystemStatus();

})();
