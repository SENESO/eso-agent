/* EsoAgent shared components (JS) — in-page dialogs and toasts replace every
   native browser dialog; formatters keep times and sizes human.
   Synced verbatim into Fleet.html and Dashboard.html by
   scripts/sync-web-assets.py; edit here, never in the HTML copies. */
function esoEl(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}
/* Generic modal. spec: {title, message, fields:[{id,label,type,value,options,checked}],
   actions:[{id,label,kind}]} -> Promise<{action, values} | null> (null = dismissed). */
function esoDialog(spec) {
  return new Promise(resolve => {
    const back = esoEl('div', 'dlg-back');
    const dlg = esoEl('div', 'dlg');
    dlg.setAttribute('role', 'dialog');
    dlg.setAttribute('aria-modal', 'true');
    dlg.appendChild(esoEl('h2', '', spec.title || ''));
    if (spec.message) dlg.appendChild(esoEl('p', 'dlg-msg', spec.message));
    const err = esoEl('div', 'dlg-err');
    const inputs = {};
    (spec.fields || []).forEach(f => {
      if (f.type === 'checks') {
        const wrap = esoEl('div', 'checks');
        f.options.forEach(o => {
          const lab = esoEl('label');
          const cb = document.createElement('input');
          cb.type = 'checkbox'; cb.checked = !!o.checked; cb.dataset.v = o.value;
          lab.appendChild(cb); lab.appendChild(document.createTextNode(' ' + o.label));
          wrap.appendChild(lab);
        });
        inputs[f.id] = wrap;
        dlg.appendChild(wrap);
        return;
      }
      if (f.label) { const l = esoEl('label', 'f', f.label); l.htmlFor = 'dlg-' + f.id; dlg.appendChild(l); }
      let inp;
      if (f.type === 'select') {
        inp = document.createElement('select');
        (f.options || []).forEach(o => { const op = esoEl('option', '', o.label); op.value = o.value; inp.appendChild(op); });
        inp.value = f.value ?? '';
      } else if (f.type === 'textarea') {
        inp = document.createElement('textarea'); inp.value = f.value ?? '';
      } else {
        inp = document.createElement('input');
        inp.type = f.type || 'text'; inp.value = f.value ?? '';
        if (f.placeholder) inp.placeholder = f.placeholder;
      }
      inp.id = 'dlg-' + f.id;
      inputs[f.id] = inp;
      dlg.appendChild(inp);
    });
    dlg.appendChild(err);
    const row = esoEl('div', 'dlg-actions');
    let done = false;
    const close = val => { if (done) return; done = true; back.remove(); document.removeEventListener('keydown', onKey); resolve(val); };
    const collect = () => {
      const values = {};
      for (const [id, inp] of Object.entries(inputs)) {
        if (inp.classList && inp.classList.contains('checks')) values[id] = [...inp.querySelectorAll('input:checked')].map(c => c.dataset.v);
        else values[id] = inp.value;
      }
      return values;
    };
    (spec.actions || [{id: 'ok', label: 'OK', kind: 'primary'}]).forEach(a => {
      const b = esoEl('button', 'act' + (a.kind === 'primary' ? ' primary' : '') + (a.kind === 'danger' ? ' danger' : ''), a.label);
      b.onclick = () => {
        const values = collect();
        if (spec.validate) { const msg = spec.validate(values); if (msg) { err.textContent = msg; return; } }
        close({action: a.id, values});
      };
      row.appendChild(b);
    });
    dlg.appendChild(row);
    back.appendChild(dlg);
    back.addEventListener('mousedown', e => { if (e.target === back) close(null); });
    const onKey = e => { if (e.key === 'Escape') close(null); };
    document.addEventListener('keydown', onKey);
    document.body.appendChild(back);
    const first = dlg.querySelector('input:not([type=checkbox]), textarea, select');
    (first || dlg.querySelector('button')).focus();
  });
}
/* Confirm dialog -> Promise<boolean>. danger=true styles the confirm action. */
async function esoConfirm(title, message, confirmLabel, danger) {
  const r = await esoDialog({title, message, actions: [
    {id: 'no', label: 'Cancel'},
    {id: 'yes', label: confirmLabel || 'Confirm', kind: danger ? 'danger' : 'primary'},
  ]});
  return !!r && r.action === 'yes';
}
/* Single-text-field dialog -> Promise<string|null> (null = cancelled). */
async function esoTextDialog(title, label, initial, opts = {}) {
  const r = await esoDialog({title, message: opts.message, fields: [{id: 'v', label, value: initial ?? '', placeholder: opts.placeholder}],
    validate: opts.required ? v => (v.v.trim() ? '' : (opts.requiredMsg || 'A value is required.')) : null,
    actions: [{id: 'no', label: 'Cancel'}, {id: 'yes', label: opts.okLabel || 'Save', kind: 'primary'}]});
  return r && r.action === 'yes' ? r.values.v : null;
}
/* Non-blocking result toast (replaces the blocking alert box). kind: '' | 'ok' | 'bad'. */
function esoToast(text, kind = '') {
  let host = document.getElementById('toasts');
  if (!host) { host = esoEl('div'); host.id = 'toasts'; document.body.appendChild(host); }
  const t = esoEl('div', 'toast ' + kind, text);
  host.appendChild(t);
  setTimeout(() => { t.style.opacity = '0'; setTimeout(() => t.remove(), 250); }, 5200);
}
/* Human sizes and times (G20: no raw bytes, no raw timestamps). */
function fmtBytes(n) {
  if (n == null || n === '') return '—';
  n = Number(n); if (isNaN(n)) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0;
  while (n >= 1024 && i < 4) { n /= 1024; i++; }
  return n.toFixed(1) + ' ' + u[i];
}
function fmtTime(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d)) return String(iso);
  const s = Math.floor((Date.now() - d.getTime()) / 1000);
  let rel;
  if (s < 45) rel = 'just now';
  else if (s < 3600) rel = Math.floor(s / 60) + ' min ago';
  else if (s < 86400) rel = Math.floor(s / 3600) + ' h ago';
  else rel = Math.floor(s / 86400) + ' d ago';
  const abs = d.toLocaleString();
  return `<span title="${abs.replace(/"/g, '&quot;')}">${rel}</span>`;
}
/* Render a JSON value as a key/value table (device status, doctor, diagnose…).
   Returns an HTML string; nested objects flatten with dotted keys. */
function kvRows(v, prefix, out) {
  if (v && typeof v === 'object' && !Array.isArray(v)) {
    const keys = Object.keys(v);
    if (!keys.length) out.push([prefix || '—', '—']);
    keys.forEach(k => kvRows(v[k], prefix ? prefix + '.' + k : k, out));
  } else if (Array.isArray(v)) {
    if (!v.length) out.push([prefix, '—']);
    else if (v.every(x => x === null || typeof x !== 'object')) out.push([prefix, v.join(', ')]);
    else v.forEach((x, i) => kvRows(x, prefix + '[' + i + ']', out));
  } else {
    out.push([prefix, v === null || v === undefined ? '—' : String(v)]);
  }
  return out;
}
function kvTable(v) {
  const rows = kvRows(v, '', []);
  if (!rows.length) return '<div class="empty">No data.</div>';
  return '<div class="tablewrap"><table><tbody>' + rows.map(r =>
    `<tr><td style="width:38%;color:var(--mut);font-weight:600">${esc(r[0])}</td><td>${esc(r[1])}</td></tr>`).join('') + '</tbody></table></div>';
}
