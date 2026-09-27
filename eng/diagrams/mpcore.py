#!/usr/bin/env python3
"""Draws the diagrams of MP Core's README into docs/images. Run from the repository root:

    python3 eng/diagrams/mpcore.py
"""
import os, sys
sys.path.insert(0, os.path.dirname(__file__))
from draw import write_both

OUT = "docs/images"
os.makedirs(OUT, exist_ok=True)


def request(d):
    d.heading(32, 44, "One request, from the caller to the broker",
              "You write the green box. MP Core is everything around it, the same in every backend.")
    y, h, w, gap, x0 = 92, 196, 138, 14, 32
    steps = [
        ("blue", "1", "Transport", ["REST and gRPC", "on separate ports", "", "Problem Details", "(RFC 9457)", "Rich gRPC status"]),
        ("rose", "2", "Security", ["Bearer token", "validated", "", "Deny by default", "", "A named actor", "in every record"]),
        ("teal", "3", "Validation", ["The shape of the", "request, checked", "before the handler", "", "One violation", "per field"]),
        ("green", "4", "Your handler", ["A static method", "", "Aggregate", "Business rules", "Value objects", "", "It never saves"]),
        ("blue", "5", "One commit", ["The change", "The messages", "The audit record", "The idempotency key", "", "All, or none"]),
        ("amber", "6", "Messages", ["Released after", "the commit", "", "Kafka, RabbitMQ", "", "Inbox on the", "consumer"]),
    ]
    for i, (color, n, title, lines) in enumerate(steps):
        x = x0 + i * (w + gap)
        d.card(x, y, w, h, color, title, lines, kicker=("you write" if color == "green" else "mp core"))
        d.badge(x + w - 20, y + 26, n, color)
        if i:
            d.arrow([(x - gap + 1, y + h / 2), (x - 2, y + h / 2)], color=d.t["line"], sw=1.8)
    # the two things that are true at every step
    by = y + h + 26
    d.rect(x0, by, 440, 58, d.fill("purple"), d.stroke("purple"), r=10, shadow=True)
    d.text(x0 + 16, by + 24, "A failure is a value", size=13.5, weight=700, fill=d.ink("purple"))
    d.text(x0 + 16, by + 43, "One model on both transports, in the caller's language", size=12, fill=d.t["muted"])
    d.rect(x0 + 456, by, 440, 58, d.fill("slate"), d.stroke("slate"), r=10, shadow=True)
    d.text(x0 + 472, by + 24, "Everything is observable", size=13.5, weight=700, fill=d.ink("slate"))
    d.text(x0 + 472, by + 43, "OpenTelemetry logs, traces and metrics, with redaction", size=12, fill=d.t["muted"])


