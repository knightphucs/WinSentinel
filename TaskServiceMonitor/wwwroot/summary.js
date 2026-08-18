"use strict";

// Bang "Overview and Summary" kieu Event Viewer - dem event theo RiskLevel,
// cat theo 1 gio / 24 gio / 7 ngay. Moi dong co nut "+" xo ra breakdown theo
// (Event ID, Source, Log) - giong nut "+" tren "Summary of Administrative
// Events" that. Khac 3 card phia tren (tinh tu toi da 200 event dang giu o
// client, chi xap xi): bang nay query DB that qua /api/events/summary nen
// chinh xac ke ca khung "7 ngay".

const summaryBody = document.getElementById("summary-body");

function numberCell(value) {
  const td = document.createElement("td");
  td.textContent = value;
  return td;
}

/**
 * Dong tong hop theo muc rui ro - Event ID/Source/Log de trong ("—") vi day la
 * dong TONG, khong ung voi mot Event ID cu the nao. Cung cot voi cac dong chi
 * tiet ben duoi (khong phai bang long nhu truoc) de nguoi dung thay het cot
 * ngay tren bang chinh, khong phai xoe ra moi thay.
 */
function buildCategoryRow(row) {
  const tr = document.createElement("tr");
  tr.className = "ov-category-row";

  const toggleCell = document.createElement("td");
  const toggle = document.createElement("button");
  toggle.type = "button";
  toggle.className = "ov-expand-toggle";
  toggle.textContent = "+";
  toggle.setAttribute("aria-expanded", "false");
  toggle.disabled = row.breakdown.length === 0;
  toggle.title = row.breakdown.length === 0
    ? "Không có event nào"
    : "Xem chi tiết theo Event ID / Source / Log";
  toggleCell.appendChild(toggle);

  // riskCell() da co san trong app.js, dung chung cho ca Dashboard va Duyet
  // log khac - o day can gan them nut "+" nen dung badge rieng, khong goi
  // thang riskCell() (ham do tra ve ca <td>, khong ghep them duoc nut).
  const badge = document.createElement("span");
  badge.className = "risk risk--" + row.riskLevel;
  badge.textContent = row.riskLevel;
  toggleCell.appendChild(badge);
  tr.appendChild(toggleCell);

  tr.appendChild(numberCell("—"));
  tr.appendChild(numberCell("—"));
  tr.appendChild(numberCell("—"));
  tr.appendChild(numberCell(row.lastHour));
  tr.appendChild(numberCell(row.last24h));
  tr.appendChild(numberCell(row.last7d));

  return { tr, toggle };
}

function buildDetailRow(item) {
  const tr = document.createElement("tr");
  tr.className = "ov-detail-row";
  tr.hidden = true;

  // Cot dau (toggle/badge) de trong - tu tao cam giac thut vao dung nhu cac
  // cot khac vi cung mot bang, khong can CSS padding-left rieng.
  tr.appendChild(document.createElement("td"));
  tr.appendChild(numberCell(item.eventId));
  tr.appendChild(numberCell(item.source));
  tr.appendChild(numberCell(item.log));
  tr.appendChild(numberCell(item.lastHour));
  tr.appendChild(numberCell(item.last24h));
  tr.appendChild(numberCell(item.last7d));

  return tr;
}

/** Tra ve [dong tong hop, ...dong chi tiet] - cac dong chi tiet an mac dinh. */
function buildSummaryRows(row) {
  const { tr, toggle } = buildCategoryRow(row);
  const detailRows = row.breakdown.map(buildDetailRow);

  toggle.addEventListener("click", () => {
    const expanded = toggle.getAttribute("aria-expanded") === "true";
    toggle.setAttribute("aria-expanded", String(!expanded));
    toggle.textContent = expanded ? "+" : "−";
    for (const detailRow of detailRows) detailRow.hidden = expanded;
  });

  return [tr, ...detailRows];
}

async function loadSummary() {
  if (!summaryBody) return;

  try {
    const res = await fetch("/api/events/summary");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const rows = await res.json();

    summaryBody.replaceChildren();
    for (const row of rows) {
      summaryBody.append(...buildSummaryRows(row));
    }

    // Dashboard la tab mac dinh hien san luc trang vua tai, nen panel nay
    // khong bi [hidden] - an toan de do offsetWidth ngay.
    // Key doi thanh "summary-v2" vi bang truoc chi co 4 cot, gio la 7 - be
    // rong da luu o localStorage cu se lech cot neu dung lai chung key.
    makeColumnsResizable(document.getElementById("summary-table"), "summary-v2");
  } catch (err) {
    console.error("Khong tai duoc Overview/Summary:", err);
    summaryBody.replaceChildren();

    const tr = document.createElement("tr");
    const td = document.createElement("td");
    td.colSpan = 7;
    td.className = "muted";
    td.textContent = "Không tải được — xem console.";
    tr.appendChild(td);
    summaryBody.appendChild(tr);
  }
}

