'use strict';

// ------------------------------------------------------------------ helpers
const $ = (sel, root = document) => root.querySelector(sel);
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const KEY_STORAGE = 'pgapi_admin_key';

function getKey() { try { return sessionStorage.getItem(KEY_STORAGE); } catch { return window.__key; } }
function setKey(k) { try { k ? sessionStorage.setItem(KEY_STORAGE, k) : sessionStorage.removeItem(KEY_STORAGE); } catch { window.__key = k; } }

class ApiError extends Error { constructor(status, msg) { super(msg); this.status = status; } }

async function call(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: { 'X-API-Key': getKey() || '', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (res.status === 401) { logout(); throw new ApiError(401, 'Nicht angemeldet oder Key ungültig.'); }
  if (res.status === 204) return null;
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) throw new ApiError(res.status, (data && (data.detail || data.title)) || `HTTP ${res.status}`);
  return data;
}
const admin = (method, path, body) => call(method, '/api/admin' + path, body);

function toast(msg) {
  const t = $('#toast');
  t.textContent = msg; t.hidden = false;
  clearTimeout(t._h); t._h = setTimeout(() => (t.hidden = true), 3000);
}

// ------------------------------------------------------------------ dialog
function openDialog({ title, body, buttons }) {
  const d = $('#dialog');
  $('#dialogTitle').textContent = title;
  const b = $('#dialogBody');
  b.innerHTML = '';
  if (typeof body === 'string') b.innerHTML = body; else if (body) b.append(body);
  $('#dialogError').textContent = '';
  const actions = $('#dialogActions');
  actions.innerHTML = '';
  for (const btn of buttons || [{ label: 'Schliessen' }]) {
    const el = document.createElement('button');
    el.type = 'button';
    el.textContent = btn.label;
    if (btn.cls) el.className = btn.cls;
    el.onclick = async () => {
      if (!btn.action) return d.close();
      el.disabled = true;
      try {
        const keepOpen = await btn.action();
        if (!keepOpen) d.close();
      } catch (e) {
        $('#dialogError').textContent = e.message;
      } finally { el.disabled = false; }
    };
    actions.append(el);
  }
  d.showModal();
  return d;
}
const dialogError = (msg) => ($('#dialogError').textContent = msg);

// ------------------------------------------------------------------ forms
// field: { k, label, type: text|password|number|checkbox|select|textarea|list|json|datetime, options, hint, wide, readonly }
function buildForm(fields, obj) {
  const root = document.createElement('div');
  root.className = 'grid';
  const inputs = {};
  for (const f of fields) {
    const v = obj[f.k];
    const label = document.createElement('label');
    if (f.wide || f.type === 'textarea' || f.type === 'json') label.className = 'wide';
    let input;
    if (f.type === 'checkbox') {
      label.className += ' check';
      input = document.createElement('input');
      input.type = 'checkbox';
      input.checked = !!v;
      label.append(input, document.createTextNode(f.label));
    } else {
      label.append(document.createTextNode(f.label));
      if (f.type === 'select') {
        input = document.createElement('select');
        const opts = typeof f.options === 'function' ? f.options() : f.options;
        for (const o of opts) {
          const opt = document.createElement('option');
          opt.value = o; opt.textContent = o === '' ? '–' : o;
          input.append(opt);
        }
        input.value = v ?? opts[0] ?? '';
      } else if (f.type === 'textarea' || f.type === 'json') {
        input = document.createElement('textarea');
        input.value = f.type === 'json' ? JSON.stringify(v ?? [], null, 2) : (v ?? '');
        if (f.rows) input.rows = f.rows;
      } else {
        input = document.createElement('input');
        input.type = { number: 'number', password: 'password', datetime: 'datetime-local' }[f.type] || 'text';
        if (f.type === 'list') input.value = (v || []).join(', ');
        else if (f.type === 'datetime') input.value = v ? new Date(v).toISOString().slice(0, 16) : '';
        else input.value = v ?? '';
        if (f.type === 'number') input.step = 'any';
      }
      label.append(input);
    }
    if (f.readonly) input.disabled = true;
    if (f.hint) { const s = document.createElement('small'); s.textContent = f.hint; label.append(s); }
    inputs[f.k] = input;
    root.append(label);
  }
  root.read = () => {
    const out = { ...obj };
    for (const f of fields) {
      const i = inputs[f.k];
      switch (f.type) {
        case 'checkbox': out[f.k] = i.checked; break;
        case 'number': out[f.k] = i.value === '' ? null : Number(i.value); break;
        case 'list': out[f.k] = i.value.split(',').map((s) => s.trim()).filter(Boolean); break;
        case 'json':
          try { out[f.k] = JSON.parse(i.value || 'null'); } catch { throw new Error(`${f.label}: ungültiges JSON`); }
          break;
        case 'datetime': out[f.k] = i.value ? new Date(i.value).toISOString() : null; break;
        case 'select': out[f.k] = i.value === '' ? null : i.value; break;
        default: out[f.k] = i.value === '' && f.nullable ? null : i.value;
      }
    }
    return out;
  };
  root.inputs = inputs;
  return root;
}

