"use strict";

// Vẽ biểu đồ bằng SVG nội tuyến — không thêm thư viện.
//
// Màu lấy từ biến --chart-* (KHÔNG phải --risk-*-bg): mấy biến risk là nền badge
// nhạt (#eaeef2, #fff8c5), vẽ cột chồng lên nhau sẽ gần như vô hình. SVG `fill`
// nhận var() bình thường nên đổi theme là biểu đồ tự đổi.

const SVG_NS = "http://www.w3.org/2000/svg";

function svgEl(name, attrs = {}) {
  const el = document.createElementNS(SVG_NS, name);
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  return el;
}

function svgRoot(width, height, label) {
  const svg = svgEl("svg", {
    viewBox: `0 0 ${width} ${height}`,
    class: "chart",
    role: "img",
    "aria-label": label,
  });
  return svg;
}

// ---------------------------------------------------------------- Tooltip dùng chung
//
// Một <div> duy nhất tái sử dụng cho MỌI biểu đồ trong file này (cột giờ + thanh
// ngang) - tránh mỗi chart tự tạo/quản lý tooltip riêng. position:fixed nên toạ độ
// luôn tính theo clientX/clientY của con trỏ, không phụ thuộc scroll của trang.
//
// Trước đây cột giờ chỉ có <title> SVG (tooltip mặc định của trình duyệt) - có
// nhưng chậm hiện, không style được, và chỉ tóm tắt cả cột chứ không tách rõ
// từng mức rủi ro. Thanh ngang (Risk Distribution/Events by Machine) thì hoàn
// toàn không có phản hồi gì khi hover ngoài đổi con trỏ.

let tooltipEl = null;

function ensureTooltip() {
  if (tooltipEl) return tooltipEl;
  tooltipEl = document.createElement("div");
  tooltipEl.className = "chart-tooltip";
  tooltipEl.setAttribute("role", "tooltip");
  tooltipEl.hidden = true;
  document.body.appendChild(tooltipEl);
  return tooltipEl;
}

/** Một dòng trong tooltip: gạch màu ngắn (khoá theo màu chuỗi dữ liệu) + nhãn mờ + giá trị đậm. */
function tooltipRow(color, label, value) {
  const row = document.createElement("div");
  row.className = "chart-tooltip__row";

  const key = document.createElement("span");
  key.className = "chart-tooltip__key";
  if (color) key.style.background = color;
  else key.style.visibility = "hidden"; // giu thang hang du khong co mau (vd dong "Ty le")

  const lbl = document.createElement("span");
  lbl.className = "chart-tooltip__label";
  lbl.textContent = label; // textContent - nhan co the la ten may/host tu du lieu that

  const val = document.createElement("span");
  val.className = "chart-tooltip__value";
  val.textContent = value;

  row.append(key, lbl, val);
  return row;
}

/**
 * Hiện tooltip với nội dung mới. `rows`: mảng {color, label, value}. `summary`
 * (tuỳ chọn): {label, value} in đậm, ngăn cách bằng đường kẻ - dùng cho "Tổng"/"Tỷ lệ".
 * CHỈ đổ nội dung, KHÔNG định vị - gọi kèm moveTooltip()/anchorTooltip() ngay sau.
 */
function showTooltip({ title, rows, summary }) {
  const el = ensureTooltip();
  el.replaceChildren();

  const titleEl = document.createElement("div");
  titleEl.className = "chart-tooltip__title";
  titleEl.textContent = title;
  el.appendChild(titleEl);

  for (const r of rows) el.appendChild(tooltipRow(r.color, r.label, r.value));

  if (summary) {
    const row = document.createElement("div");
    row.className = "chart-tooltip__summary";
    const lbl = document.createElement("span");
    lbl.textContent = summary.label;
    const val = document.createElement("span");
    val.className = "chart-tooltip__value";
    val.textContent = summary.value;
    row.append(lbl, val);
    el.appendChild(row);
  }

  el.hidden = false;
}

/** Di tooltip theo con trỏ, tự kẹp trong viewport để không tràn ra ngoài mép phải/dưới. */
function moveTooltip(clientX, clientY) {
  const el = ensureTooltip();
  const pad = 12;
  const rect = el.getBoundingClientRect();

  let x = clientX + pad;
  let y = clientY + pad;
  if (x + rect.width > window.innerWidth - 4) x = clientX - rect.width - pad;
  if (y + rect.height > window.innerHeight - 4) y = clientY - rect.height - pad;

  el.style.left = `${Math.max(4, x)}px`;
  el.style.top = `${Math.max(4, y)}px`;
}

