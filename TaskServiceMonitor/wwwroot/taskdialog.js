"use strict";

// Hộp thoại "Create Task" 5 tab, dựng theo hộp thoại cùng tên của Windows.
//
// MODELESS — cố ý không có lớp phủ chặn: mở hộp thoại rồi vẫn cuộn/bấm được bảng
// phía sau, kéo di chuyển được như cửa sổ thật. Khác hẳn #modal (position:fixed;
// inset:0 + overlay) vốn chặn toàn trang, nên KHÔNG dùng lại #modal ở đây.

const TASK_DIALOG_TABS = [
  "general",
  "triggers",
  "actions",
  "principal",
  "settings",
];

function td(tag, attrs = {}, children = []) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") el.className = v;
    else if (k === "text") el.textContent = v;
    else el.setAttribute(k, v);
  }
  for (const c of [].concat(children)) el.appendChild(c);
  return el;
}

/** Một dòng nhập: nhãn trên, control dưới. */
function field(labelText, control, hint) {
  const label = td("label", { class: "dialog__field" });
  label.appendChild(td("span", { class: "dialog__label", text: labelText }));
  label.appendChild(control);
  if (hint)
    label.appendChild(td("span", { class: "dialog__hint", text: hint }));
  return label;
}

function input(id, attrs = {}) {
  return td("input", { id, type: "text", ...attrs });
}

function select(id, options, selected) {
  const el = td("select", { id });
  for (const [value, text] of options) {
    const o = td("option", { value, text });
    if (value === selected) o.selected = true;
    el.appendChild(o);
  }
  return el;
}

function checkbox(id, labelText, checked) {
  const wrap = td("label", { class: "dialog__check" });
  const box = td("input", { id, type: "checkbox" });
  box.checked = Boolean(checked);
  wrap.append(box, document.createTextNode(" " + labelText));
  return wrap;
}

// ---------------------------------------------------------------- Trigger / Action rows

function triggerRow(data = {}) {
  const row = td("div", { class: "dialog__row" });

  const type = select(
    "",
    [
      ["Time", "Một lần (Time)"],
      ["Daily", "Hằng ngày (Daily)"],
      ["Logon", "Khi đăng nhập (Logon)"],
      ["Boot", "Khi khởi động máy (Boot)"],
      ["Registration", "Khi đăng ký task (Registration)"],
    ],
    data.type ?? "Time",
  );
  type.dataset.role = "trigger-type";

  const when = td("input", { type: "datetime-local" });
  when.dataset.role = "trigger-start";
  if (data.startBoundary) when.value = String(data.startBoundary).slice(0, 16);

  const enabled = td("input", { type: "checkbox" });
  enabled.dataset.role = "trigger-enabled";
  enabled.checked = data.enabled !== false;

  const remove = td("button", {
    type: "button",
    class: "btn btn--small",
    text: "Xoá",
  });
  remove.addEventListener("click", () => row.remove());

  row.append(
    field("Loại", type),
    field("Bắt đầu", when),
    field("Bật", enabled),
    remove,
  );

  return row;
}

function actionRow(data = {}) {
  const row = td("div", { class: "dialog__row" });

  const program = td("input", {
    type: "text",
    placeholder: "C:\\Windows\\System32\\cmd.exe",
  });
  program.dataset.role = "action-command";
  program.value = data.command ?? data.path ?? "";

  const args = td("input", { type: "text", placeholder: "/c echo hello" });
  args.dataset.role = "action-arguments";
  args.value = data.arguments ?? "";

  const workDir = td("input", { type: "text", placeholder: "(tuỳ chọn)" });
  workDir.dataset.role = "action-workdir";
  workDir.value = data.workingDirectory ?? "";

  const remove = td("button", {
    type: "button",
    class: "btn btn--small",
    text: "Xoá",
  });
  remove.addEventListener("click", () => row.remove());

  row.append(
    field("Chương trình", program),
    field("Tham số", args),
    field("Thư mục làm việc", workDir),
    remove,
  );

  return row;
}

// ---------------------------------------------------------------- Dựng hộp thoại

