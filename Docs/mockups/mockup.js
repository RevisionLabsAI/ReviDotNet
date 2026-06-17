/* Shared behaviour for every Forge agent-instance mockup.
   Collapsing/expanding is handled natively by <details>/<summary> — this only
   adds the theme toggle, expand/collapse-all, and the Inspector filter chips. */
(function () {
  function setTheme(t) {
    document.documentElement.setAttribute("data-theme", t);
    try { localStorage.setItem("forge-mock-theme", t); } catch (e) {}
    document.querySelectorAll("[data-theme-label]").forEach(function (el) {
      el.textContent = t === "dark" ? "Light" : "Dark";
    });
  }
  window.toggleTheme = function () {
    var cur = document.documentElement.getAttribute("data-theme") || "light";
    setTheme(cur === "dark" ? "light" : "dark");
  };
  // Restore saved theme (falls back to the light default in the markup).
  try {
    var saved = localStorage.getItem("forge-mock-theme");
    if (saved) setTheme(saved);
    else document.querySelectorAll("[data-theme-label]").forEach(function (el) { el.textContent = "Dark"; });
  } catch (e) {}

  // Expand / collapse every collapsible event on the page.
  // Variations mark events as either .ev (01–03) or .card (04 Studio).
  window.setAllOpen = function (open) {
    document.querySelectorAll("details.ev, details.card").forEach(function (d) { d.open = open; });
  };

  // Inspector-only: filter the trace by event type via the toolbar chips.
  window.filterTrace = function (type, btn) {
    document.querySelectorAll("[data-filter]").forEach(function (b) { b.classList.remove("on"); });
    if (btn) btn.classList.add("on");
    document.querySelectorAll("[data-type]").forEach(function (row) {
      var t = row.getAttribute("data-type");
      var show = type === "all"
        || t === type
        || (type === "error" && row.getAttribute("data-status") === "fail");
      row.style.display = show ? "" : "none";
    });
  };
})();
