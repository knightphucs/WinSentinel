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
 * Cột chồng theo giờ. Mỗi cột là một <g> riêng để <title> thành tooltip CỦA CỘT ĐÓ —
 * bản trước gắn <title> thẳng vào svg root nên chỉ có một tooltip cho cả biểu đồ.
 */
function buildHourlyChart(buckets) {
  // Ti le ~2:1 cho vua mot card rong 1/3 man hinh. viewBox la don vi TUONG DOI -
  // SVG co width:100% nen chieu cao thuc = be rong card * (height/width).
  const width = 420;
  const height = 200;
  const pad = { top: 10, right: 6, bottom: 22, left: 30 };
  const plotLeft = pad.left;
  const plotRight = width - pad.right;
  const plotBottom = height - pad.bottom;

  const max = Math.max(1, ...buckets.map((b) => b.high + b.medium + b.low));
  const svg = svgRoot(width, height, "Số event theo từng giờ trong 24 giờ qua");
  const scale = drawYAxis(svg, { top: pad.top, bottom: plotBottom, left: plotLeft, right: plotRight, max });

  const slot = (plotRight - plotLeft) / buckets.length;
  // Chua 60% be rong o -> cot manh, co khe ro giua cac gio.
  const barWidth = Math.max(1.5, slot * 0.6);

  buckets.forEach((b, i) => {
    const x = plotLeft + i * slot + (slot - barWidth) / 2;
    const group = svgEl("g");
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
    const title = svgEl("title");
    title.textContent =
      `${String(hour).padStart(2, "0")}h — High ${b.high}, Medium ${b.medium}, Low ${b.low}`;
    group.appendChild(title);
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

    const label = document.createElement("span");
    label.className = "barlist__label";
    label.textContent = row.label;
    label.title = row.label;

    const track = document.createElement("span");
    track.className = "barlist__track";

    const fill = document.createElement("span");
    fill.className = "barlist__fill";
    // Chieu dai theo gia tri LON NHAT (de so sanh tuong doi de thay), con % thi
    // tinh theo TONG - hai con so khac nhau, khong duoc lan lon.
    fill.style.width = `${(row.value / max) * 100}%`;
    if (colorFor) fill.style.background = colorFor(row);
    track.appendChild(fill);

    const value = document.createElement("span");
    value.className = "barlist__value";
    value.textContent = `${row.value.toLocaleString("vi-VN")}  (${Math.round((row.value / sum) * 100)}%)`;

    item.append(label, track, value);
    list.appendChild(item);
  }

  return list;
}

window.buildHourlyChart = buildHourlyChart;
window.buildBarList = buildBarList;
