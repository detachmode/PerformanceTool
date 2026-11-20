// DRAFT: "/" focuses the scenario filter box (brain 0006), like Aspire. Pure client side, no round trip.
document.addEventListener("keydown", function (e) {
    if (e.key !== "/" || e.ctrlKey || e.metaKey || e.altKey) return;
    var t = e.target;
    if (t && (t.tagName === "INPUT" || t.tagName === "TEXTAREA" || t.tagName === "SELECT" || t.isContentEditable)) return;
    if (document.querySelector("[role=dialog]")) return;
    var box = document.getElementById("dr-filter");
    if (box) { e.preventDefault(); box.focus(); box.select(); }
});