def layers(d):
    d.heading(32, 44, "Where your code lives, and what surrounds it",
              "Dependencies point inward. The domain knows nothing of a database, a broker or HTTP.")
    rows = [
        ("slate", "API host", "composition and transport",
         ["Endpoints, gRPC services", "Policies: who may", "Routes and listeners"],
         ["`MPCore.Hosting`", "`MPCore.Transport.Http  .Grpc`", "`MPCore.Security.AspNetCore`", "`MPCore.Observability`"]),
        ("teal", "Application", "use cases",
         ["Commands and queries, one per file", "Validators, ports, process managers", "Reactions to events"],
         ["`MPCore.Application`", "`MPCore.Validation.FluentValidation`", "`MPCore.Messaging.Abstractions`", "`MPCore.Persistence.Abstractions`"]),
        ("green", "Domain", "the business",
         ["Aggregates and entities", "Value objects", "Business rules with a name", "Domain and integration events"],
         ["`MPCore.Domain`", "", "Nothing else. No provider,", "no framework, no attribute."]),
        ("amber", "Infrastructure", "adapters",
         ["Mappings and migrations", "Repositories, read models", "Gateways to other systems"],
         ["`MPCore.Persistence.EntityFrameworkCore`", "`MPCore.Messaging.Wolverine  .Kafka  .RabbitMQ`", "`MPCore.Caching.*   MPCore.Audit.*`", "`MPCore.Idempotency   MPCore.Resilience.Http`"]),
    ]
    x0, y0, rh, gap = 32, 96, 104, 12
    d.text(x0 + 200, y0 - 8, "YOU WRITE", size=10, weight=700, fill=d.accent("green"), spacing="1")
    d.text(x0 + 548, y0 - 8, "MP CORE PROVIDES", size=10, weight=700, fill=d.accent("blue"), spacing="1")
    for i, (color, name, sub, yours, core) in enumerate(rows):
        y = y0 + i * (rh + gap)
        d.rect(x0, y, 896, rh, d.fill(color), d.stroke(color), r=12, shadow=True)
        clip = f"row{i}"
        d.add(f'<clipPath id="{clip}"><rect x="{x0}" y="{y}" width="896" height="{rh}" rx="12"/></clipPath>')
        d.add(f'<rect x="{x0}" y="{y}" width="7" height="{rh}" fill="{d.accent(color)}" clip-path="url(#{clip})"/>')
        d.text(x0 + 26, y + 44, name, size=17, weight=700, fill=d.ink(color))
        d.text(x0 + 26, y + 64, sub, size=12, fill=d.t["muted"])
        for k, s in enumerate(yours):
            d.text(x0 + 200, y + 28 + k * 20, s, size=12.5)
        d.line(x0 + 528, y + 16, x0 + 528, y + rh - 16, color=d.stroke(color), sw=1, dash="3 4")
        for k, s in enumerate(core):
            mono = s.startswith("`")
            d.text(x0 + 548, y + 27 + k * 19.5, s.strip("`"), size=11.5 if mono else 12, mono=mono,
                   fill=d.ink("blue") if mono else d.t["muted"])
    # the inward arrows on the left
    ax = x0 + 60
    d.arrow([(ax, y0 + rh - 14), (ax, y0 + rh + gap + 14)], color=d.t["line"])
    d.arrow([(ax, y0 + 2 * rh + gap - 14), (ax, y0 + 2 * (rh + gap) + 14)], color=d.t["line"])
    d.arrow([(ax, y0 + 3 * (rh + gap) + 14), (ax, y0 + 3 * rh + 2 * gap - 14)], color=d.t["line"])


def ddd(d):
    d.heading(32, 44, "Domain-Driven Design, and the type that carries each idea",
              "MP Core does not model your business. It gives every building block a place and a guarantee.")
    # strategic
    d.group(32, 96, 896, 128, "purple", "STRATEGIC DESIGN")
    strategic = [
        ("Bounded context", ["A module of a modular", "monolith, or a service"], "--shape"),
        ("Context boundary", ["One project per module:", "the compiler guards it"], "Contracts project"),
        ("Integration", ["A message that commits", "with the change"], "outbox · inbox"),
        ("Ubiquitous language", ["Rules and failures carry", "the business's own names"], "error domain · code"),
    ]
    for i, (title, lines, chip) in enumerate(strategic):
        x = 48 + i * 218
        d.text(x + 6, 130, title, size=13.5, weight=700, fill=d.ink("purple"))
        for k, s in enumerate(lines):
            d.text(x + 6, 150 + k * 17, s, size=12, fill=d.t["muted"])
        d.chip(x + 6, 190, chip, "purple", size=10.5, mono=True, pad=9)
    # tactical
    d.group(32, 254, 896, 300, "green", "TACTICAL DESIGN")
    tactical = [
        ("Aggregate", "AggregateRoot<TId>", "One per transaction; raises events"),
        ("Entity", "Entity<TId>", "Identity, and CheckRule"),
        ("Value object", "ValueObject", "Equal by value; always valid"),
        ("Business rule", "BusinessRule", "A named class, reported by its code"),
        ("Domain event", "IDomainEvent", "Handled after the commit"),
        ("Integration event", "IntegrationEvent", "Leaves through the outbox"),
        ("Repository", "IRepository<T, TId>", "A port; the adapter is yours"),
        ("Unit of work", "IUnitOfWork", "Declared by a handler, never called"),
        ("Application service", "ICommand<T> · IQuery<T>", "A handler: a static method"),
    ]
    for i, (concept, type_, note) in enumerate(tactical):
        col, row = i % 3, i // 3
        x, y = 48 + col * 292, 280 + row * 88
        d.rect(x, y, 280, 74, d.fill("green"), d.stroke("green"), r=10, shadow=True)
        d.text(x + 14, y + 24, concept, size=13.5, weight=700, fill=d.ink("green"))
        d.text(x + 14, y + 44, type_, size=11.5, mono=True, fill=d.ink("blue"))
        d.text(x + 14, y + 62, note, size=11.5, fill=d.t["muted"])


