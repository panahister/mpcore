"""A small drawing kit for the diagrams in the README files.

Every diagram is drawn twice, for a light and for a dark page, from one description. The files are plain
SVG: no script, no external font, no image. GitHub shows the one that fits the reader's theme.
"""
import os
import re
from html import escape

FONT = "-apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans', Helvetica, Arial, sans-serif"
MONO = "ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, 'Liberation Mono', monospace"

THEMES = {
    "light": {
        "canvas": "#f7f9fc", "frame": "#d8e0ea", "text": "#111827", "muted": "#64748b", "line": "#8fa0b4",
        "shadow": "#0f172a", "shadow_opacity": "0.07",
        "blue":   ("#eef4ff", "#88a9ff", "#2447c5", "#315fe8"),
        "green":  ("#ecfdf8", "#65e2c1", "#0f766e", "#14b8a6"),
        "cyan":  ("#ecfeff", "#67e8f9", "#0e7490", "#06b6d4"),
        "purple": ("#f3f0ff", "#b5a1ff", "#6338c7", "#7c5ce7"),
        "teal":   ("#ecfdf8", "#5eead4", "#0f766e", "#14b8a6"),
        "rose":   ("#fff1f5", "#fda4af", "#be123c", "#f43f5e"),
        "slate":  ("#f1f5f9", "#b8c4d2", "#475569", "#64748b"),
    },
    "dark": {
        "canvas": "#08111f", "frame": "#293a50", "text": "#f3f7fc", "muted": "#9aa9bc", "line": "#66788d",
        "shadow": "#020611", "shadow_opacity": "0.38",
        "blue":   ("#0e1a36", "#315fe8", "#b4c5ff", "#6888ff"),
        "green":  ("#082d2a", "#14b8a6", "#80f2d2", "#2dd4bf"),
        "cyan":  ("#062b38", "#0891b2", "#a5f3fc", "#22d3ee"),
        "purple": ("#1d173a", "#7c5ce7", "#d8ccff", "#a78bfa"),
        "teal":   ("#072d2b", "#14b8a6", "#99f6e4", "#2dd4bf"),
        "rose":   ("#34121d", "#e54867", "#ffb3c1", "#fb7185"),
        "slate":  ("#111c2c", "#40536a", "#d6e0ec", "#8fa1b5"),
    },
}


# Brand icons come from Simple Icons (CC0-1.0), one path on a 24 by 24 grid. Each is drawn in its brand's
# colour; a brand whose colour is black or near it gets a light one on a dark page. The marks belong to
# their owners and are used here only to name the tool.
ICON_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icons")
BRAND = {
    "keycloak": ("#4d4d4d", "#c9d1d9"), "apachekafka": ("#231f20", "#e6edf3"), "rabbitmq": ("#ff6600", "#ff7a1a"),
    "postgresql": ("#4169e1", "#7b96f0"), "redis": ("#ff4438", "#ff5d52"), "timescale": ("#315fe8", "#9fb4ff"),
    "opentelemetry": ("#425cc7", "#9fb4ff"), "jaeger": ("#3ba9c0", "#66cfe3"), "prometheus": ("#e6522c", "#f0683f"),
    "grafana": ("#f46800", "#ff7f1f"), "dotnet": ("#512bd4", "#a08bff"), "docker": ("#2496ed", "#4aa8f0"),
    "nuget": ("#004880", "#5aa9e6"), "githubactions": ("#2088ff", "#4c9dff"), "openid": ("#f78c40", "#f78c40"),
    "claude": ("#d97757", "#e38b6d"), "openai": ("#412991", "#e6edf3"), "kubernetes": ("#326ce5", "#5b8df0"),
    "swagger": ("#5a8a00", "#85ea2d"),
}
# Tools that have no icon in that collection are named by a tile with letters.
TILES = {
    "apisix": ("AX", "#e8433e"), "wso2": ("W2", "#ff7300"), "grpc": ("gR", "#2a9d8f"), "wolverine": ("Wv", "#6b4fbb"),
    "mpcore": ("MP", "#2563eb"), "oidc": ("ID", "#f78c40"), "wiremock": ("WM", "#1e88a8"), "rest": ("{ }", "#0d9488"),
    "people": ("", "#6e7781"),
    "rustfs": ("RF", "#e05d2c"), "seaweedfs": ("SW", "#2f9e44"),
}
_paths = {}


def icon_path(name):
    if name not in _paths:
        with open(os.path.join(ICON_DIR, name + ".svg"), encoding="utf-8") as f:
            _paths[name] = re.search(r'<path d="([^"]+)"', f.read()).group(1)
    return _paths[name]


