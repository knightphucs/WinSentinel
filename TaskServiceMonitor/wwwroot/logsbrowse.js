"use strict";

// Tab "Duyet log khac" - doc log theo yeu cau qua /api/logs/browse (pull, khong
// realtime, khong luu DB), tach han khoi pipeline curated (xem AdHocLogReader.cs).
// Payload da kem san rawXml nen khung chi tiet khong can loadDetail.

const lb = {
  channel: document.getElementById("logs-browse-channel"),
  eventId: document.getElementById("logs-browse-eventid"),
  count: document.getElementById("logs-browse-count-input"),
  load: document.getElementById("logs-browse-load"),
  pin: document.getElementById("logs-browse-pin"),
  export: document.getElementById("logs-browse-export"),
  body: document.getElementById("logs-browse-body"),
  empty: document.getElementById("logs-browse-empty"),
  countLabel: document.getElementById("logs-browse-count"),
  exportSelected: document.getElementById("logs-browse-export-selected"),
  format: document.getElementById("logs-browse-format"),
  savedBox: document.getElementById("saved-logs"),
  savedList: document.getElementById("saved-logs-list"),
  savedDir: document.getElementById("saved-logs-dir"),
};

// Payload cua lan tai gan nhat - nguon cho ca khung chi tiet lan loc theo cot.
let browseRows = [];

/**
 * Leaf cho co che loc theo cot dung chung voi 3 panel curated (app.js). Thu tu
 * columns PHAI khop voi <thead> trong index.html; `value: null` = cot khong loc duoc.
 */
const browseLeaf = {
  columns: [
    { label: "Rủi ro", value: (e) => e.riskLevel },
    { label: "Level", value: (e) => e.levelDisplayName },
    { label: "Thời gian", value: null },
    { label: "Source", value: (e) => e.providerName },
    { label: "Event ID", value: (e) => String(e.eventId) },
    { label: "Task Category", value: (e) => e.taskCategoryName },
    { label: "Máy", value: (e) => e.hostname },
    { label: "Channel", value: (e) => e.channel },
    { label: "Mô tả", value: null },
    { label: "Người thực hiện", value: (e) => e.actorAccount },
  ],
  filters: {},
  rows: () => browseRows,
  onApply: () => renderBrowseRows(),
};

const browsePane = window.attachDetailPane("logs-browse");

let channelsLoaded = false;

// Nguon dang xem: channel dang chay hay file .evtx da luu.
let currentSource = { kind: "channel", name: null };

async function loadChannelList() {
  if (channelsLoaded) return;
  channelsLoaded = true;

  try {
    const res = await fetch("/api/logs/channels");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const channels = await res.json();

    lb.channel.replaceChildren();
    for (const info of channels) {
      const option = document.createElement("option");
      option.value = info.name;

      // Channel TAT = Windows khong ghi gi vao do, chon vao se ra bang rong. Nhan phai
      // noi luon CACH BAT chu khong chi bao trang thai.
      const bits = [];
      if (!info.isEnabled) bits.push("TẮT — bật bằng wevtutil sl … /e:true");
      if (info.recordCount !== null && info.recordCount !== undefined) {
        bits.push(`${info.recordCount} bản ghi`);
      }
      option.textContent = bits.length ? `${info.name}  (${bits.join(", ")})` : info.name;

      if (!info.isEnabled) {
        option.classList.add("is-disabled-channel");
        option.title =
          `Channel "${info.name}" đang bị Windows tắt nên không có event nào.\n` +
          `Bật bằng PowerShell quyền Administrator:\n` +
          `  wevtutil sl "${info.name}" /e:true`;
      }
      lb.channel.appendChild(option);
    }

    // Uu tien chon san 'Application' - channel pho bien, de bat gap nhat cho
    // nguoi lan dau dung tinh nang nay.
    if (channels.some((c) => c.name === "Application")) {
      lb.channel.value = "Application";
    }
  } catch (err) {
    console.error("Khong tai duoc danh sach channel:", err);
    lb.empty.textContent = "Không tải được danh sách channel — xem console.";
    lb.empty.style.display = "block";
  }
}

function textCell(value) {
  const td = document.createElement("td");
  const empty = value === null || value === undefined || value === "";
  td.textContent = empty ? "—" : value;
  if (empty) td.className = "muted";
  return td;
}

function buildLogRow(evt) {
  const tr = document.createElement("tr");

  // Can cho "luu event dang chon": XPath loc theo EventRecordID.
  if (evt.recordId != null) tr.dataset.recordId = evt.recordId;

  tr.addEventListener("click", (e) => {
    window.selectRow(lb.body, tr, e);
    browsePane.show(evt);
  });

  // riskCell() dung chung voi app.js. RiskLevel o day chi de HIEN THI, khong luu
  // DB/bao SignalR nhu pipeline curated.
  tr.appendChild(riskCell(evt.riskLevel));
  tr.appendChild(textCell(evt.levelDisplayName));
  tr.appendChild(textCell(new Date(evt.timeCreated).toLocaleString("vi-VN")));
  tr.appendChild(textCell(evt.providerName));
  tr.appendChild(textCell(evt.eventId));
  tr.appendChild(textCell(evt.taskCategoryName ?? evt.taskCategoryId));
  tr.appendChild(textCell(evt.hostname));
  tr.appendChild(textCell(evt.channel));

  // Uu tien cau that do provider render; actionDescription chi la switch hardcode,
  // ra "Unknown event 1000" voi moi channel ngoai 15 Event ID curated.
  tr.appendChild(descriptionCell(evt));

  tr.appendChild(textCell(evt.actorAccount));

  return tr;
}