// ---------------------------------------------------------------- Log Summary

const logSummaryBody = document.getElementById("log-summary-body");

function statusCell(status) {
  const td = document.createElement("td");
  const badge = document.createElement("span");

  if (!status.subscribed) {
    badge.className = "badge badge--fail";
    badge.textContent = `✗ Không subscribe được${status.error ? ": " + status.error : ""}`;
  } else if (status.error) {
    badge.className = "badge badge--warn";
    badge.textContent = `⚠ Subscribe được nhưng lỗi đọc: ${status.error}`;
  } else if (status.eventsReceived > 0) {
    badge.className = "badge badge--ok";
    badge.textContent = "✓ Đang nhận";
  } else {
    badge.className = "badge badge--warn";
    badge.textContent = "✓ Đã subscribe (chưa có event)";
  }

  td.appendChild(badge);
  return td;
}

/**
 * Cursor dung de subscribe channel nay (co che "resume sau restart" - CLAUDE.md
 * muc "Buoc 7"), kem badge "khoi phuc N" khi da nhan duoc event NAM TRONG phan doc
 * bu (recordId <= snapshot luc subscribe - xem ChannelStatusRegistry).
 *
 * Gia tri nay DONG BANG luc khoi dong, khong bao gio doi - do la ly do phai tach
 * cot "RecordId mới nhất" rieng, xem lastRecordIdCell.
 */
function resumeCursorCell(status) {
  const td = document.createElement("td");

  if (status.resumeFromRecordId === null || status.resumeFromRecordId === undefined) {
    td.textContent = "— (lần đầu, chưa có cursor)";
    td.className = "muted";
    return td;
  }

  td.appendChild(document.createTextNode(String(status.resumeFromRecordId)));

  if (status.caughtUpCount > 0) {
    // <button> chu khong phai <span>: con so nay truoc day la ngo cut - biet la CO
    // doc bu nhung khong xem duoc doc bu NHUNG GI. Nay bam vao mo tab "Khoi phuc",
    // loc san dung channel nay.
    const badge = document.createElement("button");
    badge.type = "button";
    badge.className = "badge badge--info badge--link";
    badge.title = `Xem ${status.caughtUpCount} event đã đọc bù trên channel "${status.channel}"`;
    badge.textContent = `↺ khôi phục ${status.caughtUpCount}`;
    badge.addEventListener("click", () => window.showRecovery(status.channel));
    td.appendChild(badge);
  }

  return td;
}

/** RecordId cua event MOI NHAT nhan duoc trong phien chay nay - tang dan theo thoi gian. */
function lastRecordIdCell(status) {
  const td = document.createElement("td");

  if (status.lastRecordId === null || status.lastRecordId === undefined) {
    td.textContent = "— (chưa nhận event nào)";
    td.className = "muted";
    return td;
  }

  td.textContent = String(status.lastRecordId);
  return td;
}

async function loadLogSummary() {
  if (!logSummaryBody) return;

  try {
    const res = await fetch("/api/system/channels");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const rows = await res.json();

    logSummaryBody.replaceChildren();

    if (rows.length === 0) {
      const tr = document.createElement("tr");
      const td = document.createElement("td");
      td.colSpan = 6;
      td.className = "muted";
      td.textContent = "Chưa có channel nào subscribe (app vừa khởi động?).";
      tr.appendChild(td);
      logSummaryBody.appendChild(tr);
      return;
    }

    for (const status of rows) {
      const tr = document.createElement("tr");
      tr.appendChild(numberCell(status.channel));
      tr.appendChild(statusCell(status));
      tr.appendChild(numberCell(status.eventsReceived));
      tr.appendChild(resumeCursorCell(status));
      tr.appendChild(lastRecordIdCell(status));
      tr.appendChild(numberCell(status.lastEventUtc ? new Date(status.lastEventUtc).toLocaleString("vi-VN") : "—"));
      logSummaryBody.appendChild(tr);
    }

    // Bump key moi lan doi so cot (5 -> 6): be rong da luu o localStorage cu se lech
    // cot neu dung lai chung key.
    makeColumnsResizable(document.getElementById("log-summary-table"), "log-summary-v3");
  } catch (err) {
    console.error("Khong tai duoc Log Summary:", err);
    logSummaryBody.replaceChildren();

    const tr = document.createElement("tr");
    const td = document.createElement("td");
    td.colSpan = 5;
    td.className = "muted";
    td.textContent = "Không tải được — xem console.";
    tr.appendChild(td);
    logSummaryBody.appendChild(tr);
  }
}

loadSummary();
loadLogSummary();

// Cap nhat lai moi khi co event moi qua Dashboard - luc do con so co the doi.
window.eventBus.subscribe(() => {
  loadSummary();
  loadLogSummary();
});