// ------------------------------------------------------------------ generic CRUD list
async function crudView(cfg) {
  const view = $('#view');
  view.innerHTML = `<div class="card"><div class="toolbar"><h2>${esc(cfg.title)}</h2></div>
    ${cfg.intro ? `<p class="muted">${cfg.intro}</p>` : ''}<div class="table-wrap"><table><thead></thead><tbody></tbody></table></div></div>`;
  const toolbar = $('.toolbar', view);
  const newBtn = document.createElement('button');
  newBtn.className = 'primary'; newBtn.textContent = 'Neu';
  newBtn.onclick = () => (cfg.onNew ? cfg.onNew() : edit(null));
  toolbar.append(newBtn);

  const items = await admin('GET', cfg.path);
  $('thead', view).innerHTML = '<tr>' + cfg.columns.map((c) => `<th>${esc(c.label)}</th>`).join('') + '<th></th></tr>';
  const tbody = $('tbody', view);
  if (!items.length) tbody.innerHTML = `<tr><td colspan="${cfg.columns.length + 1}" class="muted">Noch keine Einträge.</td></tr>`;
  for (const item of items) {
    const tr = document.createElement('tr');
    tr.innerHTML = cfg.columns.map((c) => `<td>${c.html ? c.html(item) : esc(item[c.k])}</td>`).join('');
    const td = document.createElement('td');
    td.className = 'actions';
    for (const a of [...(cfg.rowActions || []), ...(cfg.noEdit ? [] : [{ label: 'Bearbeiten', fn: (i) => edit(i) }]),
      { label: 'Löschen', cls: 'danger', fn: (i) => remove(i) }]) {
      const b = document.createElement('button');
      b.textContent = a.label; if (a.cls) b.className = a.cls;
      b.onclick = () => a.fn(item);
      td.append(b);
    }
    tr.append(td);
    tbody.append(tr);
  }

  async function edit(item, prefill) {
    const isNew = !item;
    const obj = structuredClone(item || { ...(cfg.defaults ? cfg.defaults() : {}), ...(prefill || {}) });
    const fields = cfg.fields(isNew);
    const form = buildForm(fields, obj);
    openDialog({
      title: (isNew ? 'Neu: ' : 'Bearbeiten: ') + cfg.singular + (isNew ? '' : ' ' + item[cfg.id]),
      body: form,
      buttons: [
        ...(cfg.formButtons ? cfg.formButtons(form) : []),
        { label: 'Abbrechen' },
        { label: 'Speichern', cls: 'primary', action: async () => { await cfg.save(form.read(), isNew, item); toast('Gespeichert'); render(); } },
      ],
    });
  }

  async function remove(item) {
    openDialog({
      title: `${cfg.singular} löschen?`,
      body: `<p><b>${esc(item[cfg.id])}</b> wird aus der Konfiguration entfernt.</p>`,
      buttons: [{ label: 'Abbrechen' }, {
        label: 'Löschen', cls: 'primary', action: async () => {
          await admin('DELETE', `${cfg.path}/${encodeURIComponent(cfg.deleteId ? cfg.deleteId(item) : item[cfg.id])}`);
          toast('Gelöscht'); render();
        },
      }],
    });
  }
  return { edit };
}