def skills(d):
    d.heading(32, 44, "Ten skills for AI coding agents, generated with every backend",
              "A skill is a procedure, not a prompt: what to establish, what to ask, and what not to decide alone.")
    phases = [
        ("purple", "Plan", ["plan-bounded-context"]),
        ("green", "Build", ["implement-ddd-module", "implement-vertical-slice", "design-transport-contract"]),
        ("rose", "Protect and operate", ["apply-security", "apply-business-audit", "apply-observability"]),
        ("amber", "Connect", ["configure-messaging", "integrate-contexts"]),
        ("teal", "Prove", ["verify-business-behavior"]),
    ]
    widths = [164, 186, 170, 164, 196]
    x = 32
    y, h = 96, 150
    for i, ((color, name, items), w) in enumerate(zip(phases, widths)):
        d.card(x, y, w, h, color, name, [], kicker=f"step {i + 1}")
        for k, item in enumerate(items):
            d.rect(x + 12, y + 66 + k * 27, w - 24, 22, d.t["canvas"], d.stroke(color), r=6, sw=1)
            d.text(x + 20, y + 81 + k * 27, item, size=10, mono=True, fill=d.ink(color))
        x += w + 4
    # one body, two adapters
    by = 286
    d.rect(32, by, 300, 112, d.fill("blue"), d.stroke("blue"), r=12, shadow=True)
    d.text(50, by + 28, "One body per skill", size=14, weight=700, fill=d.ink("blue"))
    d.text(50, by + 50, ".mpcore/skills/<name>/SKILL.md", size=11.5, mono=True, fill=d.ink("blue"))
    d.text(50, by + 74, "Written once, for the options you chose:", size=12, fill=d.t["muted"])
    d.text(50, by + 92, "shape, transport, messaging.", size=12, fill=d.t["muted"])
    for k, (name, files, color, mark) in enumerate([
        ("Claude Code", ["CLAUDE.md", ".claude/skills/"], "amber", "claude"),
        ("Codex", ["AGENTS.md", ".agents/skills/"], "teal", "openai"),
    ]):
        yy = by + k * 60
        d.rect(400, yy, 250, 52, d.fill(color), d.stroke(color), r=10, shadow=True)
        d.icon(mark, 414, yy + 14, 24)
        d.text(448, yy + 22, name, size=13.5, weight=700, fill=d.ink(color))
        d.text(448, yy + 40, "  ".join(files), size=10.5, mono=True, fill=d.t["muted"])
        d.arrow([(332, by + 56), (366, by + 56), (366, yy + 26), (398, yy + 26)], sw=1.5)
    d.rect(700, by, 228, 112, d.fill("slate"), d.stroke("slate"), r=12, shadow=True)
    d.text(716, by + 26, "What every skill holds to", size=13, weight=700, fill=d.ink("slate"))
    for k, s in enumerate(["Reads the manifest first", "Never invents a business rule", "Never weakens a security default", "Reports the real test output"]):
        d.add(f'<circle cx="721" cy="{by + 45 + k * 18}" r="2.6" fill="{d.accent("green")}"/>')
        d.text(732, by + 49 + k * 18, s, size=11.5, fill=d.t["muted"])


