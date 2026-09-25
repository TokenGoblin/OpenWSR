// OpenWSR LAN dashboard. Plain script, no build step: it is served from inside the exe and
// has to run in whatever browser a wall tablet or a Home Assistant card happens to have.
//
// URL options, so one card can show one part:
//   view=full|map|threats|storms   theme=dark|light|auto   zoom=N   refresh=seconds
(function () {
  "use strict";

  var params = new URLSearchParams(location.search);
  var view = oneOf(params.get("view"), ["full", "map", "threats", "storms"], "full");
  var theme = oneOf(params.get("theme"), ["dark", "light", "auto"], "dark");
  var zoom = clamp(parseInt(params.get("zoom"), 10), 3, 12, 8);
  var refreshSeconds = clamp(parseInt(params.get("refresh"), 10), 10, 600, 30);

  // A warnings poll runs every minute in the app. Five minutes without one is the app's own
  // threshold for telling the user the map may be out of date, so the page uses the same.
  var STALE_MS = 5 * 60 * 1000;
  // IEM re-renders the mosaic about every five minutes.
  var RADAR_REFRESH_MS = 5 * 60 * 1000;

  document.body.setAttribute("data-view", view);
  applyTheme();

  var map = null, layers = null, centred = false, lastState = null, lastOk = null;
  if (view === "full" || view === "map") buildMap();

  poll();
  setInterval(poll, refreshSeconds * 1000);
  // Recomputed between polls too, so a dead server turns the banner on even though no
  // response arrives to trigger it.
  setInterval(renderStatus, 15 * 1000);

  function applyTheme() {
    var dark = theme === "dark" ||
      (theme === "auto" && window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
    document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
  }

  function buildMap() {
    map = L.map("map", { zoomControl: view === "full", attributionControl: true })
      .setView([39.5, -98.35], 4);

    // OSM's own tiles; the dark style is a CSS inversion of them, the same idea as the
    // app's TileToning, so no third tile provider is involved.
    L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
      maxZoom: 12,
      className: "basemap",
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(map);

    var radar = L.tileLayer(radarUrl(), {
      maxZoom: 12,
      opacity: 0.75,
      attribution: 'Radar: <a href="https://mesonet.agron.iastate.edu/">IEM</a> NEXRAD mosaic',
    }).addTo(map);
    setInterval(function () { radar.setUrl(radarUrl()); }, RADAR_REFRESH_MS);

    layers = {
      alerts: L.layerGroup().addTo(map),
      storms: L.layerGroup().addTo(map),
      places: L.layerGroup().addTo(map),
    };
  }

  function radarUrl() {
    // The cache-buster changes once per refresh window, so every tile in one window agrees.
    var bucket = Math.floor(Date.now() / RADAR_REFRESH_MS);
    return "https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/nexrad-n0q-900913/{z}/{x}/{y}.png?t=" + bucket;
  }

  function poll() {
    fetch("api/state", { cache: "no-cache" })
      .then(function (response) {
        if (!response.ok) throw new Error("HTTP " + response.status);
        var serverDate = Date.parse(response.headers.get("Date") || "");
        return response.json().then(function (state) {
          // Staleness is judged on the server's clock, not the viewer's: a wall tablet whose
          // clock has drifted must not call a fresh snapshot stale, or a stale one fresh.
          state.skewMs = isNaN(serverDate) ? 0 : Date.now() - serverDate;
          return state;
        });
      })
      .then(function (state) {
        lastState = state;
        lastOk = Date.now();
        render(state);
      })
      .catch(function () { renderStatus(); });
  }

  function render(state) {
    renderThreats(state.threats);
    renderStormList(state.storms, state.stormSite);
    if (map) renderMap(state);
    renderStatus();
  }

  function renderStatus() {
    var banner = document.getElementById("banner");
    var updated = document.getElementById("updated");
    var message = null;
    var now = Date.now();

    if (lastOk === null) {
      message = "Cannot reach OpenWSR. Is it running, with the dashboard switched on?";
    } else if (now - lastOk > refreshSeconds * 1000 * 2.5) {
      message = "Lost contact with OpenWSR " + ago(now - lastOk) + ". What is shown may be out of date.";
    } else if (lastState) {
      var serverNow = now - lastState.skewMs;
      var alerts = Date.parse(lastState.alertsUpdatedUtc || "");
      if (isNaN(alerts)) {
        message = "Waiting for OpenWSR's first warnings update.";
      } else if (serverNow - alerts > STALE_MS) {
        message = "Warnings have not refreshed for " + ago(serverNow - alerts).replace(" ago", "") +
          ". OpenWSR may have lost its connection.";
      }
    }

    banner.hidden = message === null;
    banner.textContent = message || "";
    if (lastState && lastOk !== null) {
      updated.textContent = "OpenWSR · warnings " +
        (lastState.alertsUpdatedUtc ? ago(now - lastState.skewMs - Date.parse(lastState.alertsUpdatedUtc)) : "pending") +
        (lastState.stormSite ? " · storms from " + lastState.stormSite : "");
    }
  }

  function renderThreats(threats) {
    var list = document.getElementById("threats");
    list.textContent = "";
    if (!threats.length) {
      list.appendChild(row("empty", "Nothing approaching", "", ""));
      return;
    }
    threats.forEach(function (t) {
      list.appendChild(row("rank-" + t.rank, t.label, t.range, t.detail));
    });
  }

  function renderStormList(storms, site) {
    document.getElementById("storm-site").textContent = site ? "· " + site : "";
    var list = document.getElementById("storms");
    list.textContent = "";
    if (!site) {
      list.appendChild(row("empty", "No storm watch armed", "", "Save a place in OpenWSR to watch its radar."));
      return;
    }
    if (!storms.length) {
      list.appendChild(row("empty", "No cells tracked", "", ""));
      return;
    }
    storms.forEach(function (s) {
      var cls = s.mesoRadiusKm != null ? "rank-tornadic" : s.probabilityOfSevereHail >= 50 ? "rank-direct" : "";
      list.appendChild(row(cls, "Cell " + s.id, s.motion || "untracked", stormDetail(s)));
    });
  }

  function renderMap(state) {
    layers.alerts.clearLayers();
    layers.storms.clearLayers();
    layers.places.clearLayers();

    state.alerts.forEach(function (a) {
      a.polygons.forEach(function (ring) {
        L.polygon(ring, { color: a.color, weight: 2, fillColor: a.color, fillOpacity: 0.15 })
          .bindPopup(popup(a.event, a.headline, a.expires ? "Until " + time(a.expires) : ""))
          .addTo(layers.alerts);
      });
    });

    state.storms.forEach(function (s) {
      if (s.pastPath.length) {
        L.polyline(s.pastPath.concat([[s.lat, s.lon]]), { color: "#f0f0f0", weight: 2, opacity: 0.85 })
          .addTo(layers.storms);
      }
      if (s.forecastPath.length) {
        L.polyline([[s.lat, s.lon]].concat(s.forecastPath), { color: "#f0f0f0", weight: 2, opacity: 0.55, dashArray: "6 6" })
          .addTo(layers.storms);
      }
      var colour = s.mesoRadiusKm != null ? "#ffdc28" : s.probabilityOfSevereHail >= 50 ? "#ff7828" : "#f0f0f0";
      L.circleMarker([s.lat, s.lon], { radius: 5, color: "#000", weight: 1, fillColor: colour, fillOpacity: 1 })
        .bindTooltip(s.id, { permanent: true, direction: "right", className: "storm-label", offset: [6, 0] })
        .bindPopup(popup("Cell " + s.id, s.motion || "Not tracked yet", stormDetail(s)))
        .addTo(layers.storms);
    });

    state.places.forEach(function (p) {
      L.circle([p.lat, p.lon], { radius: p.radiusKm * 1000, color: "#6fb4ff", weight: 1.5, fill: false, dashArray: "4 6" })
        .addTo(layers.places);
      L.circleMarker([p.lat, p.lon], { radius: 4, color: "#6fb4ff", weight: 2, fillColor: "#0e1116", fillOpacity: 1 })
        .bindTooltip(p.name)
        .addTo(layers.places);
    });

    // Centre once, on the primary place. After that the view is the viewer's: re-centring on
    // every poll would fight anyone who panned to look at something.
    if (!centred) {
      var primary = state.places.filter(function (p) { return p.isPrimary; })[0];
      if (primary) {
        map.setView([primary.lat, primary.lon], zoom);
        centred = true;
      }
    }
  }

  function stormDetail(s) {
    var parts = [];
    if (s.mesoRadiusKm != null) parts.push("Rotation detected");
    if (s.probabilityOfHail > 0) parts.push("Hail " + s.probabilityOfHail + "%");
    if (s.probabilityOfSevereHail > 0) parts.push("severe " + s.probabilityOfSevereHail + "%");
    if (s.maxHailSizeInches > 0) parts.push("up to " + s.maxHailSizeInches + " in");
    return parts.join(" · ");
  }

  function row(cls, title, right, detail) {
    var li = document.createElement("li");
    if (cls) li.className = cls;
    var head = document.createElement("div");
    head.className = "head";
    var name = document.createElement("span");
    name.textContent = title;
    head.appendChild(name);
    if (right) {
      var range = document.createElement("span");
      range.className = "range";
      range.textContent = right;
      head.appendChild(range);
    }
    li.appendChild(head);
    if (detail) {
      var d = document.createElement("div");
      d.className = "detail";
      d.textContent = detail;
      li.appendChild(d);
    }
    return li;
  }

  // Built from nodes, not markup: headlines and event names come from the NWS feed.
  function popup(title, line1, line2) {
    var div = document.createElement("div");
    var strong = document.createElement("strong");
    strong.textContent = title;
    div.appendChild(strong);
    [line1, line2].forEach(function (text) {
      if (!text) return;
      var p = document.createElement("div");
      p.textContent = text;
      div.appendChild(p);
    });
    return div;
  }

  function time(iso) {
    var d = new Date(iso);
    return isNaN(d) ? "" : d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  }

  function ago(ms) {
    var minutes = Math.round(ms / 60000);
    if (minutes < 1) return "just now";
    if (minutes < 60) return minutes + " min ago";
    return Math.floor(minutes / 60) + " h " + (minutes % 60) + " min ago";
  }

  function oneOf(value, allowed, fallback) {
    return allowed.indexOf(value) >= 0 ? value : fallback;
  }

  function clamp(value, lo, hi, fallback) {
    return isNaN(value) ? fallback : Math.max(lo, Math.min(hi, value));
  }
})();