// ------------------------------------------------------------------ views
let cache = { connections: [], layers: [] };
let pendingLayerPrefill = null;
const sslModes = ['Prefer', 'Disable', 'Allow', 'Require', 'VerifyCA', 'VerifyFull'];
const rolesHtml = (roles) => (roles || []).map((r) => `<span class="badge">${esc(r)}</span>`).join('');
const onOff = (v, on = 'ja', off = 'nein') => `<span class="badge ${v ? 'on' : 'off'}">${v ? on : off}</span>`;

const views = {
  async connections() {
    cache.connections = await admin('GET', '/connections');
    const fields = (isNew) => [
      { k: 'name', label: 'Name', readonly: !isNew },
      { k: 'host', label: 'Host' },
      { k: 'port', label: 'Port', type: 'number' },
      { k: 'database', label: 'Datenbank' },
      { k: 'username', label: 'Benutzer' },
      { k: 'password', label: 'Passwort', type: 'password', hint: 'Leer lassen oder ******** = unverändert' },
      { k: 'passwordEnvVar', label: 'Passwort aus Umgebungsvariable', nullable: true, hint: 'Hat Vorrang vor Passwort' },
      { k: 'sslMode', label: 'SSL-Modus', type: 'select', options: sslModes },
      { k: 'maxPoolSize', label: 'Max. Pool-Grösse', type: 'number' },
      { k: 'commandTimeoutSeconds', label: 'Timeout (s)', type: 'number' },
    ];
    await crudView({
      title: 'Datenbankverbindungen', singular: 'Verbindung', path: '/connections', id: 'name',
      intro: 'Gespeichert in <code>connections.json</code>.',
      defaults: () => ({ host: 'localhost', port: 5432, sslMode: 'Prefer', maxPoolSize: 20, commandTimeoutSeconds: 30 }),
      columns: [
        { k: 'name', label: 'Name' },
        { label: 'Server', html: (c) => esc(`${c.username}@${c.host}:${c.port}/${c.database}`) },
        { k: 'sslMode', label: 'SSL' },
      ],
      fields,
      formButtons: (form) => [{
        label: 'Verbindung testen', action: async () => {
          const r = await admin('POST', '/connections/test', fixPassword(form.read()));
          dialogError(r.ok ? '' : r.error);
          if (r.ok) toast(`OK – PostgreSQL ${r.postgresVersion}, PostGIS ${r.postgisVersion ?? 'fehlt!'}`);
          return true;
        },
      }],
      save: (c) => admin('PUT', '/connections/' + encodeURIComponent(c.name), fixPassword(c)),
      rowActions: [
        { label: 'Testen', fn: async (c) => {
          const r = await admin('POST', `/connections/${encodeURIComponent(c.name)}/test`);
          openDialog({ title: 'Verbindungstest: ' + c.name, body: r.ok
            ? `<p class="ok">Verbindung erfolgreich.</p><p>PostgreSQL ${esc(r.postgresVersion)}<br>PostGIS ${esc(r.postgisVersion ?? '–')}</p>${r.error ? `<p class="error">${esc(r.error)}</p>` : ''}`
            : `<p class="error">${esc(r.error)}</p>` });
        } },
        { label: 'Tabellen', fn: (c) => discover(c.name) },
      ],
    });
  },

  async layers() {
    [cache.connections, cache.layers] = await Promise.all([admin('GET', '/connections'), admin('GET', '/layers')]);
    const fields = (isNew) => [
      { k: 'name', label: 'Name (URL)', readonly: !isNew },
      { k: 'title', label: 'Titel', nullable: true },
      { k: 'connection', label: 'Verbindung', type: 'select', options: () => cache.connections.map((c) => c.name) },
      { k: 'schema', label: 'Schema' },
      { k: 'table', label: 'Tabelle / View' },
      { k: 'geometryColumn', label: 'Geometriespalte' },
      { k: 'idColumn', label: 'ID-Spalte', nullable: true },
      { k: 'srid', label: 'SRID', type: 'number', hint: '0 = automatisch' },
      { k: 'properties', label: 'Attribute', type: 'list', wide: true, hint: 'Komma-getrennt, leer = alle' },
      { k: 'description', label: 'Beschreibung', type: 'textarea', rows: 2 },
      { k: 'defaultLimit', label: 'Standard-Limit', type: 'number' },
      { k: 'maxFeatures', label: 'Max. Features', type: 'number' },
      { k: 'maxTileFeatures', label: 'Max. Features pro Tile', type: 'number' },
      { k: 'readOnly', label: 'Nur lesen', type: 'checkbox' },
      { k: 'public', label: 'Öffentlich (ohne Anmeldung lesbar)', type: 'checkbox' },
      { k: 'enableTiles', label: 'Vector-Tiles aktiv', type: 'checkbox' },
    ];
    const v = await crudView({
      title: 'Layer', singular: 'Layer', path: '/layers', id: 'name',
      intro: 'Gespeichert in <code>layers.json</code>. Beim Speichern wird die Tabelle in der Datenbank geprüft.',
      defaults: () => ({ connection: cache.connections[0]?.name, schema: 'public', geometryColumn: 'geom', idColumn: 'id', srid: 0,
        properties: [], defaultLimit: 100, maxFeatures: 5000, maxTileFeatures: 10000, readOnly: true, public: false, enableTiles: true }),
      columns: [
        { k: 'name', label: 'Name' },
        { label: 'Quelle', html: (l) => esc(`${l.connection}: ${l.schema}.${l.table}.${l.geometryColumn}`) },
        { label: 'Zugriff', html: (l) => onOff(!l.readOnly, 'schreibbar', 'nur lesen') + (l.public ? '<span class="badge">öffentlich</span>' : '') },
      ],
      fields,
      save: async (l) => {
        try {
          await admin('PUT', '/layers/' + encodeURIComponent(l.name), l);
        } catch (e) {
          if (e.status !== 400 || !confirm(`Prüfung fehlgeschlagen:\n${e.message}\n\nTrotzdem speichern?`)) throw e;
          await admin('PUT', '/layers/' + encodeURIComponent(l.name) + '?force=true', l);
        }
      },
      rowActions: [{ label: 'Vorschau', fn: (l) => preview(l) }],
    });
    if (pendingLayerPrefill) { const p = pendingLayerPrefill; pendingLayerPrefill = null; v.edit(null, p); }
  },

  async queries() {
    cache.connections = await admin('GET', '/connections');
    await crudView({
      title: 'Benannte Abfragen', singular: 'Abfrage', path: '/queries', id: 'name',
      intro: 'Gespeichert in <code>queries.json</code>. Aufruf über <code>/api/v1/queries/{name}?param=wert</code>. Parameter im SQL als <code>@name</code>.',
      defaults: () => ({ connection: cache.connections[0]?.name, format: 'json', parameters: [], sql: 'SELECT 1 AS x', allowWrite: false, public: false }),
      columns: [
        { k: 'name', label: 'Name' },
        { k: 'connection', label: 'Verbindung' },
        { k: 'format', label: 'Format' },
        { label: 'Parameter', html: (q) => (q.parameters || []).map((p) => `<span class="badge">@${esc(p.name)}:${esc(p.type)}</span>`).join('') },
      ],
      fields: (isNew) => [
        { k: 'name', label: 'Name (URL)', readonly: !isNew },
        { k: 'title', label: 'Titel', nullable: true },
        { k: 'connection', label: 'Verbindung', type: 'select', options: () => cache.connections.map((c) => c.name) },
        { k: 'format', label: 'Format', type: 'select', options: ['json', 'geojson'] },
        { k: 'geometryColumn', label: 'Geometriespalte (geojson)', nullable: true },
        { k: 'idColumn', label: 'ID-Spalte (geojson)', nullable: true },
        { k: 'description', label: 'Beschreibung', type: 'textarea', rows: 2 },
        { k: 'sql', label: 'SQL (ein SELECT)', type: 'textarea', rows: 8 },
        { k: 'parameters', label: 'Parameter (JSON)', type: 'json', rows: 6,
          hint: 'z.B. [{"name":"kanton","type":"string","required":true}] – Typen: string, integer, bigint, number, boolean, date, timestamp, uuid, json, geojson' },
        { k: 'allowWrite', label: 'Schreibzugriff erlauben (sonst READ ONLY Transaktion)', type: 'checkbox' },
        { k: 'public', label: 'Öffentlich (ohne Anmeldung)', type: 'checkbox' },
      ],
      save: (q) => admin('PUT', '/queries/' + encodeURIComponent(q.name), q),
      rowActions: [{ label: 'Ausführen', fn: (q) => runQuery(q) }],
    });
  },

  async apikeys() {
    await crudView({
      title: 'API-Keys', singular: 'API-Key', path: '/apikeys', id: 'name', deleteId: (k) => k.id,
      intro: 'Gespeichert (nur als SHA-256-Hash) in <code>security.json</code>. Senden als Header <code>X-API-Key</code>.',
      onNew: createKey,
      columns: [
        { k: 'name', label: 'Name' },
        { label: 'Key', html: (k) => `<code>${esc(k.keyPrefix)}…</code>` },
        { label: 'Rollen', html: (k) => rolesHtml(k.roles) },
        { label: 'Einschränkung', html: (k) => esc([...(k.allowedLayers || []).map((l) => 'Layer ' + l), ...(k.allowedQueries || []).map((q) => 'Query ' + q)].join(', ') || 'alle') },
        { label: 'Status', html: (k) => onOff(k.enabled && (!k.expiresUtc || new Date(k.expiresUtc) > new Date()), 'aktiv', 'inaktiv') },
      ],
      fields: () => keyFields(false),
      save: (k, _isNew, orig) => admin('PUT', '/apikeys/' + orig.id, { ...k, clearExpiry: !k.expiresUtc }),
      rowActions: [{ label: 'Aktiv an/aus', fn: async (k) => {
        await admin('PUT', '/apikeys/' + k.id, { enabled: !k.enabled }); toast(k.enabled ? 'Deaktiviert' : 'Aktiviert'); render();
      } }],
    });
  },

  async oidc() {
    const o = await admin('GET', '/oidc');
    singleForm('OAuth 2.0 / OpenID Connect', 'Gespeichert in <code>security.json</code>. Clients senden <code>Authorization: Bearer &lt;access_token&gt;</code>. Claim-Werte werden auf die internen Rollen read / write / admin abgebildet.', [
      { k: 'enabled', label: 'OIDC aktiv', type: 'checkbox' },
      { k: 'authority', label: 'Authority / Issuer URL', wide: true, nullable: true, hint: 'z.B. https://login.microsoftonline.com/{tenant}/v2.0 oder https://keycloak/realms/gis' },
      { k: 'audience', label: 'Audience', nullable: true, hint: 'Leer = nicht prüfen' },
      { k: 'roleClaimType', label: 'Rollen-Claim', hint: 'z.B. roles, groups, realm_access.roles' },
      { k: 'requireHttpsMetadata', label: 'HTTPS für Metadaten erzwingen', type: 'checkbox' },
      { k: 'useScopes', label: 'Auch scope/scp-Claim auswerten', type: 'checkbox' },
      { k: 'readRoles', label: 'Claim-Werte → read', type: 'list' },
      { k: 'writeRoles', label: 'Claim-Werte → write', type: 'list' },
      { k: 'adminRoles', label: 'Claim-Werte → admin', type: 'list' },
      { k: 'defaultRoles', label: 'Rollen für jedes gültige Token', type: 'list', hint: 'z.B. read' },
    ], o, (v) => admin('PUT', '/oidc', v));
  },

  async settings() {
    const [s, conns] = await Promise.all([admin('GET', '/settings'), admin('GET', '/connections')]);
    singleForm('Allgemeine Einstellungen', 'Gespeichert in <code>settings.json</code>.', [
      { k: 'defaultConnection', label: 'Standard-Verbindung (Geometrie-Operationen)', type: 'select', options: ['', ...conns.map((c) => c.name)] },
      { k: 'corsOrigins', label: 'CORS-Origins', type: 'list', wide: true, hint: 'z.B. https://www.example.ch – * = alle' },
      { k: 'tileCacheSeconds', label: 'Tile Cache-Control (s)', type: 'number' },
      { k: 'allowApiKeyInQuery', label: 'API-Key als ?api_key= erlauben (für Karten-Clients)', type: 'checkbox' },
      { k: 'enableSwaggerUi', label: 'Swagger UI aktiv', type: 'checkbox' },
      { k: 'detailedErrors', label: 'Detaillierte Datenbankfehler zurückgeben', type: 'checkbox' },
    ], s, (v) => admin('PUT', '/settings', v));
  },
};

