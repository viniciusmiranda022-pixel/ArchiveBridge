/*
 * ArchiveBridge Portal — JavaScript mínimo e progressivo.
 * Carregado como recurso próprio (script-src 'self'); nenhum handler inline, nenhum framework, nenhuma CDN.
 * A interface funciona sem JS (progressive enhancement); este arquivo apenas adiciona conforto:
 *  - alternância da sidebar em telas estreitas;
 *  - copiar hash/valor para a área de transferência;
 *  - navegação por abas client-side (quando presente);
 *  - proteção anti-duplo-clique em formulários (estado "enviando").
 * NENHUMA lógica de negócio, NENHUMA chamada de rede: só experiência de UI.
 */
(function () {
    "use strict";

    document.addEventListener("DOMContentLoaded", function () {
        setupSidebar();
        setupCopyButtons();
        setupTabs();
        setupSubmitGuards();
    });

    function setupSidebar() {
        var toggle = document.querySelector("[data-nav-toggle]");
        var sidebar = document.querySelector(".sidebar");
        var scrim = document.querySelector(".scrim");
        if (!toggle || !sidebar) { return; }
        function close() { sidebar.classList.remove("open"); if (scrim) { scrim.classList.remove("show"); } }
        toggle.addEventListener("click", function () {
            var open = sidebar.classList.toggle("open");
            if (scrim) { scrim.classList.toggle("show", open); }
        });
        if (scrim) { scrim.addEventListener("click", close); }
        document.addEventListener("keydown", function (e) { if (e.key === "Escape") { close(); } });
    }

    function setupCopyButtons() {
        var buttons = document.querySelectorAll("[data-copy]");
        Array.prototype.forEach.call(buttons, function (btn) {
            btn.addEventListener("click", function () {
                var value = btn.getAttribute("data-copy") || "";
                writeClipboard(value).then(function () {
                    btn.classList.add("copied");
                    var original = btn.getAttribute("aria-label") || "";
                    btn.setAttribute("aria-label", "Copiado");
                    window.setTimeout(function () {
                        btn.classList.remove("copied");
                        if (original) { btn.setAttribute("aria-label", original); }
                    }, 1400);
                });
            });
        });
    }

    function writeClipboard(text) {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            return navigator.clipboard.writeText(text).catch(function () { return fallbackCopy(text); });
        }
        return fallbackCopy(text);
    }

    function fallbackCopy(text) {
        return new Promise(function (resolve) {
            var ta = document.createElement("textarea");
            ta.value = text;
            ta.setAttribute("readonly", "");
            ta.style.position = "absolute";
            ta.style.left = "-9999px";
            document.body.appendChild(ta);
            ta.select();
            try { document.execCommand("copy"); } catch (e) { /* best effort */ }
            document.body.removeChild(ta);
            resolve();
        });
    }

    function setupTabs() {
        var groups = document.querySelectorAll("[data-tabs]");
        Array.prototype.forEach.call(groups, function (group) {
            var tabs = group.querySelectorAll(".tab");
            Array.prototype.forEach.call(tabs, function (tab) {
                tab.addEventListener("click", function (e) {
                    e.preventDefault();
                    var target = tab.getAttribute("data-tab");
                    Array.prototype.forEach.call(tabs, function (t) { t.classList.remove("active"); });
                    tab.classList.add("active");
                    var panels = group.querySelectorAll(".tabpanel");
                    Array.prototype.forEach.call(panels, function (p) {
                        p.classList.toggle("active", p.getAttribute("data-panel") === target);
                    });
                });
            });
        });
    }

    function setupSubmitGuards() {
        var forms = document.querySelectorAll("form[data-guard]");
        Array.prototype.forEach.call(forms, function (form) {
            form.addEventListener("submit", function () {
                var btn = form.querySelector("button[type=submit]");
                if (!btn || btn.disabled) { return; }
                var busy = btn.getAttribute("data-busy");
                if (busy) {
                    // Preserve width so the label swap doesn't reflow; then disable to block a second submit.
                    btn.textContent = busy;
                }
                window.setTimeout(function () { btn.disabled = true; }, 0);
            });
        });
    }
})();
