"use strict";

// Gom cac filter it dung (select/checkbox) vao mot popover sau nut "Bo loc",
// chi giu lai o tim kiem + sap xep luon hien ngoai cho gon. Dung chung cho ca
// 3 tab - moi tab la mot ".filter-menu" doc lap, tu quan ly trang thai dong/mo
// va so bo loc dang bat (hien o badge tren nut).

function initFilterMenus() {
  for (const menu of document.querySelectorAll(".filter-menu")) {
    const toggle = menu.querySelector(".filter-toggle");
    const panel = menu.querySelector(".filter-panel");
    const badge = menu.querySelector(".filter-badge");
    if (!toggle || !panel) continue;

    const controls = [...panel.querySelectorAll("select, input")];

    function updateBadge() {
      const active = controls.filter((c) =>
        c.type === "checkbox" ? c.checked : c.value !== "").length;

      badge.textContent = active;
      badge.hidden = active === 0;
    }

    function close() {
      panel.hidden = true;
      toggle.setAttribute("aria-expanded", "false");
    }

    function open() {
      panel.hidden = false;
      toggle.setAttribute("aria-expanded", "true");
    }

    toggle.addEventListener("click", (e) => {
      e.stopPropagation();
      if (panel.hidden) open(); else close();
    });

    // Bam trong panel khong duoc lam dong panel lai.
    panel.addEventListener("click", (e) => e.stopPropagation());

    document.addEventListener("click", close);
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape") close();
    });

    for (const c of controls) {
      c.addEventListener("input", updateBadge);
    }

    updateBadge();
  }
}

window.initFilterMenus = initFilterMenus;

// Cac ".filter-menu" la markup tinh, da co san trong DOM ngay khi file nay
// chay (ke ca cai nam trong panel dang [hidden]) - khoi tao ngay, khong can
// cho tab do duoc mo nhu colresize.js (khong do layout nen khong ngai
// display:none).
initFilterMenus();