function fixPassword(c) { if (!c.password) c.password = '********'; if (!c.passwordEnvVar) c.passwordEnvVar = null; return c; }

function keyFields(isNew) {
  return [
    { k: 'name', label: 'Name / Verwendung' },
    { k: 'roles', label: 'Rollen', type: 'list', hint: 'read, write, admin' },
    { k: 'allowedLayers', label: 'Nur diese Layer', type: 'list', hint: 'leer = alle' },
    { k: 'allowedQueries', label: 'Nur diese Abfragen', type: 'list', hint: 'leer = alle' },
    { k: 'expiresUtc', label: 'Gültig bis', type: 'datetime', hint: 'leer = unbegrenzt' },
    ...(isNew ? [] : [{ k: 'enabled', label: 'Aktiv', type: 'checkbox' }]),
  ];
}

function createKey() {
  const form = buildForm(keyFields(true), { name: '', roles: ['read'], allowedLayers: [], allowedQueries: [] });
  openDialog({
    title: 'Neuer API-Key', body: form, buttons: [{ label: 'Abbrechen' }, {
      label: 'Erstellen', cls: 'primary', action: async () => {
        const r = await admin('POST', '/apikeys', form.read());
        render();
        setTimeout(() => openDialog({
          title: 'API-Key erstellt',
          body: `<p>Der Key wird <b>nur jetzt</b> angezeigt. Bitte sicher aufbewahren:</p><pre class="secret">${esc(r.key)}</pre>`,
          buttons: [{ label: 'Kopieren', action: async () => { await navigator.clipboard.writeText(r.key); toast('Kopiert'); return true; } }, { label: 'Schliessen', cls: 'primary' }],
        }), 50);
      },
    }],
  });
}

