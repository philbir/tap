# Tap product story

Tap has one platform and two focused products:

| Identity | Promise | Use this name when… | Visual |
|---|---|---|---|
| **Tap Platform** | The local-first HTTP workbench | Talking about the complete family, shared philosophy, or Studio and Tunnels together | A distributor hub routing one HTTP flow into two paths |
| **Tap Studio** | The HTTP request and auth credential crafter | Talking about composing, authenticating, executing, documenting, flows, tests, or the desktop app | A precision craft studio shaping request and credential flows |
| **Tap Tunnels** | Tunnels with inspection built in | Talking about public or tailnet URLs, Cloudflare, Tailscale, capture, replay, SSE, or WebSockets | An illuminated tunnel with an inspection window |

## The user story

**Craft → route → inspect.** Tap Studio crafts the authenticated request. Tap Tunnels routes it
to a local service and its built-in Inspector shows exactly what arrived. Tap Platform is the
workbench that makes those two directions feel like one workflow.

Studio and Tunnels are both useful alone. The platform story should never imply that one requires
the other.

## Naming rules

- Say **Tap Platform** for the umbrella. Do not use bare “Tap” as a third product beside Studio
  and Tunnels.
- Say **Tap Studio** for the outbound request and credential product.
- Say **Tap Tunnels** for the inbound tunnel product.
- Say **Inspector** for the capture view inside Tap Tunnels. Do not present “Tunnel + Inspector”
  as a separate or compound product name.
- Internal types such as `TapInspectorHost` can keep their implementation names; user-facing copy
  should follow the product names above.

## Brand assets

| Product | Icon | Hero image |
|---|---|---|
| Tap Platform | [`tap-platform-icon.svg`](../assets/tap-platform-icon.svg) | [`tap-platform-hero.png`](../assets/tap-platform-hero.png) |
| Tap Studio | [`tap-studio-icon.svg`](../assets/tap-studio-icon.svg) | [`tap-studio-workbench-hero.png`](../assets/tap-studio-workbench-hero.png) |
| Tap Tunnels | [`tap-tunnels-icon.svg`](../assets/tap-tunnels-icon.svg) | [`tap-tunnels-hero.png`](../assets/tap-tunnels-hero.png) |

The icon family uses one rounded-square silhouette, luminous HTTP flow lines, and the same violet,
indigo, cobalt, teal, white, and orange signal palette. The hero illustrations use the same
isometric materials and lighting so each product is distinct without looking unrelated.

## Published asset map

- **Web:** each product ships an SVG favicon, PNG fallback, 180 px Apple touch icon, and a web
  manifest with 192 px and 512 px icons. Legacy `tap-favicon.svg`, `tap-mark.svg`, `tap-logo.svg`,
  and `icon.svg` paths remain compatibility aliases for the current product mark.
- **Tap Studio desktop:** Tauri `.icns`, `.ico`, PNG, Windows tile, Android, and iOS icon sets are
  generated from `tap-studio-icon.svg`; the startup splash uses the same source mark.
- **NuGet:** Tunnels packages (`Tap`, `Tap.Aspire.Hosting`, `Tap.Internals`) pack the Tunnels icon.
  Studio packages (`Tap.Studio.Cli`, `Tap.Execution`, `Tap.Workspace`) pack the Studio icon.
- **Docker / OCI:** the Tap Tunnels image serves its icon from `/tap-tunnels-icon.svg`, embeds it
  in the image, and identifies the product and icon path in image labels and index annotations.
- **Binary archives:** Tap Tunnels release archives include SVG and PNG product icons beside the
  executable.
