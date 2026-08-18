"use strict";

/**
 * Tab "Khôi phục" — trả lời câu hỏi mà Log Summary bỏ ngỏ.
 *
 * Log Summary chỉ hiện badge "↺ khôi phục N": biết là CÓ đọc bù, nhưng không biết
 * đọc bù được NHỮNG GÌ. Trang này liệt kê đúng N event đó, kèm cửa sổ thời gian app
 * không nhìn thấy gì (mất mạng / bị tắt).
 *
 * Dữ liệu lấy từ GET /api/system/recovered. Cách xác định "event nào là khôi phục"
 * ghi đầy đủ ở ManagementEndpoints.GetRecovered — tóm tắt: mỗi channel có cursor
 * (RecordId lớn nhất đã lưu lúc khởi động) và mốc đích (RecordId mới nhất đang có
 * trong log lúc đó); mọi event nằm giữa hai mốc chính là phần sinh ra trong lúc app
 * tắt. KHÔNG cần thêm cột nào vào DB.
 *
 * Vì hai mốc đó tính lại mỗi lần khởi động, trang này nói về PHIÊN CHẠY HIỆN TẠI.
 *
 * ⚠️ Bọc IIFE, không xuất gì (quy ước ghi ở đầu manage.js).
 */
(function () {

const rc = {
  panel: document.getElementById("panel-recovery"),
  session: document.getElementById("recovery-session"),
  total: document.getElementById("recovery-total"),
  truncated: document.getElementById("recovery-truncated"),
  channelsBody: document.getElementById("recovery-channels-body"),
  body: document.getElementById("recovery-body"),
  empty: document.getElementById("recovery-empty"),
  count: document.getElementById("recovery-count"),
  filterChannel: document.getElementById("recovery-filter-channel"),
  search: document.getElementById("recovery-search"),
  refresh: document.getElementById("recovery-refresh"),
  useDowntime: document.getElementById("recovery-use-downtime"),
  liveNote: document.getElementById("recovery-live-note"),
};

let payload = { sessionStartedUtc: null, totalRecovered: 0, channels: [], events: [] };
let loadedOnce = false;

function formatMoment(iso) {
  return iso ? window.formatTime(iso) : "—";
}

/** "2 giờ 14 phút" — dễ đọc hơn hai mốc thời gian bắt người xem tự trừ. */
function formatSpan(fromIso, toIso) {
  if (!fromIso || !toIso) return "—";

  const ms = new Date(toIso) - new Date(fromIso);
  if (ms <= 0) return "—";

  const minutes = Math.round(ms / 60000);
  if (minutes < 60) return `${minutes} phút`;

  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours < 24) return rest ? `${hours} giờ ${rest} phút` : `${hours} giờ`;

  return `${Math.floor(hours / 24)} ngày ${hours % 24} giờ`;
}

function td(text, className) {
  const cell = document.createElement("td");
  const empty = text === null || text === undefined || text === "";
  cell.textContent = empty ? "—" : text;
  if (empty) cell.className = "muted";
  else if (className) cell.className = className;
  return cell;
}

// ---------------------------------------------------------------- Bảng channel

function renderChannels() {
  rc.channelsBody.replaceChildren();

  for (const channel of payload.channels) {
    const tr = document.createElement("tr");

    tr.appendChild(td(channel.channel));

    const recovered = document.createElement("td");
    if (channel.recovered > 0) {
      const badge = document.createElement("span");
      badge.className = "badge badge--info";
      badge.textContent = `↺ ${channel.recovered}`;
      recovered.appendChild(badge);

      // `caughtUpCount` = watcher ĐẾM ĐƯỢC trong phiên này; `recovered` = số đã kịp
      // ghi xuống CSDL. Đang đọc bù dở thì hai số chênh nhau thật, không phải lỗi —
      // nói ra thay vì giấu một trong hai.
      if (channel.caughtUpCount !== channel.recovered) {
        const note = document.createElement("small");
        note.className = "muted";
        note.style.marginLeft = "0.4rem";
        note.title = "Watcher đã nhận bấy nhiêu event; số ở badge là số đã ghi xong xuống CSDL.";
        note.textContent = `watcher: ${channel.caughtUpCount}`;
        recovered.appendChild(note);
      }
    } else {
      recovered.textContent = "0";
      recovered.className = "muted";
    }
    tr.appendChild(recovered);

    tr.appendChild(td(channel.resumeFromRecordId ?? ""));
    tr.appendChild(td(channel.catchUpTargetRecordId ?? ""));

    // Cua so "app khong nhin thay gi": tu event cuoi cung truoc khi mat ket noi
    // den event dau tien doc bu duoc.
    tr.appendChild(td(
      channel.downtimeFromUtc
        ? `${formatMoment(channel.downtimeFromUtc)} → ${formatMoment(channel.downtimeToUtc)}`
        : "",
    ));
    tr.appendChild(td(formatSpan(channel.downtimeFromUtc, channel.downtimeToUtc)));

    // `note` chi co khi channel KHONG khoi phuc gi - no giai thich vi sao, de dong
    // "0" khong bi hieu nham la loi.
    tr.appendChild(td(channel.note ?? "", "cell--wide"));

    rc.channelsBody.appendChild(tr);
  }

  rc.session.textContent = payload.sessionStartedUtc
    ? formatMoment(payload.sessionStartedUtc)
    : "—";

  // Lấy từ server chứ KHÔNG cộng độ dài mảng `events`: mảng đó đã bị `take` cắt, nên
  // cộng lại sẽ ra đúng bằng trần `take` mỗi khi khôi phục nhiều hơn thế.
  rc.total.textContent = String(payload.totalRecovered ?? 0);

  const shown = payload.channels.reduce((sum, c) => sum + c.shown, 0);
  rc.truncated.hidden = shown >= (payload.totalRecovered ?? 0);
  rc.truncated.textContent =
    `Bảng dưới chỉ hiện ${shown} event mới nhất trong tổng ${payload.totalRecovered}. ` +
    "Cần xem sâu hơn thì dùng bộ lọc thời gian, hoặc xem thẳng ở tab Nhật ký sự kiện.";
}

// ---------------------------------------------------------------- Bảng event

/**
 * Một select, hai nhóm khác hẳn bản chất:
 *   recovered:<channel> — lọc trên danh sách app ĐÃ đọc bù (đã nạp sẵn).
 *   live:<channel>      — channel app KHÔNG theo dõi nên chẳng có gì để đọc bù;
 *                         đọc thẳng từ Windows trong khoảng mất kết nối.
 * Gộp phẳng thành một danh sách sẽ khiến người dùng tưởng hai nhóm cùng nghĩa.
 */
function fillChannelFilter() {
  const current = rc.filterChannel.value;
  const recoveredChannels = [...new Set(payload.events.map((e) => e.channel))].sort();

  rc.filterChannel.replaceChildren();

  const all = document.createElement("option");
  all.value = "";
  all.textContent = "Tất cả (app đã đọc bù)";
  rc.filterChannel.appendChild(all);

  if (recoveredChannels.length > 0) {
    const group = document.createElement("optgroup");
    group.label = "App đã đọc bù";

    for (const channel of recoveredChannels) {
      const option = document.createElement("option");
      option.value = `recovered:${channel}`;
      option.textContent = channel;
      group.appendChild(option);
    }

    rc.filterChannel.appendChild(group);
  }

  if (allChannels.length > 0) {
    const group = document.createElement("optgroup");
    group.label = "Đọc trực tiếp (app không theo dõi channel này)";

    for (const info of allChannels) {
      // Bo channel da co o nhom tren - cung mot ten hien hai lan la kho hieu.
      if (recoveredChannels.includes(info.name)) continue;

      const option = document.createElement("option");
      option.value = `live:${info.name}`;

      // Ten field la `isEnabled`, KHONG phai `enabled` - viet nham mot lan roi:
      // `!undefined` la true nen MOI channel bi danh dau "TAT" va bi disabled, danh
      // sach trong rong ma khong co loi nao. Doi chieu logsbrowse.js neu con nghi ngo.
      const off = !info.isEnabled;
      option.textContent = off ? `${info.name} (TẮT — không ghi event nào)` : info.name;
      option.disabled = off;

      group.appendChild(option);
    }

    rc.filterChannel.appendChild(group);
  }

  // Giu nguyen lua chon cu neu no van con trong danh sach moi.
  if ([...rc.filterChannel.options].some((o) => o.value === current)) {
    rc.filterChannel.value = current;
  }
}

/** `{ mode, channel }` của lựa chọn hiện tại trong ô Log Name. */
function currentSource() {
  const value = rc.filterChannel.value;
  if (!value) return { mode: "recovered", channel: null };

  const at = value.indexOf(":");
  return { mode: value.slice(0, at), channel: value.slice(at + 1) };
}

/**
 * Khoảng "app không nhìn thấy gì" rộng nhất trong các channel có đọc bù — mốc mặc
 * định khi đọc trực tiếp một channel khác. Đọc cả một channel mà không khoanh thời
 * gian thì chỉ ra mấy chục dòng mới nhất, chẳng liên quan gì tới việc mất kết nối.
 */
function downtimeWindow() {
  const starts = payload.channels.map((c) => c.downtimeFromUtc).filter(Boolean).sort();
  if (starts.length === 0 || payload.events.length === 0) return null;

  // Cận trên là event ĐỌC BÙ MỚI NHẤT, KHÔNG phải `downtimeToUtc` của bảng channel —
  // cột đó là event đọc bù ĐẦU TIÊN (mốc mở đầu khoảng mù). Lấy nhầm nó thì cửa sổ
  // co lại còn đúng một khoảnh khắc: bấm nút xong danh sách khôi phục về 0 dòng
  // (đã dính đúng lỗi này khi kiểm thử).
  //
  // `payload.events` server đã sắp giảm dần theo TimeCreated nên phần tử đầu là mới
  // nhất. Cộng thêm 1 giây vì ô `datetime-local` chỉ nhận tới giây — cắt phần mili
  // giây xuống sẽ loại mất chính event ở biên.
  const newest = new Date(payload.events[0].timeCreated).getTime() + 1000;

  return { fromIso: starts[0], toIso: new Date(newest).toISOString() };
}

function passesFilter(evt) {
  const source = currentSource();
  if (source.mode === "recovered" && source.channel && evt.channel !== source.channel) {
    return false;
  }

  const needle = rc.search.value.trim().toLowerCase();
  if (needle && !window.matchesLeafSearch(evt, needle)) return false;

  return timeRange.matches(evt.timeCreated);
}

function buildRow(evt) {
  const tr = document.createElement("tr");
  tr.className = `row--${evt.riskLevel}`;
  tr.addEventListener("click", () => window.openEventDetail(evt));

  const risk = document.createElement("td");
  const badge = document.createElement("span");
  badge.className = `risk risk--${evt.riskLevel}`;
  badge.textContent = evt.riskLevel;
  risk.appendChild(badge);

  tr.appendChild(risk);
  tr.appendChild(td(window.formatTime(evt.timeCreated), "col-time"));
  tr.appendChild(td(evt.recordId));
  tr.appendChild(td(evt.channel));
  tr.appendChild(td(evt.eventId));
  tr.appendChild(td(evt.hostname));
  tr.appendChild(td(evt.objectName, "col-name"));
  tr.appendChild(td(evt.actionDescription));
  tr.appendChild(td(evt.actorAccount));

  return tr;
}

function renderEvents() {
  const source = currentSource();

  if (source.mode === "live") {
    renderLive();
    return;
  }

  rc.liveNote.hidden = true;

  const visible = payload.events.filter(passesFilter);

  rc.body.replaceChildren();
  for (const evt of visible) rc.body.appendChild(buildRow(evt));

  rc.empty.hidden = visible.length > 0;
  rc.empty.textContent = payload.events.length === 0
    ? "Phiên chạy này không có event nào phải đọc bù — app không bỏ lỡ gì."
    : "Không có event nào khớp bộ lọc hiện tại.";

  rc.count.textContent = visible.length === payload.events.length
    ? `${payload.events.length} event`
    : `${visible.length} / ${payload.events.length} event`;
}

/** Kết quả đọc trực tiếp một channel app không theo dõi (LogBrowseEventDto). */
let liveRows = [];
let liveLabel = "";

function renderLive() {
  const needle = rc.search.value.trim().toLowerCase();
  const visible = liveRows.filter((evt) => !needle || window.matchesLeafSearch(evt, needle));

  rc.body.replaceChildren();
  for (const evt of visible) rc.body.appendChild(buildRow(evt));

  rc.empty.hidden = visible.length > 0;
  rc.count.textContent = `${visible.length} event · đọc trực tiếp`;
  rc.liveNote.hidden = false;
  rc.liveNote.textContent = liveLabel;
}

/**
 * Đọc thẳng một channel qua /api/logs/browse trong khoảng thời gian đang chọn.
 *
 * Đây KHÔNG phải dữ liệu app đã bắt được — app không theo dõi channel này nên không
 * có gì trong CSDL, cũng không có cảnh báo nào chấm trên nó. Phải nói rõ điều đó
 * trên màn hình, nếu không người xem sẽ tưởng app đang giám sát cả channel đó.
 */
async function loadLive(channel) {
  const params = new URLSearchParams({ channel, count: "200" });

  // Chua chon khoang nao thi tu lay khoang mat ket noi: doc ca mot channel ma khong
  // khoanh thoi gian chi ra may chuc dong moi nhat, chang lien quan gi toi viec mat
  // ket noi - dung cau hoi ma trang nay dat ra.
  const explicit = timeRange.range();
  const window_ = explicit.fromIso || explicit.toIso ? explicit : downtimeWindow();

  if (window_?.fromIso) params.set("from", window_.fromIso);
  if (window_?.toIso) params.set("to", window_.toIso);

  rc.empty.hidden = false;
  rc.empty.textContent = "Đang đọc trực tiếp từ Windows…";
  rc.body.replaceChildren();

  try {
    const res = await fetch(`/api/logs/browse?${params}`);
    const data = await res.json().catch(() => null);
    if (!res.ok) throw new Error(data?.error ?? `HTTP ${res.status}`);

    liveRows = data;
    liveLabel =
      `Đang ĐỌC TRỰC TIẾP channel "${channel}" — app không theo dõi channel này nên ` +
      `những event dưới đây KHÔNG nằm trong CSDL và KHÔNG được chấm cảnh báo. ` +
      (window_
        ? `Khoảng: ${formatMoment(window_.fromIso)} → ${formatMoment(window_.toIso)}.`
        : "Không giới hạn thời gian (chưa xác định được khoảng mất kết nối).") +
      " Tối đa 200 dòng mới nhất trong khoảng.";
  } catch (err) {
    console.error("Khong doc truc tiep duoc channel:", err);
    liveRows = [];
    liveLabel = `Không đọc được channel "${channel}": ${err.message}`;
  }

  rc.empty.textContent = "Không có event nào trong khoảng này.";
  renderLive();
}

/** Danh sách channel của máy, cho nhóm "Đọc trực tiếp". */
let allChannels = [];

async function loadChannelList() {
  try {
    const res = await fetch("/api/logs/channels");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    allChannels = await res.json();
  } catch (err) {
    console.error("Khong tai duoc danh sach channel:", err);
  }
}

async function load() {
  rc.empty.hidden = false;
  rc.empty.textContent = "Đang tải…";

  try {
    const res = await fetch("/api/system/recovered?take=500");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    payload = await res.json();
    await loadChannelList();
    fillChannelFilter();
    renderChannels();
    renderEvents();
  } catch (err) {
    console.error("Khong tai duoc danh sach event da khoi phuc:", err);
    rc.empty.hidden = false;
    rc.empty.textContent = "Không tải được /api/system/recovered — xem console.";
  }
}

// ---------------------------------------------------------------- Nối dây

const timeRange = window.createTimeRange("recovery-time", () => {
  const source = currentSource();
  // Che do "doc truc tiep" lay du lieu tu server theo khoang -> doi khoang la phai
  // doc lai, khong loc duoc tren cai da co.
  if (source.mode === "live") loadLive(source.channel);
  else renderEvents();
});

rc.refresh.addEventListener("click", () => {
  const source = currentSource();
  if (source.mode === "live") loadLive(source.channel);
  else load();
});

rc.filterChannel.addEventListener("change", () => {
  const source = currentSource();
  if (source.mode === "live") loadLive(source.channel);
  else renderEvents();
});

rc.search.addEventListener("input", renderEvents);

rc.useDowntime.addEventListener("click", () => {
  const window_ = downtimeWindow();
  if (!window_) {
    rc.liveNote.hidden = false;
    rc.liveNote.textContent =
      "Phiên chạy này không có khoảng mất kết nối nào — app không bỏ lỡ event nào.";
    return;
  }

  timeRange.set(window_.fromIso, window_.toIso);

  const source = currentSource();
  if (source.mode === "live") loadLive(source.channel);
  else renderEvents();
});

// Nạp lười như các tab khác; nhưng badge "↺ khôi phục N" ở Log Summary bấm được
// và sẽ mở tab này, nên phải nạp cả khi người dùng vào từ đường đó.
window.onTabShown.subscribe((tab) => {
  if (tab !== "recovery") return;

  // Keo-resize cot: PHAI doi toi luc panel thuc su hien (het hidden) moi goi duoc,
  // doc offsetWidth luc dang hidden se ra 0 - xem ghi chu trong colresize.js.
  // Bang moc theo channel co the dang gap trong <details>, nhung no mac dinh mo nen
  // van do duoc; gap lai roi mo ra khong lam mat be rong da luu.
  makeColumnsResizable(document.getElementById("recovery-channels-table"), "recovery-channels");
  makeColumnsResizable(document.getElementById("recovery-table"), "recovery");

  if (loadedOnce) return;
  loadedOnce = true;
  load();
});

/** Log Summary gọi để mở thẳng trang này và lọc sẵn đúng channel vừa bấm. */
window.showRecovery = (channel) => {
  window.activateTab(document.querySelector('.tab[data-tab="recovery"]'));

  // Gia tri cua o Log Name nay mang tien to che do ("recovered:" / "live:"), khong
  // phai ten channel tran - gan thang ten se khong khop option nao va am tham quay
  // ve "Tat ca".
  const select = () => {
    if (!channel) return;
    const wanted = `recovered:${channel}`;
    if ([...rc.filterChannel.options].some((o) => o.value === wanted)) {
      rc.filterChannel.value = wanted;
    }
  };

  if (!loadedOnce) {
    loadedOnce = true;
    load().then(() => {
      select();
      renderEvents();
    });
    return;
  }

  select();
  renderEvents();
};

})();
