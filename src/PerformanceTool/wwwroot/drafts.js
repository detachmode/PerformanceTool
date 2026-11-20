// DRAFT: "/" focuses the scenario filter box (brain 0006), like Aspire. Pure client side, no round trip.
document.addEventListener("keydown", function (e) {
    if (e.key !== "/" || e.ctrlKey || e.metaKey || e.altKey) return;
    var t = e.target;
    if (t && (t.tagName === "INPUT" || t.tagName === "TEXTAREA" || t.tagName === "SELECT" || t.isContentEditable)) return;
    if (document.querySelector("[role=dialog]")) return;
    var box = document.getElementById("dr-filter");
    if (box) { e.preventDefault(); box.focus(); box.select(); }
});

// DRAFT editor: focus an element by id after Blazor rendered (keyboard navigation, jump from the problem summary).
window.drFocus = function (id) {
    var el = document.getElementById(id);
    if (!el) return;
    el.focus();
    if (el.scrollIntoView) el.scrollIntoView({ block: "nearest" });
    if (el.tagName === "INPUT" && el.type !== "range" && el.select) { try { el.select(); } catch (e) { } }
};
// Up/Down in the scenario list move the selection, not the page.
document.addEventListener("keydown", function (e) {
    if ((e.key === "ArrowUp" || e.key === "ArrowDown" || e.key === "Home" || e.key === "End") && e.target && e.target.closest && e.target.closest(".dr-rows")) e.preventDefault();
});

// DRAFT calls view (brain 0013): hovering a call (a segment of a bar or a row of the per-call list) highlights the same call in
// every bar. Pure client side: classes are set on the .cv section, no round trip.
(function () {
    var root = null, hot = [];
    function clear() {
        hot.forEach(function (el) { el.classList.remove("hl"); });
        hot = [];
        document.querySelectorAll(".cv.hl-on").forEach(function (el) { el.classList.remove("hl-on"); });
        root = null;
    }
    document.addEventListener("mouseover", function (e) {
        var t = e.target && e.target.closest ? e.target.closest(".cv [data-call]") : null;
        if (!t) { if (root) clear(); return; }
        var idx = t.getAttribute("data-call");
        if (root && hot.length && hot[0].getAttribute("data-call") === idx) return;
        clear();
        var dlg = t.closest(".dialog") || document;
        hot = Array.prototype.slice.call(dlg.querySelectorAll('.cv-chart [data-call="' + idx + '"], .call-table [data-call="' + idx + '"]'));
        hot.forEach(function (el) { el.classList.add("hl"); });
        dlg.querySelectorAll(".cv").forEach(function (el) { el.classList.add("hl-on"); });
        root = dlg;
    });
})();