// Rut gon ve mot dong cho vua bang; ban day du nam o khung chi tiet.
function descriptionCell(evt) {
  const text = evt.description ?? evt.actionDescription;
  const oneLine = text ? text.replace(/\s+/g, " ").trim() : null;

  const td = textCell(oneLine);
  if (evt.description) td.title = evt.description; // hover xem ban day du
  return td;
}

async function loadEvents() {
  const eventId = lb.eventId.value.trim();
  const count = lb.count.value.trim();

  if (currentSource.kind === "channel") {
    currentSource = { kind: "channel", name: lb.channel.value };
  }
  if (!currentSource.name) return;

  const params = new URLSearchParams();
  params.set(currentSource.kind === "file" ? "savedFile" : "channel", currentSource.name);
  if (eventId) params.set("eventId", eventId);
  if (count) params.set("count", count);

  lb.load.disabled = true;
  lb.body.replaceChildren();
  browsePane.clear();
  lb.empty.textContent = "Đang tải…";
  lb.empty.style.display = "block";

  try {
    const res = await fetch(`/api/logs/browse?${params.toString()}`);
    const payload = await res.json().catch(() => null);
    if (!res.ok) {
      throw new Error(payload?.error ?? `HTTP ${res.status}`);
    }

    browseRows = payload;

    // Nguon doi = tap gia tri moi -> bo loc cu, khong thi bang co the rong tron.
    browseLeaf.filters = {};
    renderBrowseRows();

    const table = document.getElementById("logs-browse-table");
    // Panel chac chan da hien luc nay - an toan de do offsetWidth. initColumnFilters
    // phai chay TRUOC makeColumnsResizable (xem app.js) de be rong do duoc tinh ca
    // nut loc vua them.
    window.initColumnFilters("logs-browse", browseLeaf);
    makeColumnsResizable(table, "logs-browse");
  } catch (err) {
    lb.empty.textContent = "Lỗi: " + err.message;
    lb.empty.style.display = "block";
  } finally {
    lb.load.disabled = false;
  }
}

function renderBrowseRows() {
  const visible = browseRows.filter((evt) => window.passesColumnFilters(evt, browseLeaf));

  lb.body.replaceChildren();
  for (const evt of visible) {
    lb.body.appendChild(buildLogRow(evt));
  }

  lb.empty.style.display = visible.length === 0 ? "block" : "none";
  lb.empty.textContent = "Không có event nào khớp.";

  const label = currentSource.kind === "file" ? ` (file ${currentSource.name})` : "";
  lb.countLabel.textContent = visible.length === browseRows.length
    ? `${browseRows.length} event${label}`
    : `${visible.length} / ${browseRows.length} event${label}`;
}

lb.load.addEventListener("click", () => {
  // Bam "Tai" = quay ve doc channel dang chon trong dropdown.
  currentSource = { kind: "channel", name: lb.channel.value };
  loadEvents();
});

lb.channel.addEventListener("change", () => {
  currentSource = { kind: "channel", name: lb.channel.value };
});

window.onTabShown.subscribe((tab) => {
  if (tab === "logs-browse") {
    loadChannelList();
    loadSavedLogs();
  }
});

// ---------------------------------------------------------------- Saved Events (.evtx)

// File ghi tren MAY CHAY APP, khong phai may nguoi dung - muon lay ve thi bam
// "Tai ve" trong danh sach ben duoi.
async function exportCurrentChannel() {
  const channel = lb.channel.value;
  if (!channel) return;

  const eventId = lb.eventId.value.trim();

  lb.export.disabled = true;
  try {
    const res = await fetch("/api/logs/export", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        channel,
        eventId: eventId ? Number(eventId) : null,
      }),
    });

    const payload = await res.json().catch(() => null);
    if (!res.ok) throw new Error(payload?.error ?? `HTTP ${res.status}`);

    const kb = Math.round((payload.sizeBytes ?? 0) / 1024);

    // Xem SavedLogStore.Export: file van dung tot tren may nay.
    const note = payload.messagesEmbedded
      ? ""
      : " — không nhúng được mô tả, mở trên máy khác sẽ thiếu Description.";

    showToast(`Đã lưu "${payload.fileName}" (${kb} KB).${note}`, payload.messagesEmbedded);
    loadSavedLogs();
  } catch (err) {
    showToast("Không lưu được log: " + err.message, false);
  } finally {
    lb.export.disabled = false;
  }
}

