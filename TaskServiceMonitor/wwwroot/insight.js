"use strict";

// Hai phần phân tích của Dashboard:
//   1. Panel "Phân tích" — mở từ nút "Xem chi tiết" trên card (theo máy / rủi ro cao).
//   2. Ô "Overview" — tình trạng hệ thống + biểu đồ 24 giờ.
//
// Khác nhau ở NGUỒN: panel Phân tích tính từ mảng `events` trên client (≈200 event
// gần nhất, đúng bằng cái mà 3 card đang đếm — nên con số luôn khớp), còn Overview
// query DB thật vì nó trả lời "hệ thống có chạy đúng không", cần số toàn cục.

function insightRow(cells, onClick) {
  const tr = document.createElement("tr");
  if (onClick) tr.addEventListener("click", onClick);

  for (const c of cells) {
    const td = document.createElement("td");
    if (c instanceof Node) td.appendChild(c);
    else td.textContent = c === null || c === undefined || c === "" ? "—" : c;
    tr.appendChild(td);
  }

  return tr;
}

function setInsightHead(labels) {
  const head = document.getElementById("insight-head");
  const tr = document.createElement("tr");

  for (const label of labels) {
    const th = document.createElement("th");
    th.textContent = label;
    tr.appendChild(th);
  }

  head.replaceChildren(tr);
}

function renderInsight(mode) {
  const title = document.getElementById("insight-title");
  const note = document.getElementById("insight-note");
  const body = document.getElementById("insight-body");
  const empty = document.getElementById("insight-empty");
  const count = document.getElementById("insight-count");

  const rows = window.getEvents();
  body.replaceChildren();

  if (mode === "hosts") {
    title.textContent = "Máy đang gửi event";
    note.textContent =
      "Tính trên ~200 event gần nhất đang giữ ở trình duyệt — đúng bằng con số trên card, " +
      "không phải toàn bộ DB. Bấm một dòng để lọc Dashboard theo máy đó.";
    setInsightHead(["Máy", "Tổng", "High", "Medium", "Low", "Event gần nhất"]);

    const byHost = new Map();
    for (const evt of rows) {
      const h = byHost.get(evt.hostname) ?? { total: 0, High: 0, Medium: 0, Low: 0, latest: null };
      h.total++;
      h[evt.riskLevel] = (h[evt.riskLevel] ?? 0) + 1;
      if (!h.latest || new Date(evt.timeCreated) > new Date(h.latest)) h.latest = evt.timeCreated;
      byHost.set(evt.hostname, h);
    }

    const sorted = [...byHost.entries()].sort((a, b) => b[1].total - a[1].total);

    for (const [host, h] of sorted) {
      body.appendChild(insightRow(
        [host, h.total, h.High, h.Medium, h.Low, window.formatTime(h.latest)],
        () => window.focusDashboardHost(host),
      ));
    }

    count.textContent = `${sorted.length} máy`;
    empty.style.display = sorted.length === 0 ? "block" : "none";
    return;
  }

  title.textContent = "Event rủi ro cao hôm nay";
  note.textContent =
    "Event mức High kể từ 0h hôm nay, trong ~200 event gần nhất. Bấm một dòng để xem chi tiết.";
  setInsightHead(["Thời gian", "Máy", "Loại", "Tên", "Hành vi", "Channel"]);

  const startOfDay = new Date();
  startOfDay.setHours(0, 0, 0, 0);

  const high = rows.filter(
    (evt) => evt.riskLevel === "High" && new Date(evt.timeCreated) >= startOfDay);

  for (const evt of high) {
    body.appendChild(insightRow(
      [
        window.formatTime(evt.timeCreated),
        evt.hostname,
        evt.objectType,
        evt.objectName,
        evt.description ?? evt.actionDescription,
        evt.channel,
      ],
      () => window.openEventDetail(evt),
    ));
  }

  count.textContent = `${high.length} event`;
  empty.style.display = high.length === 0 ? "block" : "none";
}

window.renderInsight = renderInsight;

// ---------------------------------------------------------------- Overview

function setText(id, text) {
  const el = document.getElementById(id);
  if (el) el.textContent = text;
}

function healthLine(label, value, kind) {
  const row = document.createElement("div");
  row.className = "meta-row";

  const l = document.createElement("span");
  l.className = "meta-label";
  l.textContent = label;

  const v = document.createElement("span");
  v.className = "meta-value";
  if (kind) {
    const badge = document.createElement("span");
    badge.className = "badge badge--" + kind;
    badge.textContent = value;
    v.appendChild(badge);
  } else {
    v.textContent = value;
  }

  row.append(l, v);
  return row;
}

