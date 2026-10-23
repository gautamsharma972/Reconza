'use strict';
const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const fmtSize = b => b < 1024 * 1024 ? (b / 1024).toFixed(1) + ' KB' : (b / 1024 / 1024).toFixed(1) + ' MB';

// ---- Confirm destructive actions: <button data-confirm="…">
document.addEventListener('submit', e => {
  const msg = e.submitter?.dataset.confirm || e.target.dataset.confirm;
  if (msg && !confirm(msg)) { e.preventDefault(); return; }
  // Busy state: buttons with data-busy show a spinner and block double submits.
  const form = e.target;
  if (form.matches('[data-busy-form]') && !e.defaultPrevented) {
    const btn = e.submitter;
    if (form.dataset.submitting) { e.preventDefault(); return; }
    form.dataset.submitting = '1';
    if (btn?.dataset.busy) { btn.dataset.label = btn.innerHTML; btn.innerHTML = '<span class="btn-spinner"></span>' + btn.dataset.busy; btn.classList.add('is-busy'); }
    setTimeout(() => { delete form.dataset.submitting; }, 120000);
  }
});

// ---- Nav toggle (mobile) and toasts
$('[data-nav-toggle]')?.addEventListener('click', e => {
  const open = document.body.classList.toggle('nav-open');
  e.currentTarget.setAttribute('aria-expanded', open);
});
$$('[data-toast]').forEach(t => {
  const close = () => t.classList.add('toast-hide');
  $('[data-toast-close]', t)?.addEventListener('click', close);
  setTimeout(close, 6000);
});

// ---- Client-side table filter: <input data-filter="#tableId">
$$('[data-filter]').forEach(input => {
  const table = $(input.dataset.filter);
  input.addEventListener('input', () => {
    const q = input.value.toLowerCase();
    $$('tbody tr', table).forEach(tr => tr.hidden = q && !tr.textContent.toLowerCase().includes(q));
  });
});

// ---- Clickable table rows
$$('table.clickable tbody tr[data-href]').forEach(tr => tr.addEventListener('click', e => {
  if (!e.target.closest('a, button, input, select, label, form')) location.href = tr.dataset.href;
}));

// ---- Tabs
const showTab = name => {
  const buttons = $$('[data-tab]');
  if (!buttons.some(b => b.dataset.tab === name)) name = buttons[0]?.dataset.tab;
  buttons.forEach(b => b.classList.toggle('active', b.dataset.tab === name));
  $$('[data-panel]').forEach(p => p.hidden = p.dataset.panel !== name);
};
if ($('.tabs')) {
  $$('[data-tab]').forEach(b => b.addEventListener('click', () => { showTab(b.dataset.tab); history.replaceState(null, '', '#' + b.dataset.tab); }));
  showTab(location.hash.slice(1));
  $$('[data-goto-tab]').forEach(a => a.addEventListener('click', e => { e.preventDefault(); showTab(a.dataset.gotoTab); history.replaceState(null, '', '#' + a.dataset.gotoTab); $('.tabs').scrollIntoView({ behavior: 'smooth' }); }));
}

// ---- Exception filters + per-type help
const exRows = () => $$('#exceptions tbody tr');
const applyExFilter = (type, statusOnly) => {
  exRows().forEach(tr => tr.hidden = (type !== '*' && tr.dataset.type !== type) || (statusOnly && tr.dataset.status !== 'Open'));
  $$('[data-help]').forEach(h => h.hidden = h.dataset.help !== type);
};
$$('[data-type-filter]').forEach(chip => chip.addEventListener('click', () => {
  $$('[data-type-filter],[data-status-filter]').forEach(c => c.classList.toggle('active', c === chip));
  applyExFilter(chip.dataset.typeFilter, false);
}));
$$('[data-status-filter]').forEach(chip => chip.addEventListener('click', () => {
  $$('[data-type-filter],[data-status-filter]').forEach(c => c.classList.toggle('active', c === chip));
  applyExFilter('*', true);
}));