async function loadSavedLogs() {
  try {
    const res = await fetch("/api/logs/saved");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const { directory, files } = await res.json();

    lb.savedDir.textContent = directory;
    lb.savedList.replaceChildren();
    lb.savedBox.hidden = files.length === 0;

    for (const file of files) {
      lb.savedList.appendChild(buildSavedLogRow(file));
    }
  } catch (err) {
    console.error("Khong tai duoc danh sach file da luu:", err);
  }
}

// logexport.js goi lai sau khi xuat xong, tu bat ky panel nao.
window.refreshSavedLogs = loadSavedLogs;

function buildSavedLogRow(file) {
  const li = document.createElement("li");
  li.className = "saved-logs__item";

  const openBtn = document.createElement("button");
  openBtn.type = "button";
  openBtn.className = "saved-logs__open";
  openBtn.textContent = file.fileName;
  openBtn.title = `Mở file "${file.fileName}"`;
  openBtn.addEventListener("click", () => {
    currentSource = { kind: "file", name: file.fileName };
    loadEvents();
  });

  const size = document.createElement("span");
  size.className = "saved-logs__size";
  size.textContent = `${Math.round(file.sizeBytes / 1024)} KB`;

  const download = document.createElement("a");
  download.className = "btn btn--small";
  download.href = `/api/logs/saved/${encodeURIComponent(file.fileName)}/download`;
  download.textContent = "Tải về";
  download.setAttribute("download", file.fileName);

  const del = document.createElement("button");
  del.type = "button";
  del.className = "btn btn--small";
  del.textContent = "Xoá";
  del.addEventListener("click", async () => {
    if (!confirm(`Xoá file "${file.fileName}"?`)) return;

    try {
      const res = await fetch(`/api/logs/saved/${encodeURIComponent(file.fileName)}`, {
        method: "DELETE",
      });
      const payload = await res.json().catch(() => null);
      if (!res.ok) throw new Error(payload?.error ?? `HTTP ${res.status}`);

      showToast(`Đã xoá "${file.fileName}".`, true);
      loadSavedLogs();
    } catch (err) {
      showToast("Không xoá được: " + err.message, false);
    }
  });

  li.append(openBtn, size, download, del);
  return li;
}

lb.export.addEventListener("click", exportCurrentChannel);

lb.exportSelected.addEventListener("click", () =>
  window.exportSelectedEvents({
    // Doc tu file .evtx thi khong xuat lai .evtx duoc (Windows chi export tu channel)
    // - endpoint se bao loi ro rang, khong doan bua.
    channel: currentSource.kind === "channel" ? currentSource.name : undefined,
    savedFile: currentSource.kind === "file" ? currentSource.name : undefined,
    tbody: lb.body,
    format: lb.format.value,
  }));

// ---------------------------------------------------------------- Ghim channel vao sidebar

const PINNED_KEY = "pinnedChannels";

function readPinnedChannels() {
  try {
    const raw = localStorage.getItem(PINNED_KEY);
    return raw ? JSON.parse(raw) : [];
  } catch {
    return [];
  }
}

function writePinnedChannels(channels) {
  try {
    localStorage.setItem(PINNED_KEY, JSON.stringify(channels));
  } catch {
    // localStorage co the bi chan - bo qua, khong quan trong.
  }
}

// Leaf ghim chi la duong tat: chuyen sang panel "Duyet log khac" roi chon san
// channel, khong phai mot nguon du lieu moi.
function renderPinnedChannels() {
  const container = document.getElementById("pinned-channels");
  if (!container) return;

  container.replaceChildren();

  for (const channel of readPinnedChannels()) {
    const row = document.createElement("div");
    row.className = "pinned-channel";

    const leaf = document.createElement("button");
    leaf.type = "button";
    leaf.className = "tab tab--leaf";
    leaf.dataset.tab = "logs-browse";
    leaf.textContent = channel;
    leaf.title = `Duyệt channel "${channel}"`;

    // Nut sinh SAU khi app.js gan click cho ".tab" nen phai tu goi activateTab().
    leaf.addEventListener("click", async () => {
      window.activateTab(leaf);
      await loadChannelList();
      lb.channel.value = channel;
      currentSource = { kind: "channel", name: channel };
      loadEvents();
    });

    const unpin = document.createElement("button");
    unpin.type = "button";
    unpin.className = "pinned-channel__remove";
    unpin.title = `Bỏ ghim "${channel}"`;
    unpin.textContent = "×";
    unpin.addEventListener("click", (e) => {
      e.stopPropagation();
      writePinnedChannels(readPinnedChannels().filter((c) => c !== channel));
      renderPinnedChannels();
    });

    row.append(leaf, unpin);
    container.appendChild(row);
  }
}

lb.pin.addEventListener("click", () => {
  const channel = lb.channel.value;
  if (!channel) return;

  const pinned = readPinnedChannels();
  if (pinned.includes(channel)) {
    showToast(`Channel "${channel}" đã ghim rồi.`, false);
    return;
  }

  pinned.push(channel);
  writePinnedChannels(pinned);
  renderPinnedChannels();
  showToast(`Đã ghim "${channel}" vào sidebar.`, true);
});

renderPinnedChannels();
