let mermaidPromise;

async function mermaid() {
    mermaidPromise ??= import("/lib/mermaid/mermaid.min.js").then(module => module.default ?? module);
    return mermaidPromise;
}

export async function render(host, source) {
    const engine = await mermaid();
    engine.initialize({ startOnLoad: false, securityLevel: "strict", theme: "neutral" });
    const id = `sql-agent-mermaid-${Math.random().toString(36).slice(2)}`;
    const rendered = await engine.render(id, source);
    host.innerHTML = rendered.svg;
}

export function zoom(host, factor) {
    const svg = host?.querySelector("svg");
    if (!svg) return;
    const current = Number(svg.dataset.sqlAgentScale ?? "1");
    const next = Math.max(0.45, Math.min(2.5, current * factor));
    svg.dataset.sqlAgentScale = String(next);
    svg.style.transform = `scale(${next})`;
    svg.style.transformOrigin = "center top";
}

export function fit(host) {
    const svg = host?.querySelector("svg");
    if (!svg) return;
    delete svg.dataset.sqlAgentScale;
    svg.style.transform = "scale(1)";
}

export function fullscreen(host) {
    const panel = host?.closest(".er-diagram");
    if (!panel) return;
    if (document.fullscreenElement) document.exitFullscreen();
    else panel.requestFullscreen?.();
}

export function download(host, filename) {
    const svg = host?.querySelector("svg");
    if (!svg) return;
    const blob = new Blob([new XMLSerializer().serializeToString(svg)], { type: "image/svg+xml" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = filename;
    link.click();
    URL.revokeObjectURL(url);
}