class Diagram:
    def __init__(self, width, height, theme, title):
        self.w, self.h, self.t, self.title = width, height, THEMES[theme], title
        self.theme = theme
        self.parts = []

    def icon(self, name, x, y, size=24):
        """A brand's icon, or a lettered tile for a tool that has none."""
        if name in TILES:
            letters, color = TILES[name]
            self.rect(x, y, size, size, color, "none", r=size * 0.24)
            if letters:
                self.text(x + size / 2, y + size * 0.66, letters, size=size * 0.42, weight=700, fill="#ffffff", anchor="middle")
            else:
                self.add(f'<circle cx="{x + size/2}" cy="{y + size*0.38}" r="{size*0.17}" fill="#ffffff"/>')
                self.add(f'<path d="M {x + size*0.22} {y + size*0.84} Q {x + size/2} {y + size*0.42} {x + size*0.78} {y + size*0.84} Z" fill="#ffffff"/>')
            return
        color = BRAND[name][0 if self.theme == "light" else 1]
        self.add(f'<g transform="translate({x} {y}) scale({size / 24:.4f})"><path fill="{color}" d="{icon_path(name)}"/></g>')

    def tool(self, x, y, w, h, icon, name, lines=(), status=None, size=26):
        """A tool: its icon, its name, what it is here for, and whether it was run or is only compatible."""
        self.rect(x, y, w, h, self.t["canvas"], self.t["frame"], r=10, sw=1.2, shadow=True)
        self.icon(icon, x + 12, y + (h - size) / 2 if not lines or len(lines) < 2 else y + 13, size)
        tx = x + 12 + size + 10
        ty = y + (24 if lines else h / 2 + 5)
        self.text(tx, ty, name, size=13, weight=700)
        for k, line in enumerate(lines):
            self.text(tx, ty + 17 + k * 15.5, line, size=11.3, fill=self.t["muted"])
        if status:
            self.status(x + w - 15, y + 15, status)

    def tile(self, x, y, w, h, icon, name, caption=None, status=None, size=26):
        """A tool drawn upright: the icon, the name under it, and a word about what it is here for."""
        self.rect(x, y, w, h, self.t["canvas"], self.t["frame"], r=10, sw=1.2, shadow=True)
        self.icon(icon, x + (w - size) / 2, y + 11, size)
        self.text(x + w / 2, y + size + 28, name, size=12, weight=700, anchor="middle")
        if caption:
            self.text(x + w / 2, y + size + 44, caption, size=10.5, fill=self.t["muted"], anchor="middle")
        if status:
            self.status(x + w - 13, y + 13, status)

    def status(self, cx, cy, kind):
        if kind == "run":
            self.add(f'<circle cx="{cx}" cy="{cy}" r="5" fill="{self.accent("green")}"/>')
            self.add(f'<path d="M {cx-2.4} {cy+0.2} L {cx-0.6} {cy+2} L {cx+2.6} {cy-1.8}" fill="none" stroke="#ffffff" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/>')
        else:
            self.add(f'<circle cx="{cx}" cy="{cy}" r="4.3" fill="none" stroke="{self.accent("cyan")}" stroke-width="1.7"/>')

    # ---- colours
    def fill(self, c): return self.t[c][0]
    def stroke(self, c): return self.t[c][1]
    def ink(self, c): return self.t[c][2]
    def accent(self, c): return self.t[c][3]

    # ---- primitives
    def add(self, s):
        self.parts.append(s)

    def rect(self, x, y, w, h, fill, stroke="none", r=10, sw=1.25, dash=None, opacity=None, shadow=False):
        extra = ""
        if dash:
            extra += f' stroke-dasharray="{dash}"'
        if opacity is not None:
            extra += f' opacity="{opacity}"'
        if shadow:
            extra += ' filter="url(#shadow)"'
        self.add(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}"{extra}/>')

    def text(self, x, y, s, size=13, weight=400, fill=None, anchor="start", mono=False, italic=False, spacing=None):
        fill = fill or self.t["text"]
        family = MONO if mono else FONT
        extra = ' font-style="italic"' if italic else ""
        if spacing:
            extra += f' letter-spacing="{spacing}"'
        self.add(f'<text x="{x}" y="{y}" font-family="{family}" font-size="{size}" font-weight="{weight}" '
                 f'fill="{fill}" text-anchor="{anchor}"{extra}>{escape(s)}</text>')

    def line(self, x1, y1, x2, y2, color=None, sw=1.6, dash=None):
        color = color or self.t["line"]
        extra = f' stroke-dasharray="{dash}"' if dash else ""
        self.add(f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{color}" stroke-width="{sw}" stroke-linecap="round"{extra}/>')

    def arrow(self, points, color=None, sw=1.6, dash=None, both=False):
        """A line through the points, with a head at its end (and at its start, if asked)."""
        color = color or self.t["line"]
        path = "M " + " L ".join(f"{x} {y}" for x, y in points)
        extra = f' stroke-dasharray="{dash}"' if dash else ""
        self.add(f'<path d="{path}" fill="none" stroke="{color}" stroke-width="{sw}" stroke-linecap="round" stroke-linejoin="round"{extra}/>')
        self._head(points[-2], points[-1], color)
        if both:
            self._head(points[1], points[0], color)

    def _head(self, a, b, color, size=7):
        import math
        ang = math.atan2(b[1] - a[1], b[0] - a[0])
        p1 = (b[0] - size * math.cos(ang - 0.45), b[1] - size * math.sin(ang - 0.45))
        p2 = (b[0] - size * math.cos(ang + 0.45), b[1] - size * math.sin(ang + 0.45))
        self.add(f'<path d="M {p1[0]:.1f} {p1[1]:.1f} L {b[0]} {b[1]} L {p2[0]:.1f} {p2[1]:.1f}" fill="none" stroke="{color}" '
                 f'stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>')

    # ---- composed shapes
    def card(self, x, y, w, h, color, title, lines=(), kicker=None, title_size=14, line_size=12, mono_lines=False, bar="top"):
        """A box with a coloured bar, a title, and a few lines of muted text."""
        self.rect(x, y, w, h, self.fill(color), self.stroke(color), r=10, shadow=True)
        clip = f"clip{len(self.parts)}"
        self.add(f'<clipPath id="{clip}"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="10"/></clipPath>')
        if bar == "top":
            self.add(f'<rect x="{x}" y="{y}" width="{w}" height="6" fill="{self.accent(color)}" clip-path="url(#{clip})"/>')
            top = y + 7
        else:
            self.add(f'<rect x="{x}" y="{y}" width="6" height="{h}" fill="{self.accent(color)}" clip-path="url(#{clip})"/>')
            top = y
        cy = top + 22
        pad = 14 if bar == "top" else 18
        if kicker:
            self.text(x + pad, cy - 3, kicker.upper(), size=9.5, weight=700, fill=self.accent(color), spacing="0.8")
            cy += 17
        for i, t in enumerate(title if isinstance(title, (list, tuple)) else [title]):
            self.text(x + pad, cy, t, size=title_size, weight=700, fill=self.ink(color))
            cy += title_size + 5
        cy += 3
        for s in lines:
            if s == "":
                cy += 6
                continue
            is_mono = mono_lines or s.startswith("`")
            self.text(x + pad, cy, s.strip("`"), size=line_size - (0.5 if is_mono else 0), fill=self.t["muted"] if not is_mono else self.ink(color), mono=is_mono)
            cy += line_size + 5.5

    def group(self, x, y, w, h, color, label, dash="5 5"):
        """A container with a label sitting on its top edge."""
        self.rect(x, y, w, h, "none", self.stroke(color), r=14, sw=1.4, dash=dash)
        lw = 16 + len(label) * 6.6
        self.rect(x + 18, y - 11, lw, 22, self.t["canvas"], "none", r=6)
        self.rect(x + 18, y - 11, lw, 22, self.fill(color), self.stroke(color), r=11, sw=1.1)
        self.text(x + 18 + lw / 2, y + 4, label, size=11, weight=700, fill=self.ink(color), anchor="middle")

    def chip(self, x, y, label, color, size=11, mono=False, pad=10):
        w = pad * 2 + len(label) * (size * (0.61 if mono else 0.56))
        self.rect(x, y, w, size + 11, self.fill(color), self.stroke(color), r=(size + 11) / 2, sw=1)
        self.text(x + w / 2, y + size + 2.5, label, size=size, weight=600, fill=self.ink(color), anchor="middle", mono=mono)
        return w

    def badge(self, cx, cy, label, color, r=11):
        self.add(f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="{self.accent(color)}"/>')
        fg = "#ffffff" if self.t is THEMES["light"] else "#0d1117"
        self.text(cx, cy + 4, label, size=11.5, weight=700, fill=fg, anchor="middle")

    def heading(self, x, y, title, subtitle=None):
        self.text(x, y, title, size=19, weight=700)
        if subtitle:
            self.text(x, y + 21, subtitle, size=12.5, fill=self.t["muted"])

    # ---- output
    def render(self):
        t = self.t
        head = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {self.w} {self.h}" width="{self.w}" height="{self.h}" '
                f'role="img" aria-label="{escape(self.title)}">\n'
                f'<title>{escape(self.title)}</title>\n'
                f'<defs><filter id="shadow" x="-10%" y="-10%" width="120%" height="130%">'
                f'<feDropShadow dx="0" dy="2" stdDeviation="3" flood-color="{t["shadow"]}" flood-opacity="{t["shadow_opacity"]}"/></filter></defs>\n'
                f'<rect x="0.75" y="0.75" width="{self.w-1.5}" height="{self.h-1.5}" rx="16" fill="{t["canvas"]}" stroke="{t["frame"]}" stroke-width="1.5"/>\n')
        return head + "\n".join(self.parts) + "\n</svg>\n"


def write_both(path_stem, width, height, title, draw):
    """Draws the diagram for both themes: <stem>-light.svg and <stem>-dark.svg."""
    for theme in ("light", "dark"):
        d = Diagram(width, height, theme, title)
        draw(d)
        with open(f"{path_stem}-{theme}.svg", "w", encoding="utf-8") as f:
            f.write(d.render())
