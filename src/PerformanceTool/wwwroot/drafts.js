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
