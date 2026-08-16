"use strict";

// "Custom Views" kieu Event Viewer: luu lai mot to hop gia tri cua cac o
// filter/search/sort thanh MOT view co ten, bam lai la ap dung ngay. Chi
// client-side (localStorage), theo dung khuon da dung cho colwidths:*/theme -
// try/catch quanh moi lan doc/ghi vi localStorage co the bi chan (che do
// duyet web rieng tu).
//
// Gan qua marker khai bao tuong minh trong HTML, KHONG do DOM tim filter -
// dung phong cach da chon cho colresize.js/filtermenu.js (ro rang, de doc,
// khong doan ngam):
//   <div class="custom-views" data-scope="dashboard"
//        data-view-fields="filter-host,filter-type,filter-risk"></div>

function storageKey(scope) {
  return `views:${scope}`;
}

function readViews(scope) {
  try {
    const raw = localStorage.getItem(storageKey(scope));
    return raw ? JSON.parse(raw) : {};
  } catch {
    return {};
  }
}

function writeViews(scope, views) {
  try {
    localStorage.setItem(storageKey(scope), JSON.stringify(views));
  } catch {
    // localStorage co the bi chan - bo qua, khong quan trong.
  }
}

function readFieldValue(field) {
  return field.type === "checkbox" ? field.checked : field.value;
}

/**
 * Ghi gia tri vao 1 field ROI TU BAO cac listener da gan san tu truoc (Dashboard
 * dung "change", Tasks/Services/filtermenu dung "input") de chung tu render
 * lai - khong can biet/goi truc tiep tung ham render rieng cua tung tab.
 */
function writeFieldValue(field, value) {
  if (field.type === "checkbox") {
    field.checked = Boolean(value);
  } else {
    field.value = value;
  }

  field.dispatchEvent(new Event("input", { bubbles: true }));
  field.dispatchEvent(new Event("change", { bubbles: true }));
}

function setupCustomViews(container, scope, fields) {
  const select = document.createElement("select");

  const saveBtn = document.createElement("button");
  saveBtn.type = "button";
  saveBtn.className = "btn";
  saveBtn.textContent = "Lưu bộ lọc này…";

  const deleteBtn = document.createElement("button");
  deleteBtn.type = "button";
  deleteBtn.className = "btn";
  deleteBtn.textContent = "Xoá view";

  container.append(select, saveBtn, deleteBtn);

  function refreshOptions() {
    const views = readViews(scope);
    const names = Object.keys(views).sort((a, b) => a.localeCompare(b));

    select.replaceChildren();
    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = names.length === 0 ? "(chưa lưu view nào)" : "Chọn view đã lưu…";
    select.appendChild(placeholder);

    for (const name of names) {
      const option = document.createElement("option");
      option.value = name;
      option.textContent = name;
      select.appendChild(option);
    }

    deleteBtn.disabled = names.length === 0;
  }

  select.addEventListener("change", () => {
    const name = select.value;
    if (!name) return;

    const saved = readViews(scope)[name];
    if (!saved) return;

    for (const field of fields) {
      if (field.id in saved) {
        writeFieldValue(field, saved[field.id]);
      }
    }
  });

  saveBtn.addEventListener("click", () => {
    const name = prompt("Đặt tên cho bộ lọc này:");
    if (!name || !name.trim()) return;

    const views = readViews(scope);
    const snapshot = {};
    for (const field of fields) {
      snapshot[field.id] = readFieldValue(field);
    }

    views[name.trim()] = snapshot;
    writeViews(scope, views);
    refreshOptions();
    select.value = name.trim();
  });

  deleteBtn.addEventListener("click", () => {
    const name = select.value;
    if (!name) return;
    if (!confirm(`Xoá view "${name}"?`)) return;

    const views = readViews(scope);
    delete views[name];
    writeViews(scope, views);
    refreshOptions();
  });

  refreshOptions();
}

function initCustomViews() {
  for (const container of document.querySelectorAll(".custom-views")) {
    const scope = container.dataset.scope;
    const fieldIds = (container.dataset.viewFields ?? "")
      .split(",")
      .map((s) => s.trim())
      .filter(Boolean);

    if (!scope || fieldIds.length === 0) {
      continue;
    }

    const fields = fieldIds.map((id) => document.getElementById(id)).filter(Boolean);
    if (fields.length === 0) {
      continue;
    }

    setupCustomViews(container, scope, fields);
  }
}

initCustomViews();
