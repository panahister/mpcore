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
        ("cyan", "6", "Messages", ["Released after", "the commit", "", "Kafka, RabbitMQ", "", "Inbox on the", "consumer"]),
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
        ("cyan", "Infrastructure", "adapters",
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
        ("cyan", "Connect", ["configure-messaging", "integrate-contexts"]),
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
        ("Claude Code", ["CLAUDE.md", ".claude/skills/"], "cyan", "claude"),
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
    d.group(L, 650, 332, 104, "cyan", "MESSAGING")
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


def audit(d):
    d.heading(32, 44, "An audit trail is not a log",
              "A log helps an operator find a fault. An audit trail answers a question somebody will ask in a year: who did this?")
    # ---- the two paths
    d.group(32, 92, 560, 270, "purple", "WHO DID WHAT, FROM WHAT TO WHAT")
    d.card(48, 114, 160, 112, "green", "Your handler", ["changes an aggregate", "and returns"], kicker="a change")
    d.arrow([(210, 170), (232, 170)], sw=1.7)
    d.card(234, 114, 342, 112, "blue", "One commit", ["The change and its audit record are saved", "together. Rolled back, neither exists: the trail", "never claims a change that did not happen."], kicker="mp core")
    d.card(48, 236, 160, 112, "rose", "A rule is broken", ["the attempt is refused,", "the change is rolled back"], kicker="a refused attempt")
    d.arrow([(210, 292), (232, 292)], sw=1.7)
    d.card(234, 236, 342, 112, "blue", "Written apart", ["The refusal is recorded on a connection of its", "own, so it survives the rollback: what somebody", "tried is often what a reviewer wants to know."], kicker="mp core")
    # ---- what a record holds
    d.group(608, 92, 320, 270, "slate", "WHAT A RECORD HOLDS")
    rows = [("Who", "the actor, from the token"), ("What", "the action, the entity"), ("Change", "each field: before, after"),
            ("Outcome", "done, refused, failed"), ("Why not", "the rule's own code"), ("When", "and which request")]
    for k, (name, what) in enumerate(rows):
        y = 120 + k * 39
        d.rect(624, y, 288, 30, d.t["canvas"], d.t["frame"], r=7, sw=1)
        d.text(636, y + 18.5, name, size=11.5, weight=700, fill=d.ink("purple"))
        d.text(706, y + 18.5, what, size=11.5, fill=d.t["muted"])
    # ---- the promises
    promises = [("The actor is never a header", "It comes from the validated token. Queued work", "runs as a named actor: system:ReserveStock."),
                ("Secrets cannot be recorded", "A policy names the fields that are kept;", "a credential is refused, an identifier masked."),
                ("In your own database", "A table next to the business data, append-only:", "the role may insert and read, nothing else.")]
    for k, (title, l1, l2) in enumerate(promises):
        x = 32 + k * 302
        d.rect(x, 382, 292, 82, d.fill("purple"), d.stroke("purple"), r=10, shadow=True)
        d.text(x + 16, 406, title, size=12.8, weight=700, fill=d.ink("purple"))
        d.text(x + 16, 426, l1, size=11.2, fill=d.t["muted"])
        d.text(x + 16, 443, l2, size=11.2, fill=d.t["muted"])


def language(d):
    d.heading(32, 44, "No sentence is written in code",
              "A failure carries a key and its arguments. The text is found when it is shown, in the language the caller asked for.")
    d.card(32, 92, 250, 150, "green", "A rule is broken", ["`catalog.price_jump_too_large`", "", "`max_move_percent = 50`", "`current = 485.00`", "`requested = 4850.00`"], kicker="your code: a key, and arguments")
    d.arrow([(284, 167), (312, 167)], sw=1.7)
    d.group(316, 92, 300, 150, "teal", "THE MESSAGE CATALOG")
    sources = [("1", "Texts edited at run time", "in the database"), ("2", "Your module's resource files", ".resx, one per culture you add"), ("3", "MP Core's own texts", "English, the default")]
    for k, (n, name, where) in enumerate(sources):
        y = 112 + k * 41
        d.rect(330, y, 272, 34, d.t["canvas"], d.t["frame"], r=8, sw=1)
        d.badge(347, y + 17, n, "teal", r=9)
        d.text(364, y + 15, name, size=11.5, weight=700)
        d.text(364, y + 28, where, size=10.3, fill=d.t["muted"])
    d.arrow([(618, 140), (646, 120)], sw=1.7)
    d.arrow([(618, 194), (646, 214)], sw=1.7)
    d.rect(650, 92, 278, 68, d.fill("blue"), d.stroke("blue"), r=10, shadow=True)
    d.text(664, 112, "ACCEPT-LANGUAGE: EN", size=9.5, weight=700, fill=d.accent("blue"), spacing="0.8")
    d.text(664, 132, "A price may move by at most 50% in one", size=11.5)
    d.text(664, 148, "step (from 485.00 to 4850.00).", size=11.5)
    d.rect(650, 174, 278, 68, d.fill("blue"), d.stroke("blue"), r=10, shadow=True)
    d.text(664, 194, "ACCEPT-LANGUAGE: YOUR CULTURE", size=9.5, weight=700, fill=d.accent("blue"), spacing="0.8")
    d.text(664, 214, "The same rule, in a language your", size=11.5)
    d.text(664, 230, "product adds in its own repository.", size=11.5)
    promises = [("The same on both transports", "Problem Details over REST, a rich gRPC status:", "one key, one text, one code for the client."),
                ("A culture falls back", "A region, then its language, then the default.", "A text found nowhere is logged, never invented."),
                ("Support edits a text, live", "A stored translation wins over the file, and", "reaches every instance without a release.")]
    for k, (title, l1, l2) in enumerate(promises):
        x = 32 + k * 302
        d.rect(x, 262, 292, 82, d.fill("teal"), d.stroke("teal"), r=10, shadow=True)
        d.text(x + 16, 286, title, size=12.8, weight=700, fill=d.ink("teal"))
        d.text(x + 16, 306, l1, size=11.2, fill=d.t["muted"])
        d.text(x + 16, 323, l2, size=11.2, fill=d.t["muted"])


def samples(d):
    d.heading(32, 44, "Two samples, both run on every change",
              "The same framework under an online store and under a delivery platform. Tests and checks are from runs on GitHub.")
    cards = [
        ("green", "The first sample", "Storefront", "An online store",
         "A modular monolith of four modules, and two services",
         [("Transports", "REST and gRPC"), ("Messaging", "Kafka and RabbitMQ"), ("Data", "PostgreSQL, TimescaleDB, Redis"),
          ("Tenants", "one"), ("Business rules", "55"), ("Tests", "262"), ("Scenarios", "22: 146 and 160 checks"), ("MP Core", "0.9.1")],
         ["A checkout sent twice, or eight at once", "An audit trail that keeps a refused attempt", "Texts in the caller's language, edited live"],
         ["postgresql", "timescale", "redis", "apachekafka", "rabbitmq", "keycloak", "apisix"], "mpcore-storefront-sample"),
        ("cyan", "The second sample", "Tiffin", "Food delivery",
         "Nine services, a database each, no shared assembly",
         [("Transports", "REST and gRPC"), ("Messaging", "Kafka and RabbitMQ, a saga"), ("Data", "PostgreSQL, TimescaleDB, Redis, S3"),
          ("Tenants", "two cities"), ("Business rules", "33"), ("Tests", "216"), ("Scenarios", "16: 286 checks, each store"), ("MP Core", "0.9.2")],
         ["An order through six services, and taken back", "The city on every message and every call", "A service down, and nothing lost"],
         ["postgresql", "timescale", "redis", "apachekafka", "rabbitmq", "keycloak", "apisix", "rustfs", "seaweedfs"], "mpcore-tiffin-sample"),
    ]
    w, gx, x0, y0, h = 440, 16, 32, 86, 448
    for i, (color, kicker, name, what, shape, facts, proves, icons, repo) in enumerate(cards):
        x = x0 + i * (w + gx)
        d.rect(x, y0, w, h, d.fill(color), d.stroke(color), r=12, shadow=True)
        d.add(f'<rect x="{x}" y="{y0}" width="{w}" height="6" rx="3" fill="{d.accent(color)}"/>')
        d.text(x + 18, y0 + 30, kicker.upper(), size=9.5, weight=700, fill=d.accent(color), spacing="0.8")
        d.text(x + 18, y0 + 58, name, size=22, weight=700, fill=d.ink(color))
        d.text(x + 18 + len(name) * 13 + 10, y0 + 58, what, size=13, fill=d.t["muted"])
        d.text(x + 18, y0 + 80, shape, size=12, fill=d.t["text"])
        for k, (label, value) in enumerate(facts):
            fy = y0 + 106 + k * 23
            d.line(x + 18, fy + 7, x + w - 18, fy + 7, color=d.stroke(color), sw=0.6, dash="2 4")
            d.text(x + 18, fy, label, size=11, fill=d.t["muted"])
            d.text(x + 150, fy, value, size=11.5, weight=600, fill=d.ink(color) if label in ("Scenarios", "Tests") else d.t["text"])
        py = y0 + 106 + len(facts) * 23 + 8
        d.text(x + 18, py, "WHAT IT PROVES FIRST", size=9.5, weight=700, fill=d.accent(color), spacing="0.8")
        for k, line in enumerate(proves):
            d.status(x + 24, py + 16 + k * 18 - 4, "run")
            d.text(x + 36, py + 16 + k * 18, line, size=11.5)
        iy = y0 + h - 70
        for k, ic in enumerate(icons):
            d.icon(ic, x + 18 + k * 26, iy, 18)
        d.chip(x + 18, y0 + h - 40, "github.com/panahister/" + repo, "blue", size=10.5, pad=10, mono=False)


write_both(f"{OUT}/audit", 960, 486, "Business audit: the change and its record in one commit, a refused attempt recorded apart", audit)
write_both(f"{OUT}/language", 960, 366, "Localization: a key and arguments become a text in the caller's language, from three sources", language)
def capabilities(d):
    d.heading(32, 44, "Everything MP Core does, in one picture",
              "Twelve areas, twenty-eight packages. The catalogue names, for each line, the package that carries it and how it was proved.")
    areas = [
        ("green", "Domain model", ["Aggregates, entities, value objects", "Business rules with a name and a code", "Domain and integration events", "No framework in the domain"]),
        ("teal", "Use cases", ["Commands and queries, apart", "A handler is a static method", "Validation before the handler", "A failure is a value, not an exception"]),
        ("blue", "One commit", ["The transaction is the framework's", "Outbox: a message leaves if committed", "A failure after a change rolls it back", "A failed attempt takes its messages"]),
        ("cyan", "Messaging", ["Apache Kafka and RabbitMQ", "Durable local queues, no broker", "Inbox: handled once", "Retry, dead letters, giving up"]),
        ("purple", "Twice is once", ["Idempotency-Key on a request", "The key commits with the change", "A repeat receives the first answer", "Business keys, where a key is not enough"]),
        ("rose", "Security", ["A bearer-only resource server", "Deny by default", "Keycloak, or any OpenID Connect", "Behind a gateway, trusting little"]),
        ("blue", "Transport", ["REST, with Problem Details", "gRPC, with a rich status", "Both, each on its own port", "OpenAPI; versions by route"]),
        ("purple", "Business audit", ["Who did what, from what to what", "In the commit of the change", "A refused attempt is kept", "Secrets refused, identifiers masked"]),
        ("teal", "Language", ["No sentence is written in code", "The caller's language, both transports", "English built in, your languages added", "Texts edited while it runs"]),
        ("green", "Data and cache", ["PostgreSQL with EF Core", "TimescaleDB hypertables", "Memory, Redis, or both in two levels", "Paging and sorting, with limits"]),
        ("slate", "Operations", ["Logs, traces, metrics: OpenTelemetry", "Secrets masked before they leave", "Alive and ready, REST and gRPC", "Resilient calls to other systems"]),
        ("cyan", "Tooling", ["One command generates a backend", "Shape, transport and broker: 18 combinations", "Ten skills for AI coding agents", "Multi-tenancy from a token's claim"]),
    ]
    w, h, gx, gy, x0, y0 = 293, 132, 8, 10, 32, 90
    for i, (color, title, lines) in enumerate(areas):
        x, y = x0 + (i % 3) * (w + gx), y0 + (i // 3) * (h + gy)
        d.rect(x, y, w, h, d.fill(color), d.stroke(color), r=10, shadow=True)
        clip = f"cap{i}"
        d.add(f'<clipPath id="{clip}"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="10"/></clipPath>')
        d.add(f'<rect x="{x}" y="{y}" width="{w}" height="6" fill="{d.accent(color)}" clip-path="url(#{clip})"/>')
        d.text(x + 14, y + 31, title, size=14.5, weight=700, fill=d.ink(color))
        for k, line in enumerate(lines):
            d.add(f'<circle cx="{x + 18}" cy="{y + 52 + k * 19.5}" r="2.4" fill="{d.accent(color)}"/>')
            d.text(x + 28, y + 56 + k * 19.5, line, size=11.8, fill=d.t["text"])


def ecosystem(d):
    d.heading(40, 44, "The MP ecosystem",
              "Two reusable foundations, independently secured platform boundaries, and reference products that prove the contracts.")

    d.text(40, 96, "FOUNDATIONS", size=10.5, weight=700, fill=d.t["muted"], spacing="1.3")
    d.card(40, 116, 260, 104, "blue", "MP Core",
           [".NET backend architecture", "packages · CLI · template"], kicker="backend foundation", title_size=18)
    d.card(40, 330, 260, 104, "purple", "MP Frontend",
           ["React and Next.js architecture", "packages · CLI · AI workflows"], kicker="frontend foundation", title_size=18)

    d.text(410, 96, "REFERENCE PRODUCT", size=10.5, weight=700, fill=d.t["muted"], spacing="1.3")
    d.card(410, 116, 250, 104, "purple", "Storefront",
           ["modular monolith + services", "backend reference"], kicker="commerce", title_size=18)

    d.group(710, 98, 450, 390, "teal", "Tiffin reference platform", dash="7 5")
    d.card(735, 330, 170, 106, "green", ["Tiffin", "Frontend"],
           ["customer + operations"], kicker="product surfaces", title_size=16)
    d.card(940, 126, 190, 82, "purple", "Keycloak",
           ["identity + authorization"], kicker="authority", title_size=16)
    d.card(940, 232, 190, 82, "blue", "Apache APISIX",
           ["declarative REST + gRPC"], kicker="edge", title_size=16)
    d.card(940, 350, 190, 104, "teal", ["Tiffin", "Backend"],
           ["nine MP Core services"], kicker="business platform", title_size=16)

    d.arrow([(300, 168), (410, 168)], color=d.accent("blue"), sw=2.2)
    d.text(355, 153, "builds", size=10, weight=700, fill=d.t["muted"], anchor="middle")
    d.arrow([(300, 380), (735, 380)], color=d.accent("purple"), sw=2.2)
    d.text(512, 365, "builds", size=10, weight=700, fill=d.t["muted"], anchor="middle")
    d.arrow([(300, 198), (350, 198), (350, 468), (1018, 468), (1018, 454)],
            color=d.accent("blue"), sw=2.2)
    d.text(520, 483, "backend foundation", size=10, weight=700, fill=d.t["muted"], anchor="middle")
    d.arrow([(905, 356), (920, 356), (920, 167), (940, 167)], color=d.accent("purple"), sw=2)
    d.arrow([(905, 404), (920, 404), (920, 273), (940, 273)], color=d.accent("blue"), sw=2)
    d.arrow([(1035, 208), (1035, 350)], color=d.accent("purple"), sw=2)
    d.arrow([(1082, 314), (1082, 350)], color=d.accent("blue"), sw=2)

    d.text(40, 515, "Every repository owns a bounded source contract; no product imports another repository's private design or runtime state.",
           size=12.5, fill=d.t["muted"])


write_both(f"{OUT}/capabilities", 960, 668, "The twelve areas of MP Core's capabilities", capabilities)
write_both(f"{OUT}/reference-architecture", 1000, 812, "A backend platform with a gateway, identity, messaging, data and observability, and MP Core inside the backends", reference)
write_both(f"{OUT}/request", 960, 406, "One request through MP Core, from the caller to the broker", request)
write_both(f"{OUT}/layers", 960, 580, "The four layers of a backend, what you write and what MP Core provides", layers)
write_both(f"{OUT}/ddd", 960, 584, "The building blocks of Domain-Driven Design and the MP Core type that carries each", ddd)
write_both(f"{OUT}/samples", 960, 560, "The two samples: Storefront, an online store, and Tiffin, food delivery in nine services; what each is built of, how it was proved and what it proves", samples)
write_both(f"{OUT}/skills", 960, 430, "The ten skills for AI coding agents and how Codex and Claude Code find them", skills)
write_both(f"{OUT}/ecosystem", 1200, 548, "The MP ecosystem: MP Core and MP Frontend foundations with Storefront and Tiffin references", ecosystem)
print("drawn:", ", ".join(sorted(os.listdir(OUT))))