function singleForm(title, intro, fields, obj, save) {
  const view = $('#view');
  view.innerHTML = `<div class="card"><h2>${esc(title)}</h2><p class="muted">${intro}</p><div id="sf"></div>
    <p class="error" id="sfError"></p><div class="actions"><button class="primary" id="sfSave">Speichern</button></div></div>`;
  const form = buildForm(fields, obj);
  $('#sf').append(form);
  $('#sfSave').onclick = async () => {
    $('#sfError').textContent = '';
    try { await save(form.read()); toast('Gespeichert'); } catch (e) { $('#sfError').textContent = e.message; }
  };
}

async function discover(connection) {
  const tables = await admin('GET', `/connections/${encodeURIComponent(connection)}/tables`);
  const wrap = document.createElement('div');
  wrap.innerHTML = tables.length
    ? `<div class="table-wrap"><table><thead><tr><th>Tabelle</th><th>Geometrie</th><th>SRID</th><th>PK</th><th></th></tr></thead><tbody></tbody></table></div>`
    : '<p class="muted">Keine Geometrie-Tabellen gefunden.</p>';
  for (const t of tables) {
    const tr = document.createElement('tr');
    tr.innerHTML = `<td>${esc(t.schema)}.${esc(t.table)}${t.isView ? ' <span class="badge">View</span>' : ''}</td>
      <td>${esc(t.geometryColumn)} <span class="muted">${esc(t.geometryType)}</span></td><td>${t.srid}</td><td>${esc(t.primaryKey ?? '–')}</td>`;
    const td = document.createElement('td'); td.className = 'actions';
    const b = document.createElement('button'); b.textContent = 'Als Layer';
    b.onclick = () => {
      pendingLayerPrefill = { connection, schema: t.schema, table: t.table, geometryColumn: t.geometryColumn,
        idColumn: t.primaryKey, name: t.table.toLowerCase().replace(/[^a-z0-9_-]/g, '_'), title: t.table };
      $('#dialog').close();
      switchTab('layers');
    };
    td.append(b); tr.append(td);
    $('tbody', wrap)?.append(tr);
  }
  openDialog({ title: `Geometrie-Tabellen in ${connection}`, body: wrap });
}