/** Dùng khi mở tooltip bằng bàn phím (focus) - không có toạ độ con trỏ để bám theo. */
function anchorTooltip(target) {
  const r = target.getBoundingClientRect();
  moveTooltip(r.left + r.width / 2, r.top);
}

function hideTooltip() {
  if (tooltipEl) tooltipEl.hidden = true;
}

/** Nhãn nhắn cho cả 2 kiểu chart: nghe pointerenter/move/leave lẫn focus/blur (bàn phím). */
function attachTooltip(target, { onShow, highlightClass = "is-hover" } = {}) {
  target.addEventListener("pointerenter", () => {
    target.classList.add(highlightClass);
    showTooltip(onShow());
  });
  target.addEventListener("pointermove", (e) => moveTooltip(e.clientX, e.clientY));
  target.addEventListener("pointerleave", () => {
    target.classList.remove(highlightClass);
    hideTooltip();
  });
  target.addEventListener("focus", () => {
    target.classList.add(highlightClass);
    showTooltip(onShow());
    anchorTooltip(target);
  });
  target.addEventListener("blur", () => {
    target.classList.remove(highlightClass);
    hideTooltip();
  });
}

/** Nhãn trục Y + lưới ngang. Trả về hàm đổi giá trị sang toạ độ y. */
function drawYAxis(svg, { top, bottom, left, right, max }) {
  const steps = 4;
  const scale = (value) => bottom - (value / max) * (bottom - top);

  for (let i = 0; i <= steps; i++) {
    const value = Math.round((max / steps) * i);
    const y = scale(value);

    svg.appendChild(svgEl("line", {
      x1: left, y1: y.toFixed(1), x2: right, y2: y.toFixed(1),
      class: "chart__grid",
    }));

    const text = svgEl("text", {
      x: left - 6, y: (y + 3.5).toFixed(1),
      "text-anchor": "end", class: "chart__label",
    });
    text.textContent = value;
    svg.appendChild(text);
  }

  return scale;
}

/**
 * Cột chồng theo giờ. Mỗi cột là một <g> riêng, tự lo tooltip của chính nó —
 * hover/focus hiện breakdown High/Medium/Low + tổng qua tooltip dùng chung (xem
 * showTooltip ở trên), KHÔNG còn dùng <title> (tooltip mặc định trình duyệt: chậm
 * hiện, không style được, không tách rõ từng mức). aria-label giữ lại nội dung
 * tương đương cho trình đọc màn hình không cần tooltip trực quan.
 */
function buildHourlyChart(buckets) {
  // Ti le ~2:1 cho vua mot card rong 1/3 man hinh. viewBox la don vi TUONG DOI -
  // SVG co width:100% nen chieu cao thuc = be rong card * (height/width).
  const width = 420;
  const height = 200;
  const pad = { top: 10, right: 6, bottom: 22, left: 30 };
  const plotLeft = pad.left;
  const plotRight = width - pad.right;
  const plotTop = pad.top;
  const plotBottom = height - pad.bottom;

  const max = Math.max(1, ...buckets.map((b) => b.high + b.medium + b.low));
  const svg = svgRoot(width, height, "Số event theo từng giờ trong 24 giờ qua");
  const scale = drawYAxis(svg, { top: plotTop, bottom: plotBottom, left: plotLeft, right: plotRight, max });

  const slot = (plotRight - plotLeft) / buckets.length;
  // Chua 60% be rong o -> cot manh, co khe ro giua cac gio.
  const barWidth = Math.max(1.5, slot * 0.6);

  buckets.forEach((b, i) => {
    const slotLeft = plotLeft + i * slot;
    const x = slotLeft + (slot - barWidth) / 2;
    const group = svgEl("g", { class: "chart__col", tabindex: "0", role: "img" });

    // Vung hover phu HET chieu cao cot, ve TRUOC cac thanh du lieu nen nam duoi
    // (khong che mau). Lam hai viec cung luc: nen highlight luc hover/focus, VA
    // hit-target on dinh cho ca gio co it/khong co event - thanh that co the qua
    // thap (vai px) hoac khong ve gi ca de tro chuot vao chinh xac.
    group.appendChild(svgEl("rect", {
      x: slotLeft.toFixed(1), y: plotTop.toFixed(1),
      width: slot.toFixed(1), height: (plotBottom - plotTop).toFixed(1),
      class: "chart__col-hover",
    }));

    let y = plotBottom;
    for (const [count, color] of [
      [b.low, "var(--chart-low)"],
      [b.medium, "var(--chart-medium)"],
      [b.high, "var(--chart-high)"],
    ]) {
      if (count === 0) continue;

      const h = plotBottom - scale(count);
      y -= h;
      group.appendChild(svgEl("rect", {
        x: x.toFixed(1), y: y.toFixed(1),
        width: barWidth.toFixed(1), height: h.toFixed(1),
        fill: color, rx: 1,
      }));
    }

    const hour = new Date(b.hourUtc).getHours();
    const hourLabel = `${String(hour).padStart(2, "0")}:00`;
    const total = b.high + b.medium + b.low;

    group.setAttribute("aria-label",
      `${hourLabel} — High ${b.high}, Medium ${b.medium}, Low ${b.low}, tổng ${total}`);

    attachTooltip(group, {
      onShow: () => ({
        title: hourLabel,
        rows: [
          { color: "var(--chart-high)", label: "High", value: b.high },
          { color: "var(--chart-medium)", label: "Medium", value: b.medium },
          { color: "var(--chart-low)", label: "Low", value: b.low },
        ],
        summary: { label: "Tổng", value: total },
      }),
    });

    svg.appendChild(group);

    if (i % 4 === 0) {
      const text = svgEl("text", {
        x: (x + barWidth / 2).toFixed(1), y: height - 6,
        "text-anchor": "middle", class: "chart__label",
      });
      text.textContent = String(hour).padStart(2, "0") + "h";
      svg.appendChild(text);
    }
  });

  return svg;
}