// Keeps "N open" badges and the review progress card in sync after inline decisions.
function refreshReviewCounts() {
  const rows = exRows(), open = rows.filter(r => r.dataset.status === 'Open').length;
  $$('[data-open-count]').forEach(el => el.textContent = open);
  const count = $('[data-review-count]'), bar = $('[data-review-bar]');
  if (count) { count.textContent = `${rows.length - open}/${rows.length}`; count.classList.toggle('ok-ink', open === 0); count.classList.toggle('warn-ink', open > 0); }
  if (bar) { bar.style.width = (rows.length ? (rows.length - open) * 100 / rows.length : 100) + '%'; bar.classList.toggle('progress-ok', open === 0); }
}

// ---- Inline exception review: saves on change without reloading
$$('form[data-review]').forEach(form => {
  const meta = form.parentElement.querySelector('[data-meta]');
  const row = form.closest('tr');
  let timer;
  const save = async () => {
    meta.textContent = 'Saving…';
    try {
      const res = await fetch(form.action, { method: 'POST', body: new FormData(form), headers: { 'X-Requested-With': 'fetch' } });
      if (!res.ok) throw new Error(res.status);
      const data = await res.json();
      row.dataset.status = data.status;
      row.classList.toggle('is-reviewed', data.status !== 'Open');
      meta.textContent = '✓ Saved · ' + data.by;
      refreshReviewCounts();
    } catch { meta.textContent = 'Could not save — check your connection and try again.'; }
  };
  $$('input[type=radio]', form).forEach(r => r.addEventListener('change', save));
  $('input.note', form)?.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(save, 700); });
  form.addEventListener('submit', e => { e.preventDefault(); save(); });
});

// ---- Run progress polling
const progress = $('[data-run-progress]');
if (progress) {
  const stage = $('[data-stage]', progress), bar = $('[data-bar]', progress);
  const poll = async () => {
    try {
      const res = await fetch(progress.dataset.statusUrl, { headers: { 'Accept': 'application/json' } });
      const s = await res.json();
      if (s.status === 'Completed' || s.status === 'Failed') { location.reload(); return; }
      if (s.status === 'Processing') { stage.textContent = s.stage || 'Processing…'; bar.style.width = Math.max(s.progress, 5) + '%'; }
    } catch { /* transient; keep polling */ }
    setTimeout(poll, 1200);
  };
  setTimeout(poll, 800);
}

// ---- Single-file dropzones (e.g. sample file on the mapping page)
$$('[data-dropzone]').forEach(zone => {
  const input = $('input[type=file]', zone), label = $('.dz-file', zone);
  ['dragenter', 'dragover'].forEach(ev => zone.addEventListener(ev, e => { e.preventDefault(); zone.classList.add('is-over'); }));
  ['dragleave', 'drop'].forEach(ev => zone.addEventListener(ev, () => zone.classList.remove('is-over')));
  zone.addEventListener('drop', e => { e.preventDefault(); input.files = e.dataTransfer.files; input.dispatchEvent(new Event('change')); });
  input.addEventListener('change', () => {
    const f = input.files[0];
    label.textContent = f ? `${f.name} · ${fmtSize(f.size)}` : '';
    zone.classList.toggle('has-file', !!f);
    if (f && input.dataset.autosubmit) {
      const btn = $$('button[formaction]', input.form).find(b => b.formAction.includes('handler=' + input.dataset.autosubmit));
      btn?.click();
    }
  });
});

