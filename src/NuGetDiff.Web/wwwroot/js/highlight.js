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

window.nuGetDiff.highlightPending = function (selectorOrElement, generation) {
    if (!window.Prism) return;

    let scope = document;
    if (selectorOrElement) {
        const el = (typeof selectorOrElement === 'string')
            ? document.querySelector(selectorOrElement)
            : selectorOrElement;
        if (el) scope = el;
    }

    const marker = String(generation);
    const nodes = [];
    if (scope.matches?.('code[class*="language-"]')) {
        nodes.push(scope);
    }
    nodes.push(...scope.querySelectorAll('code[class*="language-"]'));

    for (const node of nodes) {
        if (node.dataset.nugetdiffHighlightGeneration === marker) continue;
        window.Prism.highlightElement(node);
        node.dataset.nugetdiffHighlightGeneration = marker;
    }
};
