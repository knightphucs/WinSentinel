"use strict";

// Cho phep keo doi rong cot cua bat ky <table> nao trong app (Dashboard, Tasks,
// Services deu dung chung ham nay). Do offsetWidth luc bang DANG HIEN (khong bi
// display:none tu thuoc tinh [hidden]) nen phai goi SAU khi da co du lieu that va
// panel chua tab do da duoc mo it nhat mot lan - goi som hon se do ra be rong 0.
//
// Do rong duoc nho qua localStorage de F5 khong mat lai.

/**
 * `opts.resizeLast` (mac dinh true): cot cuoi cung co gan tay keo hay khong.
 * Chi tat cho bang co cot cuoi la "Thao tac" (tasks/services) - cot do chi
 * chua nut bam, keo hep/rong khong co y nghia. Cac bang khac (Dashboard, Nhat
 * ky su kien, Log Summary...) cot cuoi la DU LIEU that nen phai keo duoc, nhu
 * moi cot khac - truoc day bi khoa nham ca loat theo mac dinh cu (chi tat cho
 * "Thao tac" nhung lai ap dung chung cho moi bang).
 */
function makeColumnsResizable(table, storageKey, opts = {}) {
  if (!table || table.dataset.resizableInit) return;
  table.dataset.resizableInit = "1";

  const resizeLast = opts.resizeLast !== false;
  const ths = [...table.querySelectorAll("thead th")];
  if (ths.length === 0) return;

  const saved = readSavedWidths(storageKey);
  table.style.tableLayout = "fixed";

  ths.forEach((th, i) => {
    const width = saved[i] ?? th.getBoundingClientRect().width;
    th.style.width = `${Math.max(60, Math.round(width))}px`;

    if (i === ths.length - 1 && !resizeLast) return;

    const handle = document.createElement("span");
    handle.className = "col-resizer";
    th.appendChild(handle);

    handle.addEventListener("mousedown", (e) => {
      e.preventDefault();
      const startX = e.clientX;
      const startWidth = th.getBoundingClientRect().width;
      handle.classList.add("is-active");
      document.body.classList.add("is-col-resizing");

      const onMove = (ev) => {
        const next = Math.max(60, startWidth + (ev.clientX - startX));
        th.style.width = `${next}px`;
      };

      const onUp = () => {
        document.removeEventListener("mousemove", onMove);
        document.removeEventListener("mouseup", onUp);
        handle.classList.remove("is-active");
        document.body.classList.remove("is-col-resizing");
        saveWidths(storageKey, ths);
      };

      document.addEventListener("mousemove", onMove);
      document.addEventListener("mouseup", onUp);
    });
  });
}

function readSavedWidths(storageKey) {
  try {
    const raw = localStorage.getItem("colwidths:" + storageKey);
    return raw ? JSON.parse(raw) : {};
  } catch {
    return {};
  }
}

function saveWidths(storageKey, ths) {
  try {
    const widths = {};
    ths.forEach((th, i) => { widths[i] = Math.round(th.getBoundingClientRect().width); });
    localStorage.setItem("colwidths:" + storageKey, JSON.stringify(widths));
  } catch {
    // localStorage co the bi chan (che do duyet web rieng tu) - bo qua, khong quan trong.
  }
}

window.makeColumnsResizable = makeColumnsResizable;