// ---- Multi-file run upload: auto-assign dropped files to source slots
const multi = $('[data-multi-drop]');
if (multi) {
  const slots = $$('[data-slot]');
  const status = $('[data-slot-status]');
  const submit = $('[data-submit-run]');
  const tokens = s => s.toLowerCase().replace(/\.[a-z0-9]+$/, '').split(/[^a-z0-9]+/).filter(t => t.length > 1)
    .map(t => t.replace(/s$/, ''));

  const setFile = (slot, file) => {
    const input = $('[data-slot-input]', slot);
    const dt = new DataTransfer();
    if (file) dt.items.add(file);
    input.files = dt.files;
    render(slot);
  };
  const render = slot => {
    const f = $('[data-slot-input]', slot).files[0];
    $('.slot-empty', slot).hidden = !!f;
    $('.slot-file', slot).hidden = !f;
    $('[data-clear]', slot).hidden = !f;
    slot.classList.toggle('has-file', !!f);
    if (f) { $('[data-name]', slot).textContent = f.name; $('[data-size]', slot).textContent = fmtSize(f.size); }
    const filled = slots.filter(s => $('[data-slot-input]', s).files.length).length;
    status.textContent = filled === slots.length ? 'All files ready' : `${filled} of ${slots.length} files added`;
    submit.disabled = filled !== slots.length || submit.dataset.blocked === 'true';
  };
  const score = (file, slot) => {
    const ft = tokens(file.name);
    const hints = JSON.parse(slot.dataset.hints);
    let best = 0;
    hints.forEach((h, i) => {
      const ht = tokens(h);
      const overlap = ft.filter(t => ht.some(x => x === t || (t.length > 3 && (x.startsWith(t) || t.startsWith(x))))).length;
      best = Math.max(best, overlap * (i < 2 ? 3 : 1)); // source name & last file name weigh more than column names
    });
    return best;
  };
  const assign = files => {
    const pending = [...files];
    const free = () => slots.filter(s => !$('[data-slot-input]', s).files.length);
    // Best-scoring pairs first, then fill remaining slots in order.
    const pairs = pending.flatMap(f => slots.map(s => ({ f, s, sc: score(f, s) }))).filter(p => p.sc > 0).sort((a, b) => b.sc - a.sc);
    const used = new Set();
    pairs.forEach(p => { if (!used.has(p.f) && free().includes(p.s)) { setFile(p.s, p.f); used.add(p.f); } });
    pending.filter(f => !used.has(f)).forEach(f => { const s = free()[0]; if (s) setFile(s, f); });
    slots.forEach(s => s.classList.add('flash'));
    setTimeout(() => slots.forEach(s => s.classList.remove('flash')), 600);
  };

  const input = $('[data-multi-input]', multi);
  input.addEventListener('change', () => { assign(input.files); input.value = ''; });
  ['dragenter', 'dragover'].forEach(ev => multi.addEventListener(ev, e => { e.preventDefault(); multi.classList.add('is-over'); }));
  ['dragleave', 'drop'].forEach(ev => multi.addEventListener(ev, () => multi.classList.remove('is-over')));
  multi.addEventListener('drop', e => { e.preventDefault(); assign(e.dataTransfer.files); });

  slots.forEach(slot => {
    const si = $('[data-slot-input]', slot);
    si.addEventListener('change', () => render(slot));
    $('[data-clear]', slot).addEventListener('click', () => setFile(slot, null));
    ['dragenter', 'dragover'].forEach(ev => slot.addEventListener(ev, e => { e.preventDefault(); e.stopPropagation(); slot.classList.add('is-over'); }));
    ['dragleave', 'drop'].forEach(ev => slot.addEventListener(ev, () => slot.classList.remove('is-over')));
    slot.addEventListener('drop', e => { e.preventDefault(); e.stopPropagation(); if (e.dataTransfer.files[0]) setFile(slot, e.dataTransfer.files[0]); });
    render(slot);
  });
}

// ---- Mapping page: live sample values under each chosen column
const preview = $('#preview-data');
if (preview) {
  const data = JSON.parse(preview.textContent);
  const update = input => {
    const cell = input.closest('tr').querySelector('[data-samples]');
    const i = data.headers.findIndex(h => h.toLowerCase() === input.value.trim().toLowerCase());
    cell.textContent = i < 0 ? (input.value ? 'column not in sample' : '') : data.rows.map(r => r[i]).filter(v => v !== '').slice(0, 3).join(' · ') || '(empty)';
    cell.classList.toggle('warn-ink', i < 0 && !!input.value);
  };
  $$('[data-map-input]').forEach(inp => { update(inp); inp.addEventListener('input', () => update(inp)); });
}

// ---- Print
$$('[data-print]').forEach(b => b.addEventListener('click', () => window.print()));