def reference(d):
    d.heading(32, 44, "A backend platform, and where MP Core sits in it",
              "Every box is a product you can run today. The Storefront sample runs the ones with a green mark.")
    L, LW, R, RW = 32, 676, 728, 240

    # ---- who calls
    callers = [("people", "Web and mobile apps"), ("people", "Partner systems"), ("people", "Staff and back office")]
    for i, (ic, name) in enumerate(callers):
        x = L + i * 228
        d.rect(x, 84, 220, 44, d.fill("slate"), d.stroke("slate"), r=22, shadow=True)
        d.icon(ic, x + 14, 94, 24)
        d.text(x + 48, 111, name, size=12.5, weight=600, fill=d.ink("slate"))
    d.arrow([(L + LW / 2, 130), (L + LW / 2, 158)], sw=1.8)
    d.text(L + LW / 2 + 10, 149, "HTTPS  ·  gRPC over TLS", size=10.5, fill=d.t["muted"])

    # ---- edge
    d.group(L, 170, LW, 104, "rose", "EDGE  ·  API GATEWAY")
    d.tool(L + 16, 192, 316, 68, "apisix", "Apache APISIX", ["Ends TLS, routes REST and gRPC,", "request identity, rate limits"], "run")
    d.tool(L + 344, 192, 316, 68, "wso2", "WSO2 API Manager", ["Any gateway that forwards the", "token and X-Forwarded-* headers"], "fit")
    d.arrow([(L + LW / 2, 276), (L + LW / 2, 304)], sw=1.8)
    d.text(L + LW / 2 + 10, 295, "cleartext inside  ·  the bearer token passes through", size=10.5, fill=d.t["muted"])

    # ---- backends
    d.group(L, 316, LW, 292, "blue", "YOUR BACKENDS  ·  .NET 10")
    shapes = [
        ("green", "Modular monolith", ["Several bounded contexts,", "one deployment"], ["Catalog", "Basket", "Ordering", "Payments"]),
        ("green", "Service", ["One bounded context,", "gRPC"], ["Shipments"]),
        ("green", "Service", ["One bounded context,", "REST"], ["Sales figures"]),
    ]
    widths, x = [272, 180, 180], L + 16
    for i, (color, name, lines, modules) in enumerate(shapes):
        d.card(x, 340, widths[i], 124, color, name, lines, kicker="your business")
        cx = x + 12
        cy = 340 + 96
        for m in modules:
            wchip = d.chip(cx, cy, m, "green", size=9.5, pad=6)
            cx += wchip + 4
        x += widths[i] + 6
    # the framework under them
    d.rect(L + 16, 476, LW - 32, 118, d.fill("blue"), d.stroke("blue"), r=12, shadow=True)
    d.icon("mpcore", L + 30, 490, 30)
    d.text(L + 70, 503, "MP Core", size=15, weight=700, fill=d.ink("blue"))
    d.text(L + 70, 520, "The same decisions in every backend, made once and tested", size=11.3, fill=d.t["muted"])
    d.icon("wolverine", L + 452, 490, 30)
    d.text(L + 490, 503, "Wolverine", size=13, weight=700, fill=d.ink("blue"))
    d.text(L + 490, 520, "Handlers, durable queues", size=11.3, fill=d.t["muted"])
    chips = ["REST + gRPC", "Token validation", "Input validation", "One commit + outbox", "Idempotency + inbox",
             "Business audit", "Failures in the caller's language", "Caching", "Resilient HTTP", "Telemetry"]
    cx, cy = L + 30, 536
    for c in chips:
        wchip = 18 + len(c) * 5.9
        if cx + wchip > L + LW - 28:
            cx, cy = L + 30, cy + 27
        d.chip(cx, cy, c, "blue", size=10.5, pad=9)
        cx += wchip + 6

    # ---- messaging
    d.arrow([(L + 170, 610), (L + 170, 638)], sw=1.8, both=True)
    d.arrow([(L + 506, 610), (L + 506, 638)], sw=1.8, both=True)
    d.group(L, 650, 332, 104, "amber", "MESSAGING")
    d.tile(L + 12, 670, 150, 74, "apachekafka", "Apache Kafka", "events, for many readers", "run", size=24)
    d.tile(L + 170, 670, 150, 74, "rabbitmq", "RabbitMQ", "work, for one reader", "run", size=24)

    # ---- data
    d.group(L + 344, 650, 332, 104, "teal", "DATA")
    d.tile(L + 356, 670, 100, 74, "postgresql", "PostgreSQL", "relational", "run", size=24)
    d.tile(L + 462, 670, 100, 74, "timescale", "TimescaleDB", "time series", "run", size=24)
    d.tile(L + 568, 670, 96, 74, "redis", "Redis", "cache", "run", size=24)

    # ---- identity
    d.group(R, 170, RW, 172, "purple", "IDENTITY")
    d.tool(R + 12, 192, RW - 24, 64, "keycloak", "Keycloak", ["Roles, service accounts"], "run")
    d.tool(R + 12, 264, RW - 24, 64, "openid", "OpenID Connect", ["WSO2 IS, Entra ID, others"], "fit")
    d.arrow([(L + LW + 2, 400), (R + 40, 400), (R + 40, 346)], sw=1.5, dash="5 4")
    d.text(R + 50, 394, "validates every token", size=10.5, fill=d.t["muted"])

    # ---- observability
    d.group(R, 430, RW, 178, "slate", "OBSERVABILITY")
    d.tool(R + 12, 452, RW - 24, 48, "opentelemetry", "OpenTelemetry", [], "run", size=24)
    d.text(R + 50 + 0, 452 + 40, "", size=1)
    for k, (ic, name) in enumerate([("jaeger", "Jaeger"), ("prometheus", "Prometheus"), ("grafana", "Grafana")]):
        d.tile(R + 12 + k * 74, 510, 68, 84, ic, name, None, "run", size=24)
    d.arrow([(L + LW + 2, 476), (R - 2, 476)], sw=1.5, dash="5 4")
    d.text(L + LW + 12, 466, "OTLP", size=10, fill=d.t["muted"])

    # ---- delivery
    d.group(R, 650, RW, 104, "green", "DELIVERY")
    for k, (ic, name) in enumerate([("githubactions", "Actions"), ("nuget", "NuGet"), ("docker", "Docker")]):
        d.tile(R + 12 + k * 74, 670, 68, 74, ic, name, None, None, size=24)

    # ---- legend
    d.status(L + 8, 787, "run")
    d.text(L + 20, 791, "Run end to end by the scenarios of the Storefront sample", size=11.5, fill=d.t["muted"])
    d.status(L + 360, 787, "fit")
    d.text(L + 372, 791, "Fits by standard (OpenID Connect, forwarded headers); not run by this project", size=11.5, fill=d.t["muted"])


write_both(f"{OUT}/reference-architecture", 1000, 812, "A backend platform with a gateway, identity, messaging, data and observability, and MP Core inside the backends", reference)
write_both(f"{OUT}/request", 960, 406, "One request through MP Core, from the caller to the broker", request)
write_both(f"{OUT}/layers", 960, 580, "The four layers of a backend, what you write and what MP Core provides", layers)
write_both(f"{OUT}/ddd", 960, 584, "The building blocks of Domain-Driven Design and the MP Core type that carries each", ddd)
write_both(f"{OUT}/skills", 960, 430, "The ten skills for AI coding agents and how Codex and Claude Code find them", skills)
print("drawn:", ", ".join(sorted(os.listdir(OUT))))
