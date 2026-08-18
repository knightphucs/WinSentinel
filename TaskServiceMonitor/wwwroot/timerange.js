"use strict";

/**
 * Bộ lọc "khoảng thời gian" dùng chung cho Dashboard, Cảnh báo, Thông báo và
 * Khôi phục — một chỗ định nghĩa, bốn chỗ dùng, nên nhãn và ý nghĩa không bao giờ
 * lệch nhau giữa các tab.
 *
 * Vì sao cần: khi mất mạng (hoặc app tắt) rồi bật lại, đống event đọc bù đổ vào một
 * lúc. Nhìn danh sách "mới nhất trước" thì không tách được "cái vừa xảy ra" với "cái
 * xảy ra lúc mất kết nối" — phải khoanh được đúng cửa sổ thời gian mới soi kỹ được.
 *
 * ⚠️ Bọc IIFE, chỉ xuất window.createTimeRange (quy ước ghi ở đầu manage.js).
 */
(function () {

/** Preset -> số mili-giây lùi về trước. `custom` đọc từ hai ô nhập tay. */
const PRESETS = {
  "1h": 60 * 60 * 1000,
  "6h": 6 * 60 * 60 * 1000,
  "24h": 24 * 60 * 60 * 1000,
  "7d": 7 * 24 * 60 * 60 * 1000,
  "30d": 30 * 24 * 60 * 60 * 1000,
};

/**
 * `<input type="datetime-local">` trả chuỗi KHÔNG mang múi giờ ("2026-08-18T09:30").
 * `new Date(chuỗi đó)` hiểu là GIỜ MÁY, còn `.toISOString()` đổi sang UTC — đúng cái
 * server cần, vì mọi cột thời gian trong DB đều là UTC. Không được nối thêm "Z" vào
 * chuỗi rồi gửi thẳng: làm vậy là khai giờ Việt Nam thành giờ UTC, lệch đúng 7 tiếng.
 */
function localInputToIso(value) {
  if (!value) return null;

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

/**
 * Nối một bộ điều khiển thời gian vào 3 phần tử theo quy ước id:
 *   #<prefix>-preset  (select)
 *   #<prefix>-from    (datetime-local, chỉ dùng khi preset = "custom")
 *   #<prefix>-to
 *
 * `onChange` được gọi mỗi lần người dùng đổi lựa chọn — bên gọi tự quyết định là
 * lọc lại tại chỗ (client) hay gọi lại API (server).
 *
 * Trả về:
 *   range()   -> { fromIso, toIso } (null = không giới hạn đầu đó)
 *   applyTo(params) -> gắn from/to vào URLSearchParams, bỏ qua nếu null
 *   matches(iso)    -> lọc client-side cho mảng đã có sẵn trong bộ nhớ
 *   label()   -> chữ mô tả để hiện cạnh số đếm
 */
function createTimeRange(prefix, onChange) {
  const preset = document.getElementById(`${prefix}-preset`);
  const from = document.getElementById(`${prefix}-from`);
  const to = document.getElementById(`${prefix}-to`);

  // Cho phép gọi cho panel chưa có markup (vd tab bị gỡ) mà không nổ - trả về bộ
  // điều khiển "không lọc gì" thay vì ném lỗi làm chết cả file đang nạp.
  if (!preset) {
    return {
      range: () => ({ fromIso: null, toIso: null }),
      applyTo: () => {},
      matches: () => true,
      label: () => "",
      set: () => {},
    };
  }

  function syncCustomVisibility() {
    const custom = preset.value === "custom";
    const wrap = document.getElementById(`${prefix}-custom`);
    if (wrap) wrap.hidden = !custom;
  }

  function range() {
    if (preset.value === "custom") {
      return { fromIso: localInputToIso(from?.value), toIso: localInputToIso(to?.value) };
    }

    const span = PRESETS[preset.value];
    if (!span) return { fromIso: null, toIso: null };

    return { fromIso: new Date(Date.now() - span).toISOString(), toIso: null };
  }

  function label() {
    if (preset.value === "custom") {
      const { fromIso, toIso } = range();
      if (!fromIso && !toIso) return "";
      const fmt = (iso) => (iso ? new Date(iso).toLocaleString("vi-VN") : "…");
      return `${fmt(fromIso)} → ${fmt(toIso)}`;
    }

    return preset.value ? preset.options[preset.selectedIndex].textContent : "";
  }

  /**
   * Chiều ngược của localInputToIso: mốc UTC → chuỗi mà `datetime-local` nhận được
   * ("YYYY-MM-DDTHH:mm:ss", giờ máy, KHÔNG có hậu tố Z).
   *
   * Phải tự ghép tay chứ KHÔNG dùng `toISOString().slice(0, 19)`: hàm đó trả giờ UTC,
   * đổ vào ô nhập là hiện sai đúng bằng độ lệch múi giờ (7 tiếng ở Việt Nam).
   */
  function isoToLocalInput(iso) {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return "";

    const pad = (n) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` +
      `T${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
  }

  const control = {
    range,
    label,

    /**
     * Đặt sẵn một khoảng cụ thể (chuyển luôn sang chế độ "Tuỳ chọn"). Dùng cho nút
     * "Dùng khoảng mất kết nối" ở tab Khôi phục.
     *
     * CỐ Ý không tự gọi `onChange`: bên gọi thường muốn đặt xong rồi mới quyết định
     * nạp lại một lần, tránh chạy hai request liền nhau.
     */
    set(fromIso, toIso) {
      preset.value = "custom";
      if (from) from.value = fromIso ? isoToLocalInput(fromIso) : "";
      if (to) to.value = toIso ? isoToLocalInput(toIso) : "";
      syncCustomVisibility();
    },

    applyTo(params) {
      const { fromIso, toIso } = range();
      if (fromIso) params.set("from", fromIso);
      if (toIso) params.set("to", toIso);
      return params;
    },

    matches(iso) {
      if (!iso) return true;

      const { fromIso, toIso } = range();
      const t = new Date(iso).getTime();
      if (fromIso && t < new Date(fromIso).getTime()) return false;
      if (toIso && t > new Date(toIso).getTime()) return false;
      return true;
    },
  };

  function fire() {
    syncCustomVisibility();
    if (onChange) onChange(control);
  }

  preset.addEventListener("change", fire);
  // Hai ô tay nghe 'change' chứ không phải 'input': gõ dở "2026-08-1" đã bắn 'input'
  // rồi, gọi lại API theo từng ký tự vừa tốn vừa nhấp nháy kết quả.
  from?.addEventListener("change", fire);
  to?.addEventListener("change", fire);

  syncCustomVisibility();
  return control;
}

window.createTimeRange = createTimeRange;

})();
