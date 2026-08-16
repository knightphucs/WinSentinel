"use strict";

// Khung chi tiet doc ngay duoi bang log, kieu Event Viewer. Dung cho 4 panel log;
// Dashboard/Tasks/Services van dung modal cu.
//
// Tham so `loadDetail` ton tai vi hai nguon du lieu khac nhau: payload
// /api/logs/browse da co san rawXml, con mang `events` tren client la
// EventSummaryDto KHONG kem rawXml (co y, xem EventDtos.cs) nen phai goi API rieng.

const DETAIL_SPLIT_KEY = "splitheight:";
const DETAIL_MIN_TOP = 120;
const DETAIL_MIN_BOTTOM = 100;

// Nho chieu cao khung duoi, giong cach colresize.js nho chieu rong cot.
function readSplitHeight(prefix) {
  const raw = localStorage.getItem(DETAIL_SPLIT_KEY + prefix);
  const value = Number.parseInt(raw ?? "", 10);
  return Number.isFinite(value) && value > 0 ? value : null;
}

function writeSplitHeight(prefix, px) {
  try {
    localStorage.setItem(DETAIL_SPLIT_KEY + prefix, String(Math.round(px)));
  } catch {
    // localStorage co the bi chan - bo qua, khong quan trong.
  }
}

function metaRow(label, value) {
  const row = document.createElement("div");
  row.className = "meta-row";

  const labelEl = document.createElement("span");
  labelEl.className = "meta-label";
  labelEl.textContent = label;

  const valueEl = document.createElement("span");
  valueEl.className = "meta-value";
  valueEl.textContent =
    value === null || value === undefined || value === "" ? "—" : value;

  row.append(labelEl, valueEl);
  return row;
}

// Thu tu bam dung tab "General" cua Event Viewer - nguoi dung doi chieu hai man
// hinh canh nhau nen KHONG sap xep lai cho "hop ly hon".
function generalRows(evt) {
  return [
    ["Log Name", evt.channel],
    ["Source", evt.providerName],
    ["Logged", evt.timeCreated ? new Date(evt.timeCreated).toLocaleString("vi-VN") : null],
    ["Event ID", evt.eventId],
    ["Task Category", evt.taskCategoryName ?? evt.taskCategoryId],
    ["Level", evt.levelDisplayName ?? evt.level],
    ["Keywords", evt.keywords],
    ["User", evt.actorAccount],
    ["Computer", evt.hostname],
    ["OpCode", evt.opcodeName],
    // Ba dong duoi KHONG co trong Event Viewer - la phan rieng cua app nay.
    ["Rủi ro", evt.riskLevel],
    ["Đối tượng", evt.objectName],
    ["Hành vi", evt.actionDescription],
  ];
}

/**
 * @param {string} prefix Tien to id trong HTML, vd "logs-security".
 * @param {(evt:object)=>Promise<object>} [options.loadDetail] Lay ban day du khi
 *        payload tren client thieu rawXml.
 */
