"use strict";

// Nut doi sang/toi o header. Uu tien lua chon tay (luu localStorage) hon he
// thong; chua chon lan nao thi theo prefers-color-scheme cua he dieu hanh
// (xu ly boi CSS, xem style.css). Script chan FOUC nam rieng trong <head> vi
// phai chay TRUOC khi CSS ve - file nay chi lo phan nut bam.

function effectiveTheme() {
  const explicit = document.documentElement.getAttribute("data-theme");
  if (explicit === "light" || explicit === "dark") return explicit;
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function updateToggleLabel() {
  const btn = document.getElementById("theme-toggle");
  if (btn) btn.textContent = effectiveTheme() === "dark" ? "Giao diện: Tối" : "Giao diện: Sáng";
}

function applyTheme(theme) {
  document.documentElement.setAttribute("data-theme", theme);
  try { localStorage.setItem("theme", theme); } catch { /* bo qua neu bi chan */ }
  updateToggleLabel();
}

document.getElementById("theme-toggle")?.addEventListener("click", () => {
  applyTheme(effectiveTheme() === "dark" ? "light" : "dark");
});

updateToggleLabel();
