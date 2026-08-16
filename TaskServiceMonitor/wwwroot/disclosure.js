"use strict";

// Xoe/dong noi dung theo kieu disclosure (nut bam + khoi noi dung ngay sau no
// trong DOM) - dung chung cho 2 cho: cay console tree o sidebar
// (.tree-toggle/.tree-children) va cac khoi Overview/Summary o Dashboard
// (.ov-panel__header/.ov-panel__body). Ca hai deu CHI toggle "hidden", khong
// dung toi co che ".tab"/data-tab rieng cua app.js.

function wireDisclosure(toggleSelector, contentClass) {
  for (const toggle of document.querySelectorAll(toggleSelector)) {
    const content = toggle.nextElementSibling;
    if (!content || !content.classList.contains(contentClass)) {
      continue;
    }

    toggle.addEventListener("click", () => {
      const expanded = toggle.getAttribute("aria-expanded") === "true";
      toggle.setAttribute("aria-expanded", String(!expanded));
      content.hidden = expanded;
    });
  }
}

wireDisclosure(".tree-toggle", "tree-children");
wireDisclosure(".ov-panel__header", "ov-panel__body");