function attachDetailPane(prefix, options = {}) {
  const host = document.getElementById(`${prefix}-detail`);
  if (!host) {
    return { show() {}, clear() {} };
  }

  host.replaceChildren();

  const head = document.createElement("div");
  head.className = "detail-pane__head";

  const title = document.createElement("strong");
  title.className = "detail-pane__title";
  title.textContent = "Chọn một dòng ở trên để xem chi tiết";
  head.appendChild(title);

  const tabs = document.createElement("div");
  tabs.className = "detail-pane__tabs";

  const generalBtn = document.createElement("button");
  generalBtn.type = "button";
  generalBtn.className = "detail-pane__tab is-active";
  generalBtn.textContent = "General";

  const detailsBtn = document.createElement("button");
  detailsBtn.type = "button";
  detailsBtn.className = "detail-pane__tab";
  detailsBtn.textContent = "Details";

  tabs.append(generalBtn, detailsBtn);

  const general = document.createElement("div");
  general.className = "detail-pane__body";

  const description = document.createElement("p");
  description.className = "detail-pane__description";

  const meta = document.createElement("div");
  meta.className = "detail-pane__meta";
  general.append(description, meta);

  const details = document.createElement("pre");
  details.className = "detail-pane__xml detail-pane__body";
  details.hidden = true;

  host.append(head, tabs, general, details);

  function showTab(name) {
    generalBtn.classList.toggle("is-active", name === "general");
    detailsBtn.classList.toggle("is-active", name === "details");
    general.hidden = name !== "general";
    details.hidden = name !== "details";
  }

  generalBtn.addEventListener("click", () => showTab("general"));
  detailsBtn.addEventListener("click", () => showTab("details"));

  // Thanh keo chinh chieu cao.
  const bar = host.previousElementSibling;
  const top = bar?.previousElementSibling;

  const saved = readSplitHeight(prefix);
  if (saved) host.style.height = saved + "px";

  if (bar && top) {
    bar.addEventListener("pointerdown", (e) => {
      e.preventDefault();
      bar.setPointerCapture(e.pointerId);
      document.body.classList.add("is-row-resizing");

      const startY = e.clientY;
      const startHeight = host.offsetHeight;
      const available = startHeight + top.offsetHeight;

      const onMove = (move) => {
        // Keo len thi khung duoi to ra -> dau tru.
        let next = startHeight - (move.clientY - startY);
        next = Math.max(DETAIL_MIN_BOTTOM, Math.min(next, available - DETAIL_MIN_TOP));
        host.style.height = next + "px";
      };

      const onUp = () => {
        bar.removeEventListener("pointermove", onMove);
        bar.removeEventListener("pointerup", onUp);
        document.body.classList.remove("is-row-resizing");
        writeSplitHeight(prefix, host.offsetHeight);
      };

      bar.addEventListener("pointermove", onMove);
      bar.addEventListener("pointerup", onUp);
    });
  }

  function renderGeneral(evt) {
    if (evt.description) {
      description.textContent = evt.description;
      description.classList.remove("muted");
    } else {
      description.textContent = evt.rawXml || evt.id
        ? "(Không có mô tả — provider không cài message DLL, hoặc event được lưu " +
          "trước khi app biết đọc Description.)"
        : "(Không có mô tả.)";
      description.classList.add("muted");
    }

    meta.replaceChildren();
    for (const [label, value] of generalRows(evt)) {
      meta.appendChild(metaRow(label, value));
    }
  }

  let requestToken = 0;

  async function show(evt) {
    title.textContent = `${evt.actionDescription ?? "Event"} — ${evt.objectName ?? "(không tên)"}`;
    renderGeneral(evt);

    if (evt.rawXml) {
      details.textContent = evt.rawXml;
      return;
    }

    if (!options.loadDetail) {
      details.textContent = "(Không có XML thô cho dòng này.)";
      return;
    }

    // Bam nhanh qua nhieu dong -> chi ban cuoi cung duoc ghi vao khung, khong thi
    // ket qua ve cham se de len dong dang chon.
    const token = ++requestToken;
    details.textContent = "Đang tải…";

    try {
      const full = await options.loadDetail(evt);
      if (token !== requestToken) return;

      renderGeneral({ ...evt, ...full });
      details.textContent = full.rawXml ?? "(Không có XML thô.)";
    } catch (err) {
      if (token !== requestToken) return;
      console.error("Khong lay duoc chi tiet event:", err);
      details.textContent = "Không tải được chi tiết event — xem console.";
    }
  }

  function clear() {
    requestToken++;
    title.textContent = "Chọn một dòng ở trên để xem chi tiết";
    description.textContent = "";
    meta.replaceChildren();
    details.textContent = "";
    showTab("general");
  }

  return { show, clear };
}

// Tach rieng khoi attachDetailPane vi moi bang tu dung <tr> theo cach cua no.
//
// Ho tro chon nhieu dong nhu Event Viewer: click thuong = chon mot, Ctrl+click =
// them/bot, Shift+click = chon ca dai tu dong neo. Dong neo (anchor) luu tren chinh
// tbody de moi bang co neo rieng.
function selectRow(tbody, tr, event) {
  const rows = [...tbody.querySelectorAll("tr")];
  const ctrl = event?.ctrlKey || event?.metaKey;
  const shift = event?.shiftKey;

  if (shift && tbody.__anchorRow && rows.includes(tbody.__anchorRow)) {
    const from = rows.indexOf(tbody.__anchorRow);
    const to = rows.indexOf(tr);
    const [lo, hi] = from < to ? [from, to] : [to, from];

    clearSelection(tbody);
    for (let i = lo; i <= hi; i++) mark(rows[i], true);
    return;
  }

  if (ctrl) {
    mark(tr, !tr.classList.contains("is-selected"));
    tbody.__anchorRow = tr;
    return;
  }

  clearSelection(tbody);
  mark(tr, true);
  tbody.__anchorRow = tr;
}

function mark(tr, selected) {
  tr.classList.toggle("is-selected", selected);
  if (selected) {
    tr.setAttribute("aria-selected", "true");
  } else {
    tr.removeAttribute("aria-selected");
  }
}

function clearSelection(tbody) {
  for (const other of tbody.querySelectorAll("tr.is-selected")) mark(other, false);
}

/** RecordId cua cac dong dang chon - dau vao cho "luu event dang chon". */
function selectedRecordIds(tbody) {
  return [...tbody.querySelectorAll("tr.is-selected")]
    .map((tr) => Number(tr.dataset.recordId))
    .filter((id) => Number.isFinite(id) && id > 0);
}

window.attachDetailPane = attachDetailPane;
window.selectRow = selectRow;
window.selectedRecordIds = selectedRecordIds;
