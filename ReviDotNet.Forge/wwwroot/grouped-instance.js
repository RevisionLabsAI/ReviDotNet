// Expand / collapse every <details> inside the grouped instance view. Native <details> keeps the
// browser owning open/close state; this just drives the "Expand all" / "Collapse all" buttons.
window.groupedSetAllOpen = function (rootSelector, open) {
    document.querySelectorAll(rootSelector + ' details').forEach(function (d) { d.open = open; });
};
