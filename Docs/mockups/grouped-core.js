/* ============================================================================
   Shared logic for the "Grouped" agent-instance mockups (05 + the cleaner
   variations 06–08). Injects the icon sprite, builds the demo trace, and renders
   the header + activation/step/cube tree into #app. Look is owned entirely by
   each variation's stylesheet (tokens.css → grouped.css → the variation <style>).

   Instructional hints ("click a cube to inspect…") are intentionally omitted.
   ========================================================================== */
(function () {
  // ---------- icon sprite ----------
  var SPRITE = '<svg width="0" height="0" aria-hidden="true" focusable="false"><defs>'
    + '<symbol id="i-agent" viewBox="0 0 24 24"><rect x="4" y="8" width="16" height="11" rx="3"/><path d="M12 8V5.2"/><path d="M12 4h0"/><path d="M9.3 13h0"/><path d="M14.7 13h0"/><path d="M9.5 16.2h5"/><path d="M2.6 12.5v3"/><path d="M21.4 12.5v3"/></symbol>'
    + '<symbol id="i-think" viewBox="0 0 24 24"><path d="M12 3.2a5.4 5.4 0 0 0-3.4 9.6V15h6.8v-2.2A5.4 5.4 0 0 0 12 3.2Z"/><path d="M9.2 17.5h5.6"/><path d="M10.2 20h3.6"/></symbol>'
    + '<symbol id="i-tool" viewBox="0 0 24 24"><path d="M14.7 6.2a3.8 3.8 0 0 0-5.1 5.1L3.4 17.5v3.1h3.1l6.2-6.2a3.8 3.8 0 0 0 5.1-5.1l-2.4 2.4-2.1-2.1 2.4-2.4Z"/></symbol>'
    + '<symbol id="i-sub" viewBox="0 0 24 24"><rect x="9" y="3.2" width="6" height="4.2" rx="1.2"/><rect x="3" y="16.6" width="6" height="4.2" rx="1.2"/><rect x="15" y="16.6" width="6" height="4.2" rx="1.2"/><path d="M12 7.4v3.1"/><path d="M6 16.6v-2.1h12v2.1"/><path d="M12 10.5v4"/></symbol>'
    + '<symbol id="i-start" viewBox="0 0 24 24"><circle cx="12" cy="12" r="8.6"/><path d="M10.2 8.4 16 12l-5.8 3.6Z"/></symbol>'
    + '<symbol id="i-globe" viewBox="0 0 24 24"><circle cx="12" cy="12" r="8.6"/><path d="M3.5 12h17"/><path d="M12 3.4c2.6 2.5 4.1 5.4 4.1 8.6S14.6 18.1 12 20.6c-2.6-2.5-4.1-5.4-4.1-8.6S9.4 5.9 12 3.4Z"/></symbol>'
    + '<symbol id="i-search" viewBox="0 0 24 24"><circle cx="11" cy="11" r="6.4"/><path d="M20 20l-4.3-4.3"/></symbol>'
    + '<symbol id="i-code" viewBox="0 0 24 24"><path d="M8.6 8.5 4.6 12l4 3.5"/><path d="M15.4 8.5 19.4 12l-4 3.5"/><path d="M13.4 6 10.6 18"/></symbol>'
    + '<symbol id="i-doc" viewBox="0 0 24 24"><path d="M6.5 3.5h7l4 4v13h-11Z"/><path d="M13.4 3.6v4h4"/><path d="M9 13.5h6"/><path d="M9 16.8h6"/></symbol>'
    + '<symbol id="i-refresh" viewBox="0 0 24 24"><path d="M19.6 12a7.6 7.6 0 1 1-2.3-5.4"/><path d="M17.8 3.4v3.6h-3.6"/></symbol>'
    + '<symbol id="i-theme" viewBox="0 0 24 24"><path d="M20 14.6A8 8 0 1 1 9.4 4a6.5 6.5 0 0 0 10.6 10.6Z"/></symbol>'
    + '<symbol id="i-expand" viewBox="0 0 24 24"><path d="M8 4.5H4.5V8"/><path d="M16 4.5h3.5V8"/><path d="M8 19.5H4.5V16"/><path d="M16 19.5h3.5V16"/></symbol>'
    + '<symbol id="i-bolt" viewBox="0 0 24 24"><path d="M13 3 5.5 13H11l-1 8 7.5-10H12l1-8Z"/></symbol>'
    + '<symbol id="i-arrow" viewBox="0 0 24 24"><path d="M5 12h14"/><path d="M13 6l6 6-6 6"/></symbol>'
    + '<symbol id="i-chevron-r" viewBox="0 0 24 24"><path d="M9 6l6 6-6 6"/></symbol>'
    + '<symbol id="i-token" viewBox="0 0 24 24"><circle cx="12" cy="12" r="8.6"/><path d="M9 9.5h6M9 12h6M9 14.5h4"/></symbol>'
    + '<symbol id="i-grid" viewBox="0 0 24 24"><rect x="4" y="4" width="7" height="7" rx="1.5"/><rect x="13" y="4" width="7" height="7" rx="1.5"/><rect x="4" y="13" width="7" height="7" rx="1.5"/><rect x="13" y="13" width="7" height="7" rx="1.5"/></symbol>'
    + '<symbol id="i-layers" viewBox="0 0 24 24"><path d="M12 3.5 3.5 8 12 12.5 20.5 8 12 3.5Z"/><path d="M3.5 12 12 16.5 20.5 12"/><path d="M3.5 16 12 20.5 20.5 16"/></symbol>'
    + '<symbol id="i-check" viewBox="0 0 24 24"><path d="M5 12.5l4.2 4.2L19 7"/></symbol>'
    + '<symbol id="i-stopwatch" viewBox="0 0 24 24"><circle cx="12" cy="13.7" r="7.3"/><path d="M12 13.7V9.8"/><path d="M9.6 2.8h4.8"/><path d="M12 2.8v3.4"/><path d="M18.4 7.2l1.4-1.4"/></symbol>'
    + '<symbol id="i-x" viewBox="0 0 24 24"><path d="M6.5 6.5l11 11"/><path d="M17.5 6.5l-11 11"/></symbol>'
    + '<symbol id="i-ban" viewBox="0 0 24 24"><circle cx="12" cy="12" r="8.4"/><path d="M6.1 6.1l11.8 11.8"/></symbol>'
    + '</defs></svg>';
  function injectSprite() {
    if (document.getElementById('grouped-sprite')) return;
    var holder = document.createElement('div');
    holder.id = 'grouped-sprite';
    holder.style.display = 'none';
    holder.innerHTML = SPRITE;
    document.body.insertBefore(holder, document.body.firstChild);
  }

  // ---------- helpers ----------
  function esc(s) { return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
  function el(html) { var t = document.createElement('template'); t.innerHTML = html.trim(); return t.content.firstElementChild; }
  function svg(id) { return '<svg class="ic"><use href="#' + id + '"/></svg>'; }
  function durTxt(ms) { if (ms == null) return ''; return ms < 1000 ? ms + 'ms' : (ms / 1000).toFixed(1) + 's'; }
  // Coarser "time ran" for cycles / steps / the whole instance: whole seconds, switching to
  // minutes past 60s — 59s → 1m → 1m 1s → 2m 18s (and h past 60m).
  function fmtRan(ms) { if (ms == null) return ''; var s = Math.round(ms / 1000); if (s < 60) return s + 's'; var m = Math.floor(s / 60), rs = s % 60; if (m < 60) return m + 'm' + (rs ? ' ' + rs + 's' : ''); var h = Math.floor(m / 60), rm = m % 60; return h + 'h' + (rm ? ' ' + rm + 'm' : ''); }
  function actDurMs(act) { var t = 0; (act.steps || []).forEach(function (s) { if (s.durMs) t += s.durMs; }); return t; }   // a cycle's time = sum of its steps
  function totalRunMs() { var t = 0; TRACE.activations.forEach(function (a) { t += actDurMs(a); }); return t; }              // instance total = sum of all steps
  function stClass(s) { return s === 'done' ? 'st-ok' : s === 'failed' ? 'st-fail' : s === 'running' ? 'st-run' : s === 'dropped' ? 'st-drop' : 'st-wait'; }
  function stLabel(s, ms) { return s === 'done' ? (ms != null ? durTxt(ms) : 'Done') : s === 'failed' ? 'Failed' : s === 'running' ? 'Running' : s === 'dropped' ? 'Dropped' : 'Queued'; }
  function stPill(s, ms) { return '<span class="st ' + stClass(s) + '"><span class="dot"></span>' + stLabel(s, ms) + '</span>'; }
  function breakdown(calls) { var b = { done: 0, running: 0, failed: 0, queued: 0, dropped: 0 }; calls.forEach(function (c) { b[c.status] = (b[c.status] || 0) + 1; }); return b; }
  function noun(calls) { var c = calls[0]; return c.kind === 'subagent' ? c.name + ' sub-agents' : c.name + ' calls'; }
  function ioLabel(t) { return '<span class="io-label">' + t + '</span>'; }

  // ---------- demo data ----------
  var SNIPS = ['Solid-state cells hit 500 Wh/kg in pilot production.', 'Cycle life exceeded 1,200 cycles at 90% retention.', 'Fast charge 10→80% achieved in 15 minutes.', 'Pilot manufacturing line slated for 2026.', 'Independent labs corroborated the energy-density figures.', 'Dendrite suppression remains the durability challenge.', 'Sulfide electrolytes lead near-term commercialization.', 'Projected cost below $80/kWh by 2030.'];
  var DOMAINS = ['techreview.example.com', 'battery-news.example.com', 'ieee-spectrum.example.org', 'nature-energy.example.com', 'qs-daily.example.com', 'greencar.example.com', 'arxiv.example.org', 'electrek.example.com'];

  function scrapeCalls() {
    var failed = { 7: 'Connection timed out after 30s', 23: 'HTTP 403 Forbidden — origin blocked the automated request' };
    var out = [];
    for (var i = 1; i <= 52; i++) {
      var d = DOMAINS[i % DOMAINS.length], status = i > 50 ? 'dropped' : failed[i] ? 'failed' : 'done';
      var kb = (2 + (i * 37 % 9)) + (i * 123 % 900) / 1000;
      var url = 'https://' + d + '/article-' + (i < 10 ? '0' + i : i);
      out.push({
        kind: 'tool', icon: 'i-globe', name: 'web-scrape', status: status, title: 'web-scrape #' + i,
        summary: status === 'failed' ? d + ' — ' + failed[i].split('—')[0].trim()
          : status === 'dropped' ? d + ' — dropped (over tool-call-limit)'
            : d + ' — ' + kb.toFixed(1) + ' KB',
        durationMs: (status === 'dropped') ? null : 600 + (i * 53 % 2200),
        input: '{ "url": "' + url + '" }',
        output: status === 'failed' ? failed[i]
          : status === 'dropped' ? 'Dropped — tool-call-limit (50) reached; this call was not executed.'
            : 'Fetched ' + kb.toFixed(1) + ' KB.\n"' + SNIPS[i % SNIPS.length] + '"'
      });
    }
    return out;
  }

  function extractCalls() {
    var out = [];
    for (var i = 1; i <= 3; i++) {
      out.push({ kind: 'tool', icon: 'i-code', name: 'web-extract', status: 'done', title: 'web-extract #' + i, summary: 'batch ' + i + ' — ' + (3 + i) + ' quantitative claims', durationMs: 480 + i * 140, input: '{ "pages": ' + (i * 16) + ' }', output: '{ "claims": ' + (3 + i) + ' }' });
    }
    return out;
  }

  function fcSteps(i, status) {
    var n = (i === 3) ? 8 : 3, calls = [];
    for (var k = 1; k <= n; k++) {
      var cs = 'done';
      if (status === 'failed' && k === n) cs = 'failed';
      else if (status === 'running' && k === n) cs = 'running';
      else if (status === 'queued') cs = 'queued';
      calls.push({ kind: 'tool', icon: 'i-search', name: 'web-search', status: cs, title: 'web-search #' + k,
        summary: cs === 'failed' ? 'source unreachable' : cs === 'running' ? 'searching…' : cs === 'queued' ? 'queued' : 'claim ' + i + '.' + k + ' — ' + (k + 1) + ' corroborating sources',
        durationMs: (cs === 'running' || cs === 'queued') ? null : 700 + (k * 180),
        input: '{ "q": "claim ' + i + '.' + k + ' independent verification" }',
        output: cs === 'failed' ? 'No reachable source returned results.' : cs === 'running' ? 'awaiting results…' : cs === 'queued' ? 'not started' : (k + 1) + ' corroborating sources found.' });
    }
    var d = 0; calls.forEach(function (c) { if (c.durationMs) d += c.durationMs; });   // sub-agent step time = sum of its searches
    return [{ no: 0, state: 'verify', cycle: 0, status: status, durMs: d || undefined, open: (i === 8 || i === 3), title: 'Verifying claim cluster ' + i, thinking: 'Find one independent source per claim and compare the reported figures.', message: status === 'queued' ? null : 'Searching independent sources, one per claim.', calls: calls, select: (i === 3 ? 1 : undefined) }];
  }
  function factChecker(i, status) {
    var map = { done: 'claim cluster ' + i + ' — corroborated', running: 'verifying claim cluster ' + i + ' …', failed: 'claim cluster ' + i + ' — a source was unreachable', queued: 'claim cluster ' + i + ' — queued' };
    return { kind: 'subagent', icon: 'i-sub', name: 'fact-checker', version: 'v2', status: status, title: 'fact-checker #' + i, summary: map[status], steps: fcSteps(i, status) };
  }
  function subagents() {
    var st = { 1: 'done', 2: 'done', 3: 'done', 4: 'done', 5: 'done', 6: 'done', 7: 'failed', 8: 'running', 9: 'running', 10: 'queued' }, out = [];
    for (var i = 1; i <= 10; i++) out.push(factChecker(i, st[i]));
    return out;
  }

  var TRACE = {
    meta: {
      agent: 'research / deep-research', version: 'v3', model: 'gemini-2-5-flash', session: 'b1c4e9af20d7', started: '2:58:02 PM',
      status: 'running', statusText: 'Running · Step 4',
      task: 'Survey the current state of solid-state battery commercialization: gather sources, verify claims across independent agents, and produce one cited report.',
      tiles: [
        { accent: 'primary', icon: 'i-layers', v: '4', l: 'Steps' },
        { accent: 'secondary', icon: 'i-tool', v: '66', l: 'Tool calls' },
        { accent: 'tertiary', icon: 'i-sub', v: '10', l: 'Sub-agents' },
        { accent: 'info', icon: 'i-token', v: '214k', l: 'Tokens' }
      ],
      meters: [
        { l: 'Cost budget · run', v: '$0.62 / $2.00', pct: 31 },
        { l: 'Steps · verify activation', v: '1 / 6', pct: 17 }
      ]
    },
    activations: [
      { state: 'plan', cycle: 1, status: 'done', endSignal: 'READY', nextState: 'gather',
        steps: [{ no: 0, status: 'done', durMs: 3000, title: 'Planning the survey',
          thinking: 'Break the task into gather → verify → synthesize. First find candidate sources.',
          message: 'I’ll search for sources, then fan out scraping and verification in parallel.',
          calls: [{ kind: 'tool', icon: 'i-search', name: 'web-search', status: 'done', title: 'web-search', summary: '50 candidate URLs across 8 domains', durationMs: 900, input: '{ "q": "solid-state battery commercialization 2026", "limit": 50 }', output: '50 candidate URLs across 8 domains.' }] }] },

      { state: 'gather', cycle: 2, status: 'done', endSignal: 'READY', nextState: 'verify', maxSteps: 8,
        steps: [
          { no: 1, status: 'done', durMs: 34000, open: true, title: 'Fetching sources', message: 'Requested 52 fetches; tool-call-limit capped it at 50, run 8 at a time (max-parallel-tools).', calls: scrapeCalls(), select: 51, maxParallel: 8 },
          { no: 2, status: 'done', durMs: 2000, title: 'Extracting claims', message: 'Pulling the quantitative claims out of each fetched page.', calls: extractCalls() }
        ] },

      { state: 'verify', cycle: 3, status: 'running', maxSteps: 6,
        steps: [{ no: 3, status: 'running', durMs: 99000, open: true, title: 'Verifying claims', message: 'Delegating verification — one fact-checker sub-agent per claim cluster.', calls: subagents(), select: 8 }] },

      { state: 'synthesize', cycle: 4, status: 'queued',
        steps: [{ no: 4, status: 'queued', title: 'Synthesizing the report', calls: [{ kind: 'tool', icon: 'i-doc', name: 'report', status: 'queued', title: 'final report', summary: 'cited Markdown report — not yet generated', input: null, output: 'Waits for the gather and verify steps to finish.' }] }] }
    ]
  };

  // ---------- tooltip ----------
  var tipEl;
  function ensureTip() {
    tipEl = document.getElementById('tip');
    if (!tipEl) { tipEl = document.createElement('div'); tipEl.id = 'tip'; tipEl.setAttribute('role', 'tooltip'); document.body.appendChild(tipEl); }
    return tipEl;
  }
  function showTip(cube, c) {
    tipEl.innerHTML = '<div class="tt">' + svg(c.icon) + '<span>' + esc(c.title) + '</span></div>'
      + '<div class="td">' + esc(c.summary) + '</div>'
      + '<div class="td" style="margin-top:3px;font-weight:600;color:var(--' + (c.status === 'done' ? 'success' : c.status === 'failed' ? 'error' : c.status === 'running' ? 'info' : 'text-3') + ')">'
      + stLabel(c.status, c.durationMs) + (c.durationMs != null ? ' · ' + durTxt(c.durationMs) : '') + '</div>';
    tipEl.classList.add('on');
    var r = cube.getBoundingClientRect(), tw = tipEl.offsetWidth, th = tipEl.offsetHeight;
    var left = Math.max(8, Math.min(r.left + r.width / 2 - tw / 2, window.innerWidth - tw - 8));
    var top = r.top - th - 9; if (top < 8) top = r.bottom + 9;
    tipEl.style.left = left + 'px'; tipEl.style.top = top + 'px';
  }
  function hideTip() { tipEl.classList.remove('on'); }

  // ---------- renderers ----------
  function renderCallDetail(c, ownerDisplay) {
    if (c.kind === 'subagent') return renderMini(c, ownerDisplay);
    var wrap = el('<div></div>');
    if (c.input) wrap.appendChild(el('<div class="panel2"><div class="ph">' + ioLabel('Input') + '</div><div class="code">' + esc(c.input) + '</div></div>'));
    var isErr = c.status === 'failed';
    var label = isErr ? 'Error' : c.status === 'dropped' ? 'Not executed' : 'Output';
    wrap.appendChild(el('<div class="panel2 ' + (isErr ? 'err' : '') + '"><div class="ph">' + ioLabel(label) + '</div><div class="code">' + esc(c.output || '') + '</div></div>'));
    return wrap;
  }

  function renderMini(c, ownerDisplay) {
    var m = el('<div class="mini">'
      + '<div class="mini-h"><span class="ava">' + svg('i-agent') + '</span>'
      + '<div style="flex:1 1 auto;min-width:0"><div class="n">' + esc(c.name) + ' <span class="pill pill-soft pill-mono">' + (c.version || 'v1') + '</span></div><div class="s">' + esc(c.summary) + '</div></div>'
      + stPill(c.status) + '</div>'
      + '<div class="mini-stream"></div></div>');
    var s = m.querySelector('.mini-stream');
    // Render the sub-agent's steps with parent.child numbering (e.g. Step 4.1). The detail panel
    // no longer wraps the mini in a second box/header (see select()), so these nest cleanly —
    // one agent header, then its numbered steps.
    (c.steps || []).forEach(function (st) { s.appendChild(renderStep(st, (ownerDisplay || '') + '.')); });
    return m;
  }

  function renderCallRow(c, ownerDisplay) {
    var det = el('<details class="ev call call--' + c.kind + '">'
      + '<summary><span class="tile">' + svg(c.icon) + '</span>'
      + '<span class="call-h"><span class="call-t"><span class="kind">' + esc(c.name) + '</span>' + (c.version ? ' <span class="pill pill-soft pill-mono">' + c.version + '</span>' : '') + '</span><span class="call-s">' + esc(c.summary) + '</span></span>'
      + '<span class="call-r">' + stPill(c.status, c.durationMs) + ' <svg class="ic chev"><use href="#i-chevron-r"/></svg></span></summary>'
      + '<div class="call-b"></div></details>');
    det.querySelector('.call-b').appendChild(renderCallDetail(c, ownerDisplay));
    return det;
  }

  function renderCubeGrid(calls, preselect, ownerDisplay, maxParallel) {
    // No "N web-scrape calls" lead and no "click a cube" hint — the step header's segmented
    // badge already carries the counts. Keep only the (useful) concurrency note when present.
    var sum = maxParallel ? '<span class="s faint">max ' + maxParallel + ' parallel</span>' : '';
    var wrap = el('<div class="cubewrap">' + (sum ? '<div class="cube-sum">' + sum + '</div>' : '') + '<div class="cubes" role="radiogroup"></div><div class="cube-detail"></div></div>');
    var grid = wrap.querySelector('.cubes'), panel = wrap.querySelector('.cube-detail');
    var sel = -1, cubes = [];

    function clearDetail() { panel.innerHTML = ''; panel.className = 'cube-detail'; }
    function select(idx) {
      if (sel === idx) { cubes[idx].classList.remove('sel'); cubes[idx].setAttribute('aria-checked', 'false'); sel = -1; clearDetail(); return; }
      if (sel >= 0) { cubes[sel].classList.remove('sel'); cubes[sel].setAttribute('aria-checked', 'false'); }
      sel = idx; cubes[idx].classList.add('sel'); cubes[idx].setAttribute('aria-checked', 'true');
      cubes.forEach(function (cu, i) { cu.tabIndex = (i === idx) ? 0 : -1; });
      var c = calls[idx];
      panel.innerHTML = '';
      if (c.kind === 'subagent') {
        // A spawned agent's own header (mini-h) IS the detail header — don't draw a generic
        // cd-head (which repeats the name) or wrap it in a second box. The mini-instance is the
        // single container.
        panel.className = 'cube-detail sub';
        panel.appendChild(renderMini(c, ownerDisplay));
      } else {
        panel.className = 'cube-detail';
        panel.appendChild(el('<div class="cd-head" style="--accent:var(--ev-tool);--accent-rgb:var(--ev-tool-rgb)"><span class="tile">' + svg(c.icon) + '</span><span class="cd-t"><span class="kind">' + esc(c.name) + '</span> ' + esc(c.title) + '</span>' + stPill(c.status, c.durationMs) + '</div>'));
        var body = el('<div class="cd-body"></div>'); body.appendChild(renderCallDetail(c, ownerDisplay)); panel.appendChild(body);
      }
    }

    calls.forEach(function (c, idx) {
      var mark = c.status === 'done' ? '<span class="cmark"><svg class="cm-ic cm-check"><use href="#i-check"/></svg></span>'
        : c.status === 'failed' ? '<span class="cmark cx"></span>'
          : c.status === 'running' ? '<span class="cmark cm-bars"><i></i><i></i><i></i></span>'
            : c.status === 'dropped' ? '<span class="cmark"><svg class="cm-ic cm-drop"><use href="#i-ban"/></svg></span>'
              : '<span class="cmark"><svg class="cm-ic cm-watch"><use href="#i-stopwatch"/></svg></span>';
      var cube = el('<button class="cube ' + c.status + '" role="radio" aria-checked="false" tabindex="' + (idx === 0 ? 0 : -1) + '" aria-label="' + esc(c.title) + ' — ' + c.status + '">' + mark + '</button>');
      cube.addEventListener('click', function () { select(idx); });
      cube.addEventListener('mouseenter', function () { showTip(cube, c); });
      cube.addEventListener('mouseleave', hideTip);
      cube.addEventListener('focus', function () { showTip(cube, c); });
      cube.addEventListener('blur', hideTip);
      grid.appendChild(cube); cubes.push(cube);
    });

    grid.addEventListener('keydown', function (e) {
      var keys = ['ArrowRight', 'ArrowDown', 'ArrowLeft', 'ArrowUp'];
      if (keys.indexOf(e.key) < 0) return; e.preventDefault();
      var cur = sel < 0 ? 0 : sel;
      var next = (e.key === 'ArrowRight' || e.key === 'ArrowDown') ? Math.min(cubes.length - 1, cur + 1) : Math.max(0, cur - 1);
      cubes[next].focus();
    });

    if (preselect && preselect >= 1 && preselect <= calls.length) select(preselect - 1);
    return wrap;
  }

  function renderCalls(calls, preselect, ownerDisplay, maxParallel) {
    if (calls.length > 5) return renderCubeGrid(calls, preselect, ownerDisplay, maxParallel);
    var wrap = el('<div class="calllist"></div>');
    calls.forEach(function (c) { wrap.appendChild(renderCallRow(c, ownerDisplay)); });
    return wrap;
  }

  // segmented status badge — one colored section per non-empty status (icon + count, no words)
  function segBadge(calls) {
    var b = breakdown(calls), segs = '';
    if (b.done) segs += '<span class="seg seg-done"><svg class="seg-ic"><use href="#i-check"/></svg>' + b.done + '</span>';
    if (b.running) segs += '<span class="seg seg-run"><span class="segdot"></span>' + b.running + '</span>';
    if (b.failed) segs += '<span class="seg seg-fail"><svg class="seg-ic"><use href="#i-x"/></svg>' + b.failed + '</span>';
    if (b.queued) segs += '<span class="seg seg-queue"><svg class="seg-ic"><use href="#i-stopwatch"/></svg>' + b.queued + '</span>';
    if (b.dropped) segs += '<span class="seg seg-drop"><svg class="seg-ic"><use href="#i-ban"/></svg>' + b.dropped + '</span>';
    return '<span class="segbadge">' + segs + '</span>';
  }

  // prefix is '' for top-level steps and 'N.' for a sub-agent's steps (N = parent step's
  // display number). Display is 1-based (step.no + 1); step.no itself is left untouched.
  function renderStep(step, prefix) {
    var display = (prefix || '') + (step.no + 1);
    var det = el('<details class="ev step step--' + step.status + '"' + (step.open ? ' open' : '') + '>'
      + '<summary>'
      + '<span class="stepno" title="internal step ' + step.no + '">Step ' + display + '</span>'
      + '<span class="step-h"><span class="step-t">' + esc(step.title || '') + '</span></span>'
      + '<span class="step-r">' + (step.durMs != null ? '<span class="step-ran">' + fmtRan(step.durMs) + '</span>' : '') + (step.calls.length > 1 ? segBadge(step.calls) : '') + stPill(step.status) + ' <svg class="ic chev"><use href="#i-chevron-r"/></svg></span>'
      + '</summary><div class="body"></div></details>');
    var body = det.querySelector('.body');
    if (step.thinking) body.appendChild(el('<div class="thinking">' + svg('i-think') + '<span>' + esc(step.thinking) + '</span></div>'));
    if (step.message) body.appendChild(el('<div class="stepmsg">' + esc(step.message) + '</div>'));
    body.appendChild(renderCalls(step.calls, step.select, display, step.maxParallel));
    return det;
  }

  // state-activation tier — groups a state's steps; shows the transition signal that ended it
  function renderActivation(act) {
    var det = el('<details class="ev activation act--' + act.status + '" open>'
      + '<summary>'
      + '<span class="act-dot"></span>'
      + '<span class="act-state">' + esc(act.state) + '</span>'
      + '<span class="act-cycle">Cycle ' + act.cycle + '</span>'
      + '<span class="act-sp"></span>'
      + '<span class="act-r">'
      + '<span class="act-steps">' + act.steps.length + (act.maxSteps ? (' / ' + act.maxSteps) : '') + (act.steps.length === 1 && !act.maxSteps ? ' step' : ' steps') + '</span>'
      + (actDurMs(act) > 0 ? '<span class="act-ran">' + fmtRan(actDurMs(act)) + '</span>' : '')
      + stPill(act.status)
      + ' <svg class="ic chev"><use href="#i-chevron-r"/></svg>'
      + '</span>'
      + '</summary><div class="act-body"></div></details>');
    var body = det.querySelector('.act-body');
    act.steps.forEach(function (st) { body.appendChild(renderStep(st)); });
    if (act.endSignal) {
      body.appendChild(el('<div class="tmark"><span class="tline"></span><span class="sig">' + svg('i-arrow') + esc(act.endSignal) + (act.nextState ? (' → ' + esc(act.nextState)) : '') + '</span><span class="tline"></span></div>'));
    }
    return det;
  }

  function renderHeader(m) {
    var tiles = m.tiles.map(function (t) {
      return '<div class="tile-stat" style="--accent:var(--' + t.accent + ');--accent-rgb:var(--' + t.accent + '-rgb)"><span class="ti">' + svg(t.icon) + '</span><span class="tile-body"><span class="v">' + t.v + '</span><span class="l">' + t.l + '</span></span></div>';
    }).join('');
    var meters = m.meters.map(function (mt) {
      var cls = mt.pct >= 100 ? ' over' : mt.pct >= 80 ? ' warn' : '';
      return '<div class="meter"><div class="meter-top"><span class="meter-l">' + esc(mt.l) + '</span><span class="meter-v">' + esc(mt.v) + '</span></div><div class="meter-bar' + cls + '"><span style="width:' + mt.pct + '%"></span></div></div>';
    }).join('');
    return el('<div class="inst">'
      + '<div class="inst-top">'
      + '<span class="inst-ava">' + svg('i-agent') + '</span>'
      + '<div class="inst-id" style="flex:1 1 auto;min-width:0">'
      + '<div class="inst-name">' + esc(m.agent) + '</div>'
      + '<div class="inst-sub"><span class="pill pill-soft pill-mono">' + esc(m.version) + '</span>'
      + '<span class="pill pill-soft">' + svg('i-bolt') + esc(m.model) + '</span>'
      + '<span class="mono faint">session ' + esc(m.session) + '</span>'
      + '<span class="faint">· started ' + esc(m.started) + '</span>'
      + (totalRunMs() > 0 ? '<span class="faint">· ' + (m.status === 'running' ? 'running ' : 'ran ') + fmtRan(totalRunMs()) + '</span>' : '') + '</div>'
      + '</div>'
      + '<span class="st ' + stClass(m.status) + ' inst-status" title="Run status — shows the AgentExitReason on completion (Completed / GuardrailViolation / BudgetExceeded / …)"><span class="dot"></span>' + esc(m.statusText) + '</span>'
      + '</div>'
      + '<div class="inst-task"><span class="lbl">Task</span> ' + esc(m.task) + '</div>'
      + '<div class="tiles">' + tiles + '</div>'
      + '<div class="meters">' + meters + '</div>'
      + '</div>');
  }

  // ---------- mount ----------
  function mount() {
    injectSprite();
    ensureTip();
    window.addEventListener('scroll', hideTip, true);
    var app = document.getElementById('app');
    if (!app) return;
    app.appendChild(renderHeader(TRACE.meta));
    var stream = el('<div class="stream"></div>');
    TRACE.activations.forEach(function (act) { stream.appendChild(renderActivation(act)); });
    app.appendChild(stream);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount);
  else mount();
})();