/** Thanh ngang kèm % — dùng cho Risk Distribution và Events by Machine. */
function buildBarList(rows, { colorFor, total } = {}) {
  const sum = total ?? rows.reduce((acc, r) => acc + r.value, 0);
  const list = document.createElement("div");
  list.className = "barlist";

  if (rows.length === 0 || sum === 0) {
    const empty = document.createElement("p");
    empty.className = "muted";
    empty.textContent = "Chưa có dữ liệu.";
    list.appendChild(empty);
    return list;
  }

  const max = Math.max(...rows.map((r) => r.value));

  for (const row of rows) {
    const item = document.createElement("div");
    item.className = "barlist__item";
    // Dong da co san so lieu dang chu (khong AN gi ca luc chua hover) - tooltip o
    // day la de nhat quan trai nghiem voi cot gio (cung mot ngon ngu tuong tac),
    // khong phai vi thieu du lieu. tabindex de ban phim cung mo duoc, giong cot gio.
    item.tabIndex = 0;
    item.setAttribute("role", "img");

    const label = document.createElement("span");
    label.className = "barlist__label";
    label.textContent = row.label;
    // Khong con can label.title rieng - ten day du (khong bi cat "...") da nam
    // trong tooltip chung cua ca dong (attachTooltip ben duoi), tranh 2 tooltip
    // (native cua trinh duyet + custom) chong len nhau khi hover dung o nhan.

    const track = document.createElement("span");
    track.className = "barlist__track";

    const fill = document.createElement("span");
    fill.className = "barlist__fill";
    const color = colorFor ? colorFor(row) : null;
    // Chieu dai theo gia tri LON NHAT (de so sanh tuong doi de thay), con % thi
    // tinh theo TONG - hai con so khac nhau, khong duoc lan lon.
    fill.style.width = `${(row.value / max) * 100}%`;
    if (color) fill.style.background = color;
    track.appendChild(fill);

    const pct = Math.round((row.value / sum) * 100);
    const value = document.createElement("span");
    value.className = "barlist__value";
    value.textContent = `${row.value.toLocaleString("vi-VN")}  (${pct}%)`;

    item.append(label, track, value);
    item.setAttribute("aria-label", `${row.label} — ${row.value.toLocaleString("vi-VN")} (${pct}%)`);

    attachTooltip(item, {
      onShow: () => ({
        title: row.label,
        rows: [{ color, label: "Số lượng", value: row.value.toLocaleString("vi-VN") }],
        summary: { label: "Tỷ lệ", value: `${pct}%` },
      }),
    });

    list.appendChild(item);
  }

  return list;
}

window.buildHourlyChart = buildHourlyChart;
window.buildBarList = buildBarList;