async function preview(layer) {
  const body = document.createElement('div');
  body.innerHTML = '<div id="map"></div><p class="muted" id="pvInfo">Lade…</p><pre id="pvJson"></pre>';
  openDialog({ title: 'Vorschau: ' + layer.name, body });
  try {
    const [meta, fc] = await Promise.all([
      call('GET', `/api/v1/layers/${encodeURIComponent(layer.name)}`),
      call('GET', `/api/v1/layers/${encodeURIComponent(layer.name)}/features?limit=200`),
    ]);
    $('#pvInfo').textContent = `SRID ${meta.srid}, ${meta.geometryType}, ${fc.numberReturned} Features geladen (max. 200). Attribute: ${meta.properties.map((p) => p.name).join(', ')}`;
    $('#pvJson').textContent = JSON.stringify(fc.features.slice(0, 3), null, 2);
    if (window.L) {
      const map = L.map('map');
      L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '&copy; OpenStreetMap' }).addTo(map);
      const gj = L.geoJSON(fc, { onEachFeature: (f, l) => l.bindPopup(`<pre>${esc(JSON.stringify(f.properties, null, 1))}</pre>`) }).addTo(map);
      const b = gj.getBounds();
      b.isValid() ? map.fitBounds(b, { maxZoom: 16 }) : map.setView([46.8, 8.2], 7);
      // the dialog may still be laying out: recompute the map size once it has settled
      setTimeout(() => { map.invalidateSize(); if (b.isValid()) map.fitBounds(b, { maxZoom: 16 }); }, 150);
    }
  } catch (e) { $('#pvInfo').textContent = ''; dialogError(e.message); }
}

