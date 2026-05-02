// Syntax-highlighting interop. Prism is loaded as plain script tags before
// blazor.webassembly.js, so window.Prism is always defined here.
window.nuGetDiff = window.nuGetDiff || {};

window.nuGetDiff.highlightAll = function (selector) {
    if (!window.Prism) return;
    let scope = document;
    if (selector) {
        const el = (typeof selector === 'string') ? document.querySelector(selector) : selector;
        if (el) scope = el;
    }
    if (window.Prism.highlightAllUnder) {
        window.Prism.highlightAllUnder(scope);
    } else if (window.Prism.highlightAll) {
        window.Prism.highlightAll();
    }
};
