// The console's whole JS interop surface (design handoff § Porting notes): rail resize with
// persisted width, theme toggle with persisted choice, clipboard copy, and a focus trap for
// the owned dialogs. Everything else is Blazor.

const RAIL_KEY = 'aspireldap.railWidth';
const THEME_KEY = 'aspireldap.theme';
const RAIL_DEFAULT = 292;
const RAIL_MIN = 180;
const RAIL_MAX = 560;

function setRail(px, persist) {
  const w = Math.max(RAIL_MIN, Math.min(RAIL_MAX, px));
  document.documentElement.style.setProperty('--rail-w', w + 'px');
  if (persist) {
    try { localStorage.setItem(RAIL_KEY, String(w)); } catch { /* storage unavailable */ }
  }
  return w;
}

function railWidth() {
  const raw = getComputedStyle(document.documentElement).getPropertyValue('--rail-w');
  return parseInt(raw, 10) || RAIL_DEFAULT;
}

export function initRail(el) {
  let saved = 0;
  try { saved = parseInt(localStorage.getItem(RAIL_KEY) || '', 10) || 0; } catch { saved = 0; }
  if (saved) setRail(saved, false);

  let startX = 0;
  let startW = 0;

  el.addEventListener('pointerdown', e => {
    startX = e.clientX;
    startW = railWidth();
    el.setPointerCapture(e.pointerId);
    el.setAttribute('aria-pressed', 'true');
    document.body.style.userSelect = 'none';
  });
  el.addEventListener('pointermove', e => {
    if (el.getAttribute('aria-pressed') !== 'true') return;
    setRail(startW + (e.clientX - startX), false);
  });
  const stop = e => {
    if (el.getAttribute('aria-pressed') !== 'true') return;
    el.removeAttribute('aria-pressed');
    document.body.style.userSelect = '';
    setRail(railWidth(), true);
    if (e.pointerId !== undefined && el.hasPointerCapture(e.pointerId)) el.releasePointerCapture(e.pointerId);
  };
  el.addEventListener('pointerup', stop);
  el.addEventListener('pointercancel', stop);
  el.addEventListener('dblclick', () => {
    setRail(RAIL_DEFAULT, false);
    try { localStorage.removeItem(RAIL_KEY); } catch { /* storage unavailable */ }
  });
  el.addEventListener('keydown', e => {
    if (e.key === 'ArrowLeft') { setRail(railWidth() - 16, true); e.preventDefault(); }
    if (e.key === 'ArrowRight') { setRail(railWidth() + 16, true); e.preventDefault(); }
  });
}

// The boot script in App.razor stamps data-theme before first paint; this toggle just flips
// and persists it. Returns the new mode so the caller can update its button.
export function toggleTheme() {
  const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  document.documentElement.dataset.theme = next;
  try { localStorage.setItem(THEME_KEY, next); } catch { /* storage unavailable */ }
  return next;
}

export function currentTheme() {
  return document.documentElement.dataset.theme || 'light';
}

// Never throws: a rejection here used to escape the Blazor event handler and kill the
// circuit (#119). Rejections are real even on localhost (document not focused,
// permissions policy); a missing navigator.clipboard (non-secure origins) reports the
// same clean false — no legacy execCommand fallback by decision, callers toast it.
export async function copyText(text) {
  try {
    if (!navigator.clipboard?.writeText) return false;
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

// The toast is a manual popover so it joins the top layer above any open modal dialog
// (#117): promotion order decides stacking, and the toast is always promoted after the
// dialog it must overlay. Idempotent — Blazor calls this on every render while a toast
// is up, and the element re-enters the DOM on each new message.
export function showToastPopover(el) {
  if (el?.isConnected && !el.matches(':popover-open')) el.showPopover();
}

// ── Modal dialogs (#117) ─────────────────────────────────────────────────────
// The <dialog> element + showModal() owns modality: top layer, background inertness,
// Escape as a "cancel" close request, ::backdrop. This module only relays close requests
// and backdrop clicks to .NET — the server decides whether the dialog actually closes,
// and its Busy guard is authoritative; the data-busy checks here are UX-latency cover.
// One dialog at a time (the shell enforces that), so module-level state suffices.
let dialog = null;
let restoreTo = null;
let netRef = null;
let downOutside = false;

const busy = () => dialog?.dataset.busy === 'true';
// An open picker list claims Escape: the picker's own Blazor handler closes the list,
// and only the next Escape reaches the dialog.
const comboOpen = () => !!dialog?.querySelector('[role="combobox"][aria-expanded="true"]');
// Backdrop test by coordinates, not event target: clicks on the panel's own padding also
// target the dialog element, and getBoundingClientRect() excludes ::backdrop.
const outside = e => {
  const r = dialog.getBoundingClientRect();
  return e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom;
};
const relay = () => {
  try { netRef?.invokeMethodAsync('CancelFromJs'); } catch { /* component disposed */ }
};

export function openModal(el, dotnetRef) {
  closeModal();
  dialog = el;
  netRef = dotnetRef;
  restoreTo = document.activeElement;
  el.showModal();
  // Focus the panel itself, not its first field: showModal's default focusing steps pick
  // the first focusable control, and programmatic focus on an input triggers
  // :focus-visible in Chromium — the ring flashing on dialog open reads as a glitch
  // (#109). The panel carries tabindex="-1"; Tab moves into the first control normally.
  el.focus({ preventScroll: true });
  el.addEventListener('cancel', e => {
    e.preventDefault(); // .NET owns closing; the element never closes itself
    if (!busy() && !comboOpen()) relay();
  });
  // CloseWatcher anti-abuse: a second Escape without fresh user activation (Escape grants
  // none) skips "cancel" and closes outright. Reopen while busy; relay otherwise.
  el.addEventListener('close', () => {
    if (busy()) requestAnimationFrame(() => { if (dialog?.isConnected) dialog.showModal(); });
    else relay();
  });
  // A backdrop click cancels only when the interaction STARTED on the backdrop too:
  // drag-selecting text in a field and releasing past the panel edge fires click at the
  // dialog element (common ancestor) and must not discard the user's input.
  el.addEventListener('pointerdown', e => { downOutside = outside(e); });
  el.addEventListener('click', e => {
    if (downOutside && outside(e) && !busy()) relay();
    downOutside = false;
  });
}

export function closeModal() {
  // Works from module state: on the Blazor unmount path the element is already out of the
  // DOM when the dispose interop runs, so native close/focus-restore never happens.
  if (dialog?.isConnected) {
    netRef = null; // the close event below must not relay back into a disposing component
    dialog.close();
  }
  if (restoreTo && typeof restoreTo.focus === 'function') restoreTo.focus();
  dialog = null;
  restoreTo = null;
  netRef = null;
}