function runQuery(q) {
  const params = Object.fromEntries((q.parameters || []).map((p) => [p.name, p.default ?? '']));
  const form = buildForm((q.parameters || []).map((p) => ({ k: p.name, label: `${p.name} (${p.type})${p.required ? ' *' : ''}`, hint: p.description })), params);
  const out = document.createElement('pre');
  const body = document.createElement('div');
  body.append(form, out);
  openDialog({
    title: 'Abfrage ausführen: ' + q.name, body, buttons: [{ label: 'Schliessen' }, {
      label: 'Ausführen', cls: 'primary', action: async () => {
        const values = Object.fromEntries(Object.entries(form.read()).filter(([, v]) => v !== '' && v !== null));
        const r = await call('POST', `/api/v1/queries/${encodeURIComponent(q.name)}`, values);
        out.textContent = JSON.stringify(r, null, 2).slice(0, 20000);
        return true;
      },
    }],
  });
}

// ------------------------------------------------------------------ app shell
let currentTab = 'connections';
function switchTab(tab) {
  currentTab = tab;
  document.querySelectorAll('#tabs button').forEach((b) => b.classList.toggle('active', b.dataset.tab === tab));
  render();
}
async function render() {
  try { await views[currentTab](); } catch (e) { $('#view').innerHTML = `<div class="card"><p class="error">${esc(e.message)}</p></div>`; }
}
function showApp(on) {
  $('#login').hidden = on; $('#view').hidden = !on; $('#tabs').hidden = !on; $('#logout').hidden = !on;
}
function logout() { setKey(null); showApp(false); }

document.querySelectorAll('#tabs button').forEach((b) => (b.onclick = () => switchTab(b.dataset.tab)));
$('#logout').onclick = logout;
$('#loginForm').onsubmit = async (e) => {
  e.preventDefault();
  setKey($('#loginKey').value.trim());
  $('#loginError').textContent = '';
  try {
    const me = await admin('GET', '/whoami');
    if (!me) throw new Error('Anmeldung fehlgeschlagen');
    $('#loginKey').value = '';
    showApp(true); render();
  } catch (err) {
    setKey(null);
    $('#loginError').textContent = err.status === 403 ? 'Dieser Key hat keine Admin-Rolle.' : err.message;
  }
};

if (getKey()) admin('GET', '/whoami').then(() => { showApp(true); render(); }).catch(() => logout());