function buildTaskDialog() {
  const root = td("div", { class: "dialog", id: "task-dialog", hidden: "" });

  // --- Thanh tieu de (keo duoc) ---
  const head = td("div", { class: "dialog__head" });
  const title = td("strong", {
    class: "dialog__title",
    id: "task-dialog-title",
    text: "Create Task",
  });
  const close = td("button", {
    type: "button",
    class: "dialog__close",
    text: "×",
    title: "Đóng (Esc)",
  });
  head.append(title, close);

  // --- Tab ---
  const tabs = td("div", { class: "dialog__tabs" });
  const bodies = {};
  const labels = {
    general: "General",
    triggers: "Triggers",
    actions: "Actions",
    principal: "Principal",
    settings: "Settings",
  };

  for (const name of TASK_DIALOG_TABS) {
    const btn = td("button", {
      type: "button",
      class: "dialog__tab",
      text: labels[name],
    });
    btn.dataset.tab = name;
    btn.addEventListener("click", () => showTab(name));
    tabs.appendChild(btn);

    bodies[name] = td("div", { class: "dialog__body" });
    bodies[name].hidden = true;
  }

  function showTab(name) {
    for (const btn of tabs.querySelectorAll(".dialog__tab")) {
      btn.classList.toggle("is-active", btn.dataset.tab === name);
    }
    for (const [key, body] of Object.entries(bodies))
      body.hidden = key !== name;
  }

  // --- General ---
  bodies.general.append(
    field(
      "Tên task",
      input("td-name", { placeholder: "WinSentinelDemo" }),
      "chỉ chữ/số/._- và phải bắt đầu bằng tiền tố an toàn",
    ),
    field(
      "Location",
      input("td-location", { value: "\\", readonly: "" }),
      "chỉ tạo được ở thư mục gốc",
    ),
    field("Author", input("td-author")),
    field("Description", td("textarea", { id: "td-description", rows: "2" })),
    checkbox("td-hidden", "Hidden (task ẩn)"),
  );

  // --- Triggers ---
  const triggerList = td("div", { class: "dialog__list", id: "td-triggers" });
  const addTrigger = td("button", {
    type: "button",
    class: "btn",
    text: "+ Thêm trigger",
  });
  addTrigger.addEventListener("click", () =>
    triggerList.appendChild(triggerRow()),
  );
  bodies.triggers.append(triggerList, addTrigger);

  // --- Actions ---
  const actionList = td("div", { class: "dialog__list", id: "td-actions" });
  const addAction = td("button", {
    type: "button",
    class: "btn",
    text: "+ Thêm action",
  });
  addAction.addEventListener("click", () =>
    actionList.appendChild(actionRow()),
  );
  bodies.actions.append(actionList, addAction);

  // --- Principal ---
  bodies.principal.append(
    field("User ID", input("td-userid", { placeholder: "DOMAIN\\User" })),
    field(
      "Group ID",
      input("td-groupid", { placeholder: "BUILTIN\\Administrators" }),
      "khai Group thì User bị bỏ qua — hai cái loại trừ nhau",
    ),
    field(
      "Logon type",
      select(
        "td-logontype",
        [
          ["InteractiveToken", "InteractiveToken (đã đăng nhập)"],
          ["S4U", "S4U (không cần mật khẩu)"],
          ["Password", "Password"],
          ["Group", "Group"],
          ["ServiceAccount", "ServiceAccount"],
        ],
        "InteractiveToken",
      ),
    ),
    field(
      "Run level",
      select(
        "td-runlevel",
        [
          ["LeastPrivilege", "LeastPrivilege (thường)"],
          ["HighestAvailable", "HighestAvailable (quyền Administrator)"],
        ],
        "LeastPrivilege",
      ),
      "HighestAvailable = chạy quyền cao nhất",
    ),
  );

  // --- Settings ---
  bodies.settings.append(
    checkbox("td-allowdemand", "Cho phép chạy tay (AllowStartOnDemand)", true),
    checkbox("td-battery", "Không chạy khi dùng pin"),
    field(
      "Nhiều instance",
      select(
        "td-instances",
        [
          ["IgnoreNew", "IgnoreNew"],
          ["Parallel", "Parallel"],
          ["Queue", "Queue"],
          ["StopExisting", "StopExisting"],
        ],
        "IgnoreNew",
      ),
    ),
    field(
      "Giới hạn thời gian chạy",
      input("td-timelimit", { placeholder: "PT1H (ISO 8601)" }),
      "để trống = theo mặc định của Windows (72 giờ)",
    ),
  );

  // --- Chan ---
  const foot = td("div", { class: "dialog__foot" });
  const apiMode = select(
    "td-api",
    [
      ["xml", "Dựng XML (mặc định)"],
      ["objectmodel", "COM NewTask()"],
    ],
    "xml",
  );
  const submit = td("button", {
    type: "button",
    class: "btn btn--primary",
    text: "Tạo / ghi đè",
  });
  const status = td("span", { class: "dialog__status", id: "td-status" });
  foot.append(field("Cách gọi API", apiMode), submit, status);

  root.append(head, tabs, ...Object.values(bodies), foot);
  document.body.appendChild(root);

  makeDraggable(root, head);
  showTab("general");

  return {
    root,
    title,
    close,
    submit,
    status,
    triggerList,
    actionList,
    apiMode,
    showTab,
  };
}