/** Dòng thời gian "Hoạt động gần đây" — đọc mảng `events` sẵn có, không gọi API. */
function renderTimeline() {
  const box = document.getElementById("chart-timeline");
  if (!box) return;

  const rows = window.getEvents().slice(0, 12);
  box.replaceChildren();

  if (rows.length === 0) {
    const empty = document.createElement("p");
    empty.className = "muted";
    empty.textContent = "Chưa có event nào.";
    box.appendChild(empty);
    return;
  }

  for (const evt of rows) {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "timeline__item";
    item.addEventListener("click", () => window.openEventDetail(evt));

    const time = document.createElement("span");
    time.className = "timeline__time";
    time.textContent = window.formatTime(evt.timeCreated);

    const host = document.createElement("span");
    host.className = "timeline__host";
    host.textContent = evt.hostname;

    const what = document.createElement("span");
    what.className = "timeline__what";
    what.textContent = `${evt.actionDescription}${evt.objectName ? " — " + evt.objectName : ""}`;

    const risk = document.createElement("span");
    risk.className = "risk risk--" + evt.riskLevel;
    risk.textContent = evt.riskLevel;

    item.append(time, host, what, risk);
    box.appendChild(item);
  }
}

async function loadOverview() {
  const box = document.getElementById("overview-body");
  if (!box) return;

  try {
    const res = await fetch("/api/system/overview");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const o = await res.json();

    const ok = o.channels.filter((c) => c.subscribed && !c.error).length;
    const bad = o.channels.length - ok;

    // --- Card lay so lieu TOAN CUC tu DB (khac 2 card kia tinh tren ~200 event) ---
    const receiving = o.channels.filter((c) => c.subscribed && !c.error && c.eventsReceived > 0).length;
    setText("card-total", o.totalEvents.toLocaleString("vi-VN"));
    setText("card-online", `${receiving}/${o.channels.length}`);
    setText("card-online-note", bad > 0 ? `${bad} channel có lỗi` : "tất cả bình thường");

    // --- Bieu do ---
    document.getElementById("chart-hourly")
      ?.replaceChildren(window.buildHourlyChart(o.hourlyBuckets));

    const riskOrder = { High: 0, Medium: 1, Low: 2 };
    const riskRows = [...o.riskDistribution]
      .sort((a, b) => riskOrder[a.riskLevel] - riskOrder[b.riskLevel])
      .map((r) => ({ label: r.riskLevel, value: r.count }));

    document.getElementById("chart-risk")?.replaceChildren(
      window.buildBarList(riskRows, {
        colorFor: (r) => `var(--chart-${r.label.toLowerCase()})`,
      }));

    document.getElementById("chart-hosts")?.replaceChildren(
      window.buildBarList(
        o.byHost.map((h) => ({ label: h.hostname, value: h.total })),
        { colorFor: () => "var(--chart-low)" }));

    renderTimeline();

    const health = document.createElement("div");
    health.className = "ov-health";

    health.append(
      healthLine("Quyền chạy",
        o.isElevated ? `Administrator (${o.currentUser})` : `KHÔNG phải Administrator (${o.currentUser}) — channel Security sẽ không đọc được`,
        o.isElevated ? "ok" : "warn"),
      healthLine("Channel theo dõi",
        bad === 0 ? `${ok}/${o.channels.length} đang nhận log` : `${ok} OK, ${bad} có vấn đề`,
        bad === 0 ? "ok" : "warn"),
      healthLine("Tổng event trong DB", o.totalEvents.toLocaleString("vi-VN")),
      healthLine("Khoảng thời gian phủ",
        o.oldestEventUtc
          ? `${window.formatTime(o.oldestEventUtc)} → ${window.formatTime(o.newestEventUtc)}`
          : "chưa có event nào"),
      healthLine("Rào an toàn ghi", `chỉ tên bắt đầu bằng "${o.writablePrefix}"`),
    );

    const top = document.createElement("ul");
    top.className = "ov-top";
    for (const t of o.topEventIds) {
      const li = document.createElement("li");
      li.textContent = `${t.eventId} — ${t.action} (${t.source}): ${t.count.toLocaleString("vi-VN")}`;
      top.appendChild(li);
    }

    const topTitle = document.createElement("strong");
    topTitle.className = "ov-subtitle";
    topTitle.textContent = "Event ID hay gặp nhất";

    // Bieu do gio da len khoi .charts o tren, o Overview chi con phan tinh trang.
    box.replaceChildren(health, topTitle, top);
  } catch (err) {
    box.textContent = "Không tải được tình trạng hệ thống: " + err.message;
  }
}

loadOverview();

// Overview la truy van DB that (count + group by 24h). Event realtime co the bay
// lien tuc, ve lai moi lan se ban pha DB - chan tan suat 30 giay mot lan.
let lastOverviewLoad = 0;
window.eventBus.subscribe(() => {
  // Timeline doc mang tren client nen ve lai duoc ngay, khong ton gi.
  renderTimeline();

  const now = Date.now();
  if (now - lastOverviewLoad < 30000) return;
  lastOverviewLoad = now;
  loadOverview();
});
