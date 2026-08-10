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

export function copyText(text) {
  return navigator.clipboard.writeText(text);
}

// One dialog at a time (the shell enforces that), so module-level trap state suffices.
let trapped = null;
let restoreTo = null;
let trapHandler = null;

const FOCUSABLE = 'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])';

export function trapFocus(el) {
  releaseFocus();
  trapped = el;
  restoreTo = document.activeElement;
  const first = el.querySelector(FOCUSABLE);
  if (first) first.focus();
  trapHandler = e => {
    if (e.key !== 'Tab') return;
    const items = [...el.querySelectorAll(FOCUSABLE)].filter(i => !i.disabled && i.offsetParent !== null);
    if (items.length === 0) return;
    const firstItem = items[0];
    const lastItem = items[items.length - 1];
    if (e.shiftKey && document.activeElement === firstItem) { lastItem.focus(); e.preventDefault(); }
    else if (!e.shiftKey && document.activeElement === lastItem) { firstItem.focus(); e.preventDefault(); }
  };
  el.addEventListener('keydown', trapHandler);
}

export function releaseFocus() {
  if (trapped && trapHandler) trapped.removeEventListener('keydown', trapHandler);
  if (restoreTo && typeof restoreTo.focus === 'function') restoreTo.focus();
  trapped = null;
  restoreTo = null;
  trapHandler = null;
}