/** Kéo theo thanh tiêu đề — cùng pattern pointer capture đã dùng ở detailpane.js. */
function makeDraggable(root, handle) {
  handle.addEventListener("pointerdown", (e) => {
    if (e.target.closest("button")) return;

    e.preventDefault();
    handle.setPointerCapture(e.pointerId);

    const rect = root.getBoundingClientRect();
    const dx = e.clientX - rect.left;
    const dy = e.clientY - rect.top;

    const onMove = (m) => {
      // Ghim trong khung nhin de khong keo mat hop thoai ra ngoai man hinh.
      const x = Math.max(
        0,
        Math.min(m.clientX - dx, window.innerWidth - rect.width),
      );
      const y = Math.max(
        0,
        Math.min(m.clientY - dy, window.innerHeight - rect.height),
      );
      root.style.left = x + "px";
      root.style.top = y + "px";
      root.style.right = "auto";
      root.style.transform = "none";
    };

    const onUp = () => {
      handle.removeEventListener("pointermove", onMove);
      handle.removeEventListener("pointerup", onUp);
    };

    handle.addEventListener("pointermove", onMove);
    handle.addEventListener("pointerup", onUp);
  });
}

// ---------------------------------------------------------------- Hop thoai Service

/**
 * Cùng khung modeless với Create Task. Service không có 5 tab như Task Scheduler
 * (services.msc cũng không có hộp thoại "tạo mới"), nên chỉ chia 2 tab cho gọn.
 */
function buildServiceDialog() {
  const root = td("div", { class: "dialog", id: "service-dialog", hidden: "" });

  const head = td("div", { class: "dialog__head" });
  const title = td("strong", { class: "dialog__title", text: "Tạo Service" });
  const close = td("button", {
    type: "button",
    class: "dialog__close",
    text: "×",
    title: "Đóng (Esc)",
  });
  head.append(title, close);

  const tabs = td("div", { class: "dialog__tabs" });
  const bodies = {
    general: td("div", { class: "dialog__body" }),
    advanced: td("div", { class: "dialog__body" }),
  };
  bodies.advanced.hidden = true;

  for (const [name, label] of [
    ["general", "General"],
    ["advanced", "Dependencies / Account"],
  ]) {
    const btn = td("button", {
      type: "button",
      class: "dialog__tab",
      text: label,
    });
    btn.dataset.tab = name;
    btn.addEventListener("click", () => showTab(name));
    tabs.appendChild(btn);
  }

  function showTab(name) {
    for (const btn of tabs.querySelectorAll(".dialog__tab")) {
      btn.classList.toggle("is-active", btn.dataset.tab === name);
    }
    for (const [key, body] of Object.entries(bodies))
      body.hidden = key !== name;
  }

  bodies.general.append(
    field(
      "Tên service",
      input("sd-name", { placeholder: "WinSentinelDemoSvc" }),
      "phải bắt đầu bằng tiền tố an toàn",
    ),
    field("Tên hiển thị", input("sd-displayname")),
    field(
      "Đường dẫn binary",
      input("sd-binarypath", { value: "C:\\Windows\\System32\\snmptrap.exe" }),
    ),
    field("Mô tả", td("textarea", { id: "sd-description", rows: "2" })),
    // 5 start type - truoc day UI chi co 3, thieu boot/system du backend van nhan.
    field(
      "Start type",
      select(
        "sd-starttype",
        [
          ["demand start", "Demand (thủ công)"],
          ["auto start", "Automatic"],
          ["disabled", "Disabled"],
          ["boot start", "Boot"],
          ["system start", "System"],
        ],
        "demand start",
      ),
    ),
  );

  bodies.advanced.append(
    field(
      "Phụ thuộc",
      input("sd-dependencies", { placeholder: "RpcSs, LanmanWorkstation" }),
      "ngăn cách bằng dấu phẩy",
    ),
    field(
      "Tài khoản chạy",
      select(
        "sd-account",
        [
          ["LocalSystem", "LocalSystem (mặc định)"],
          ["NT AUTHORITY\\LocalService", "LocalService"],
          ["NT AUTHORITY\\NetworkService", "NetworkService"],
        ],
        "LocalSystem",
      ),
      "chỉ nhận 3 tài khoản dựng sẵn — đều không cần mật khẩu",
    ),
  );

  const foot = td("div", { class: "dialog__foot" });
  const submit = td("button", {
    type: "button",
    class: "btn btn--primary",
    text: "Tạo service",
  });
  const status = td("span", { class: "dialog__status", id: "sd-status" });
  foot.append(submit, status);

  root.append(head, tabs, bodies.general, bodies.advanced, foot);
  document.body.appendChild(root);

  makeDraggable(root, head);
  showTab("general");

  return { root, close, submit, status, showTab };
}

window.buildTaskDialog = buildTaskDialog;
window.buildServiceDialog = buildServiceDialog;
window.taskDialogRows = { triggerRow, actionRow };
