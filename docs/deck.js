const $ = (id) => document.getElementById(id);
const slides = Array.from(document.querySelectorAll('.slide'));
const publicSlideCount = slides.filter((s) => s.dataset.secret !== 'true').length;
const progress = $('progress');
const counter = $('counter');
const prevBtn = $('prevBtn');
const nextBtn = $('nextBtn');
const notesDrawer = $('notesDrawer');
const notesText = $('notesText');
const themeSelect = $('themeSelect');
const mobileThemeSelect = $('mobileThemeSelect');
const mobileCounter = $('mobileCounter');
const themeColorMeta = $('themeColorMeta');
const overviewOverlay = $('overviewOverlay');
const overviewGrid = $('overviewGrid');
const prefersDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
const themes = prefersDark
  ? ['flash-gordon', 'blue-skies', 'night-flight', 'flight-manual']
  : ['blue-skies', 'flash-gordon', 'flight-manual', 'night-flight'];

let index = 0;
let secretHintTimer = null;

function updateSecretHint(isLastPublicSlide) {
  clearTimeout(secretHintTimer);
  counter.classList.remove('is-secret-hint');
  if (mobileCounter) mobileCounter.classList.remove('is-secret-hint');
  if (!isLastPublicSlide) return;
  secretHintTimer = setTimeout(() => {
    if (index === publicSlideCount - 1) {
      counter.classList.add('is-secret-hint');
      if (mobileCounter) mobileCounter.classList.add('is-secret-hint');
    }
  }, 5000);
}

function animateSlide(slide) {
  // Animate horizontal bars from 0% to target width
  slide.querySelectorAll('.bar-fill').forEach((bar) => {
    const target = bar.dataset.width || bar.style.width;
    bar.style.width = '0%';
    requestAnimationFrame(() => requestAnimationFrame(() => {
      bar.style.width = target;
    }));
  });

  // Subtle count-up animation for hero numbers
  slide.querySelectorAll('[data-count]').forEach((el) => {
    const target = parseInt(el.dataset.count, 10);
    const suffix = el.dataset.suffix || '';
    const useComma = el.dataset.comma === 'true';
    const duration = 450;
    const start = performance.now();
    function tick(now) {
      const t = Math.min(1, (now - start) / duration);
      const eased = 1 - Math.pow(1 - t, 3);
      const val = Math.round(target * eased);
      el.textContent = (useComma ? val.toLocaleString() : val) + suffix;
      if (t < 1) requestAnimationFrame(tick);
    }
    requestAnimationFrame(tick);
  });
}

function buildOverview() {
  overviewGrid.innerHTML = '';

  const makeCard = (s, idx, actKey, actLabel) => {
    const isHeader = Boolean(s.dataset.act);
    const title = s.dataset.title || `Slide ${idx + 1}`;
    const icon = s.dataset.icon || '📄';
    const card = document.createElement('button');
    card.dataset.act = actKey;
    card.className = 'overview-card' + (isHeader ? ' is-act' : '') + (idx === index ? ' current' : '');

    const numLabel = isHeader ? actLabel : `Slide ${idx + 1}`;
    card.innerHTML = `
      <div class="overview-card-top">
        <span class="overview-num">${numLabel}</span>
        <span class="overview-icon">${icon}</span>
      </div>
      <span class="overview-title">${title}</span>
    `;

    card.addEventListener('click', () => {
      setOverviewOpen(false);
      if (isMobileScrollMode()) {
        slides[idx].scrollIntoView({ behavior: 'smooth', block: 'start' });
      } else {
        render(idx);
      }
    });
    return card;
  };

  const groups = [];
  let currentGroup = null;

  slides.forEach((s, idx) => {
    if (s.dataset.secret === 'true') return;
    if (s.dataset.act) {
      currentGroup = {
        actKey: s.dataset.act,
        actLabel: s.dataset.actLabel || '',
        actSlide: { s, idx },
        items: []
      };
      groups.push(currentGroup);
    } else if (currentGroup) {
      currentGroup.items.push({ s, idx });
    }
  });

  groups.forEach((g) => {
    const row = document.createElement('div');
    row.className = 'overview-act-row' + (!g.actSlide ? ' is-intro' : '');
    row.dataset.act = g.actKey;
    if (g.actSlide) {
      row.appendChild(makeCard(g.actSlide.s, g.actSlide.idx, g.actKey, g.actLabel));
    }
    const slidesWrap = document.createElement('div');
    slidesWrap.className = 'overview-act-row-slides';
    slidesWrap.style.setProperty('--row-cols', String(Math.max(1, g.items.length)));
    g.items.forEach(({ s, idx }) => slidesWrap.appendChild(makeCard(s, idx, g.actKey, g.actLabel)));
    row.appendChild(slidesWrap);
    overviewGrid.appendChild(row);
  });
}

function decodeFlightBlobUrl(b64) {
  const raw = atob(b64);
  const bytes = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i) ^ 0x5A;
  return URL.createObjectURL(new Blob([bytes], { type: 'image/jpeg' }));
}

function ensureFlightData(callback) {
  const prev = window.__onFlightData;
  window.__onFlightData = () => {
    if (prev) prev();
    callback();
  };
  if (window.__flightData || window.__actMedia) {
    callback();
  } else if (!$('flightDataScript')) {
    const s = document.createElement('script');
    s.id = 'flightDataScript';
    s.src = 'flight-data.js';
    document.body.appendChild(s);
  }
}

function loadFlightMedia() {
  const imgs = document.querySelectorAll('img[data-act-img]');
  const hasUnloaded = Array.from(imgs).some((img) => !img.src);
  if (!hasUnloaded) return;
  ensureFlightData(() => {
    imgs.forEach((img) => {
      if (img.src) return;
      const key = img.dataset.actImg;
      const b64 = key === 'secret' ? window.__flightData : window.__actMedia?.[key];
      if (b64) img.src = decodeFlightBlobUrl(b64);
    });
  });
}

const isAudienceMode = new URLSearchParams(window.location.search).get('mode') === 'audience';
const audienceFsPrompt = $('audienceFsPrompt');
const presenterConsole = $('presenterConsole');
const pCounter = $('pCounter');
const pPrevBtn = $('pPrevBtn');
const pNextBtn = $('pNextBtn');
const pThemeSelect = $('pThemeSelect');
const presenterNotesBody = $('presenterNotesBody');
const currentPreviewBox = $('currentPreviewBox');
const currentPreviewStage = $('currentPreviewStage');
const nextPreviewBox = $('nextPreviewBox');
const nextPreviewStage = $('nextPreviewStage');
const nextPreviewEnd = $('nextPreviewEnd');
const audienceDot = $('audienceDot');
const audienceStatusText = $('audienceStatusText');
const reopenAudienceBtn = $('reopenAudienceBtn');
const presenterTimerEl = $('presenterTimer');
const timerToggleBtn = $('timerToggleBtn');

let audienceWin = null;
let isPresenterMode = false;
let syncChannel = null;
try { syncChannel = new BroadcastChannel('vsods-deck'); } catch (_) {}

let timerSeconds = 0;
let timerRunning = false;
let timerInterval = null;

function formatTimer(secs) {
  const m = String(Math.floor(secs / 60)).padStart(2, '0');
  const s = String(secs % 60).padStart(2, '0');
  return `${m}:${s}`;
}

function updateTimerUI() {
  presenterTimerEl.textContent = formatTimer(timerSeconds);
  timerToggleBtn.textContent = timerRunning ? 'Pause' : 'Start';
}

function startTimer() {
  if (timerRunning) return;
  timerRunning = true;
  updateTimerUI();
  timerInterval = setInterval(() => {
    timerSeconds++;
    presenterTimerEl.textContent = formatTimer(timerSeconds);
  }, 1000);
}

function pauseTimer() {
  timerRunning = false;
  clearInterval(timerInterval);
  timerInterval = null;
  updateTimerUI();
}

function resetTimer() {
  timerSeconds = 0;
  updateTimerUI();
}

function postSync(msg) {
  if (audienceWin && !audienceWin.closed) {
    try { audienceWin.postMessage(msg, '*'); } catch (_) {}
  }
  if (isAudienceMode && window.opener && !window.opener.closed) {
    try { window.opener.postMessage(msg, '*'); } catch (_) {}
  }
  if (syncChannel) {
    try { syncChannel.postMessage(msg); } catch (_) {}
  }
}

function updateAudienceConnectionUI() {
  const connected = Boolean(audienceWin && !audienceWin.closed);
  audienceDot.classList.toggle('disconnected', !connected);
  audienceStatusText.textContent = connected ? 'Audience window connected' : 'Audience window closed';
  reopenAudienceBtn.style.display = connected ? 'none' : 'inline-flex';
}

function scalePreviewStage(box, stage) {
  if (!box || !stage) return;
  const rect = box.getBoundingClientRect();
  if (rect.width <= 0) return;
  const scale = rect.width / 1440;
  stage.style.transform = `scale(${scale})`;
}

function getSlideNotesHtml(slideEl) {
  const el = slideEl?.querySelector('aside.notes');
  return el ? el.innerHTML.trim() : '';
}

function cloneSlideToStage(slideEl, stage) {
  stage.innerHTML = '';
  if (!slideEl) return;
  const clone = slideEl.cloneNode(true);
  clone.classList.add('active');
  clone.querySelectorAll('[id]').forEach((el) => el.removeAttribute('id'));
  clone.querySelectorAll('.slide-next-btn, aside.notes').forEach((el) => el.remove());
  clone.querySelectorAll('.bar-fill').forEach((bar) => {
    const w = bar.dataset.width || bar.style.width;
    if (w) bar.style.width = w;
  });
  stage.appendChild(clone);
}

function fitPresenterNotes() {
  if (!isPresenterMode || !presenterNotesBody) return;
  presenterNotesBody.style.fontSize = '';
  presenterNotesBody.style.lineHeight = '';
  const available = presenterNotesBody.clientHeight;
  if (available <= 0 || presenterNotesBody.scrollHeight <= available) return;

  const maxSize = parseFloat(getComputedStyle(presenterNotesBody).fontSize) || 24;
  presenterNotesBody.style.lineHeight = '1.45';
  if (presenterNotesBody.scrollHeight <= available) return;

  let lo = 10;
  let hi = maxSize;
  while (hi - lo > 0.5) {
    const mid = (lo + hi) / 2;
    presenterNotesBody.style.fontSize = mid + 'px';
    if (presenterNotesBody.scrollHeight <= available) lo = mid;
    else hi = mid;
  }
  presenterNotesBody.style.fontSize = Math.floor(lo * 2) / 2 + 'px';
}

function updatePresenterConsole() {
  if (!isPresenterMode) return;
  pCounter.textContent = `${index + 1} / ${publicSlideCount}`;
  pCounter.classList.toggle('is-secret-ready', index === publicSlideCount - 1);
  pPrevBtn.disabled = index === 0;
  pNextBtn.disabled = index === slides.length - 1;

  const notesHtml = getSlideNotesHtml(slides[index]);
  if (notesHtml) {
    presenterNotesBody.innerHTML = notesHtml;
    presenterNotesBody.classList.remove('presenter-notes-empty');
  } else {
    presenterNotesBody.textContent = 'No speaker notes for this slide.';
    presenterNotesBody.classList.add('presenter-notes-empty');
  }
  fitPresenterNotes();

  cloneSlideToStage(slides[index], currentPreviewStage);
  scalePreviewStage(currentPreviewBox, currentPreviewStage);

  const nextSlide = index + 1 < slides.length ? slides[index + 1] : null;
  if (nextSlide) {
    nextPreviewEnd.style.display = 'none';
    nextPreviewStage.style.display = 'block';
    cloneSlideToStage(nextSlide, nextPreviewStage);
    scalePreviewStage(nextPreviewBox, nextPreviewStage);
  } else {
    nextPreviewStage.innerHTML = '';
    nextPreviewStage.style.display = 'none';
    nextPreviewEnd.style.display = 'flex';
  }
  updateAudienceConnectionUI();
}

function openAudienceWindow() {
  const url = new URL(window.location.href);
  url.searchParams.set('mode', 'audience');
  audienceWin = window.open(
    url.toString(),
    'vsods-audience',
    'width=1280,height=720,menubar=no,toolbar=no,location=no,status=no'
  );
  if (!audienceWin) {
    alert('Popup blocked! Please allow popups for this page to open the Audience window.');
    return false;
  }
  updateAudienceConnectionUI();
  return true;
}

function startPresentationMode() {
  if (isAudienceMode) return;
  if (!audienceWin || audienceWin.closed) {
    if (!openAudienceWindow()) return;
  } else {
    audienceWin.focus();
  }
  isPresenterMode = true;
  document.body.classList.add('is-presenter');
  if (timerSeconds === 0 && !timerRunning) startTimer();
  updatePresenterConsole();
  postSync({ type: 'sync', index, theme: themeSelect.value });
}

function exitPresentationMode() {
  isPresenterMode = false;
  document.body.classList.remove('is-presenter');
  pauseTimer();
  if (audienceWin && !audienceWin.closed) {
    try { audienceWin.close(); } catch (_) {}
  }
  audienceWin = null;
}

function parseSlideFromUrl() {
  const hash = window.location.hash || '';
  const hashMatch = hash.match(/^#(?:slide[=-])?(\d+)$/i);
  const raw = hashMatch ? hashMatch[1] : new URLSearchParams(window.location.search).get('slide');
  if (!raw || !/^\d+$/.test(raw)) return null;
  const num = parseInt(raw, 10);
  if (num < 1) return 0;
  return Math.min(slides.length - 1, num - 1);
}

function buildCurrentSlideUrl() {
  const url = new URL(window.location.href);
  url.searchParams.delete('mode');
  url.hash = `slide=${index + 1}`;
  return url.toString();
}

async function copyTextToClipboard(text) {
  if (navigator.clipboard && window.isSecureContext) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch (_) {}
  }
  try {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.setAttribute('readonly', '');
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(ta);
    return ok;
  } catch (_) {
    return false;
  }
}

let shareFeedbackTimer = null;
async function handleShareClick(btn, defaultLabel, onDone) {
  const url = buildCurrentSlideUrl();
  const copied = await copyTextToClipboard(url);
  if (!copied) return;
  const labelEl = btn.querySelector('.ctrl-label');
  const iconEl = btn.querySelector('.share-icon');
  const checkEl = btn.querySelector('.share-check');
  btn.classList.add('copied');
  if (labelEl) labelEl.textContent = 'Copied!';
  else btn.textContent = '✓ Copied!';
  if (iconEl) iconEl.style.display = 'none';
  if (checkEl) checkEl.style.display = '';
  clearTimeout(shareFeedbackTimer);
  shareFeedbackTimer = setTimeout(() => {
    btn.classList.remove('copied');
    if (labelEl) labelEl.textContent = defaultLabel;
    else btn.textContent = defaultLabel;
    if (iconEl) iconEl.style.display = '';
    if (checkEl) checkEl.style.display = 'none';
    if (onDone) onDone();
  }, 1500);
}

function render(i, fromSync = false) {
  index = Math.max(0, Math.min(slides.length - 1, i));
  slides.forEach((s, idx) => s.classList.toggle('active', idx === index));
  document.body.classList.toggle('is-first-slide', index === 0);
  prevBtn.disabled = index === 0;
  nextBtn.disabled = index >= publicSlideCount - 1;
  counter.textContent = `${index + 1} / ${publicSlideCount}`;
  counter.classList.toggle('is-secret-ready', index === publicSlideCount - 1);
  updateSecretHint(index === publicSlideCount - 1);
  progress.style.width = `${Math.min(100, ((index + 1) / publicSlideCount) * 100)}%`;
  notesText.innerHTML = getSlideNotesHtml(slides[index]);
  if (slides[index].dataset.secret === 'true' || slides[index].dataset.act || slides[index + 1]?.dataset.act) {
    loadFlightMedia();
  }
  animateSlide(slides[index]);
  updatePresenterConsole();
  updateControlsLayout();
  if (!fromSync && !isAudienceMode) {
    try {
      history.replaceState(null, '', '#slide=' + (index + 1));
    } catch (_) {}
    postSync({ type: 'sync', index, theme: themeSelect.value });
  }
}

function setTheme(theme, fromSync = false) {
  document.documentElement.setAttribute('data-theme', theme);
  themeSelect.value = theme;
  pThemeSelect.value = theme;
  if (mobileThemeSelect) mobileThemeSelect.value = theme;
  if (themeColorMeta && themeColors[theme]) {
    themeColorMeta.setAttribute('content', themeColors[theme]);
  }
  fitHeroTitle();
  updatePresenterConsole();
  updateControlsLayout();
  if (!fromSync) {
    try { localStorage.setItem('deck-theme', theme); } catch (_) {}
  }
  if (!fromSync && !isAudienceMode) {
    postSync({ type: 'sync', index, theme });
  }
}

function setOverviewOpen(open) {
  overviewOverlay.classList.toggle('open', open);
  overviewOverlay.setAttribute('aria-hidden', String(!open));
}

function toggleOverview() {
  const opening = !overviewOverlay.classList.contains('open');
  if (opening) buildOverview();
  setOverviewOpen(opening);
}

function toggleNotes() {
  const open = notesDrawer.classList.toggle('open');
  document.body.classList.toggle('notes-open', open);
  try { localStorage.setItem('deck-notes-open', String(open)); } catch (_) {}
  updateControlsLayout();
}

function toggleAudienceFullscreen() {
  if (!document.fullscreenElement) {
    document.documentElement.requestFullscreen().catch(() => {});
  } else {
    document.exitFullscreen().catch(() => {});
  }
}

function handleSyncMessage(data) {
  if (!data || typeof data !== 'object') return;
  if (isAudienceMode && data.type === 'sync') {
    if (data.theme && data.theme !== themeSelect.value) setTheme(data.theme, true);
    if (typeof data.index === 'number' && data.index !== index) render(data.index, true);
  } else if (!isAudienceMode) {
    if (data.type === 'ready') {
      updateAudienceConnectionUI();
      postSync({ type: 'sync', index, theme: themeSelect.value });
    } else if (data.type === 'nav' && typeof data.delta === 'number') {
      render(index + data.delta);
    } else if (data.type === 'audience-closed') {
      setTimeout(updateAudienceConnectionUI, 50);
    }
  }
}

window.addEventListener('message', (e) => handleSyncMessage(e.data));
if (syncChannel) syncChannel.addEventListener('message', (e) => handleSyncMessage(e.data));

if (typeof ResizeObserver !== 'undefined') {
  const ro = new ResizeObserver(() => {
    if (isPresenterMode) {
      scalePreviewStage(currentPreviewBox, currentPreviewStage);
      scalePreviewStage(nextPreviewBox, nextPreviewStage);
      fitPresenterNotes();
    }
  });
  ro.observe(currentPreviewBox);
  ro.observe(nextPreviewBox);
  ro.observe(presenterNotesBody);
}

const controlsNav = $('controlsNav');
const controlsSecondary = $('controlsSecondary');
const controlsBurgerBtn = $('controlsBurgerBtn');
const shareBtn = $('shareBtn');

function setControlsMenuOpen(open) {
  controlsSecondary.classList.toggle('open', open);
  controlsBurgerBtn.setAttribute('aria-expanded', String(open));
  controlsBurgerBtn.textContent = open ? '✕' : '☰';
}

function updateControlsLayout() {
  if (isAudienceMode || isPresenterMode || window.innerWidth <= 768) return;
  if (window.innerWidth <= 900) {
    document.body.classList.add('controls-compact');
    return;
  }
  if (!document.body.classList.contains('notes-open') || !notesText.textContent.trim()) {
    document.body.classList.remove('controls-compact');
    setControlsMenuOpen(false);
    return;
  }
  document.body.classList.remove('controls-compact');
  const controlsRect = controlsNav.getBoundingClientRect();
  const range = document.createRange();
  range.selectNodeContents(notesText);
  const textRect = range.getBoundingClientRect();
  const overlapsNotes = textRect.width > 0 && controlsRect.left < textRect.right + 16;
  document.body.classList.toggle('controls-compact', overlapsNotes);
  if (!overlapsNotes) setControlsMenuOpen(false);
}

window.addEventListener('resize', updateControlsLayout);
document.fonts?.ready?.then(updateControlsLayout);

controlsBurgerBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  setControlsMenuOpen(!controlsSecondary.classList.contains('open'));
});

document.addEventListener('click', (e) => {
  if (controlsSecondary.classList.contains('open') && !controlsNav.contains(e.target)) {
    setControlsMenuOpen(false);
  }
});

prevBtn.addEventListener('click', () => render(index - 1));
nextBtn.addEventListener('click', () => render(index + 1));
$('heroNextBtn').addEventListener('click', () => render(index + 1));
$('presentBtn').addEventListener('click', () => {
  setControlsMenuOpen(false);
  startPresentationMode();
});
$('notesBtn').addEventListener('click', () => {
  setControlsMenuOpen(false);
  toggleNotes();
});
$('gridBtn').addEventListener('click', () => {
  setControlsMenuOpen(false);
  toggleOverview();
});
counter.addEventListener('click', () => {
  setControlsMenuOpen(false);
  if (index === publicSlideCount - 1) {
    render(slides.length - 1);
  } else {
    toggleOverview();
  }
});
shareBtn.addEventListener('click', () => {
  handleShareClick(shareBtn, 'Share', () => setControlsMenuOpen(false));
});
$('overviewClose').addEventListener('click', () => setOverviewOpen(false));
overviewOverlay.addEventListener('click', (e) => {
  if (!e.target.closest('.overview-card, button')) {
    setOverviewOpen(false);
  }
});
themeSelect.addEventListener('change', (e) => setTheme(e.target.value));

// Presenter Console controls
pPrevBtn.addEventListener('click', () => render(index - 1));
pNextBtn.addEventListener('click', () => render(index + 1));
$('pGridBtn').addEventListener('click', toggleOverview);
pCounter.addEventListener('click', () => {
  if (index === publicSlideCount - 1) {
    render(slides.length - 1);
  } else {
    toggleOverview();
  }
});
pThemeSelect.addEventListener('change', (e) => setTheme(e.target.value));
$('exitPresentBtn').addEventListener('click', exitPresentationMode);
reopenAudienceBtn.addEventListener('click', () => {
  if (openAudienceWindow()) {
    postSync({ type: 'sync', index, theme: themeSelect.value });
  }
});
timerToggleBtn.addEventListener('click', () => (timerRunning ? pauseTimer() : startTimer()));
$('timerResetBtn').addEventListener('click', resetTimer);

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && controlsSecondary.classList.contains('open')) {
    e.preventDefault();
    setControlsMenuOpen(false);
    return;
  }
  if (e.key === 'Escape' || e.key.toLowerCase() === 'g') {
    if (isAudienceMode && e.key === 'Escape') return;
    e.preventDefault();
    if (overviewOverlay.classList.contains('open')) {
      setOverviewOpen(false);
    } else {
      buildOverview();
      setOverviewOpen(true);
    }
    return;
  }
  if (overviewOverlay.classList.contains('open')) return;
  if (e.key === 'ArrowRight' || e.key === ' ' || e.key === 'PageDown') {
    e.preventDefault();
    if (!isPresenterMode && !isAudienceMode && index >= publicSlideCount - 1) return;
    if (isAudienceMode) {
      render(index + 1, true);
      postSync({ type: 'nav', delta: 1 });
    } else {
      render(index + 1);
    }
  } else if (e.key === 'ArrowLeft' || e.key === 'PageUp') {
    e.preventDefault();
    if (isAudienceMode) {
      render(index - 1, true);
      postSync({ type: 'nav', delta: -1 });
    } else {
      render(index - 1);
    }
  } else if (e.key.toLowerCase() === 'f' && isAudienceMode) {
    e.preventDefault();
    toggleAudienceFullscreen();
  } else if (e.key.toLowerCase() === 'p' && !isAudienceMode) {
    e.preventDefault();
    if (isPresenterMode) exitPresentationMode();
    else startPresentationMode();
  } else if (e.key.toLowerCase() === 'n' && !isAudienceMode && !isPresenterMode) {
    toggleNotes();
  } else if (e.key.toLowerCase() === 't' && !isAudienceMode) {
    const next = themes[(themes.indexOf(themeSelect.value) + 1) % themes.length];
    setTheme(next);
  }
});

if (isAudienceMode) {
  document.body.classList.add('is-audience');
  document.body.classList.remove('notes-open');
  notesDrawer.classList.remove('open');
  audienceFsPrompt.classList.add('visible');

  document.addEventListener('click', () => {
    if (!document.fullscreenElement) toggleAudienceFullscreen();
  });
  document.addEventListener('fullscreenchange', () => {
    if (document.fullscreenElement) {
      audienceFsPrompt.classList.remove('visible');
    }
  });
  window.addEventListener('beforeunload', () => postSync({ type: 'audience-closed' }));

  render(0, true);
  postSync({ type: 'ready' });
} else {
  let savedTheme = null;
  let savedNotes = null;
  try {
    savedTheme = localStorage.getItem('deck-theme');
    savedNotes = localStorage.getItem('deck-notes-open');
  } catch (_) {}
  if (savedTheme && themes.includes(savedTheme)) setTheme(savedTheme, true);
  else setTheme(themes[0], true);
  if (savedNotes !== null) {
    notesDrawer.classList.toggle('open', savedNotes === 'true');
  }
  document.body.classList.toggle('notes-open', notesDrawer.classList.contains('open'));
  setInterval(() => {
    if (isPresenterMode) updateAudienceConnectionUI();
  }, 1000);
  const initialIndex = parseSlideFromUrl();
  render(initialIndex ?? 0);
  window.addEventListener('hashchange', () => {
    const targetIndex = parseSlideFromUrl();
    if (targetIndex !== null && targetIndex !== index) {
      render(targetIndex);
    }
  });
}
const mobileNav = $('mobileNav');
const mobileMenu = $('mobileMenu');
const mobileBurgerBtn = $('mobileBurgerBtn');
const mobileGridBtn = $('mobileGridBtn');
const mobileAllNotesBtn = $('mobileAllNotesBtn');
const mobileShareBtn = $('mobileShareBtn');

function isMobileScrollMode() {
  return window.innerWidth <= 768 && !isAudienceMode && !isPresenterMode;
}

function setMobileMenuOpen(open) {
  mobileMenu.classList.toggle('open', open);
  mobileMenu.setAttribute('aria-hidden', String(!open));
  mobileBurgerBtn.setAttribute('aria-expanded', String(open));
  mobileBurgerBtn.textContent = open ? '✕' : '☰';
}

// Build per-slide inline expanding notes for mobile pageless scroll
const mobileNoteControls = [];
slides.forEach((slide) => {
  const notesHtml = getSlideNotesHtml(slide);
  if (!notesHtml) return;

  const wrap = document.createElement('div');
  wrap.className = 'mobile-notes-wrap';

  const toggle = document.createElement('button');
  toggle.type = 'button';
  toggle.className = 'mobile-notes-toggle';
  toggle.setAttribute('aria-expanded', 'false');
  toggle.innerHTML = '<span>📝 Notes</span><span class="chevron" aria-hidden="true">▾</span>';

  const body = document.createElement('div');
  body.className = 'mobile-notes-body';
  body.hidden = true;
  body.innerHTML = notesHtml;

  const setSlideNoteOpen = (open) => {
    body.hidden = !open;
    toggle.setAttribute('aria-expanded', String(open));
  };

  toggle.addEventListener('click', () => {
    setSlideNoteOpen(body.hidden);
    syncAllNotesButtonLabel();
  });

  wrap.appendChild(toggle);
  wrap.appendChild(body);
  slide.appendChild(wrap);
  mobileNoteControls.push({ body, setSlideNoteOpen });
});

function syncAllNotesButtonLabel() {
  if (!mobileNoteControls.length) return;
  const allOpen = mobileNoteControls.every((item) => !item.body.hidden);
  mobileAllNotesBtn.textContent = allOpen ? 'Hide All Notes' : 'Show All Notes';
  mobileAllNotesBtn.classList.toggle('active', allOpen);
  mobileAllNotesBtn.setAttribute('aria-pressed', String(allOpen));
}

mobileBurgerBtn.addEventListener('click', (e) => {
  e.stopPropagation();
  setMobileMenuOpen(!mobileMenu.classList.contains('open'));
});

mobileGridBtn.addEventListener('click', () => {
  setMobileMenuOpen(false);
  buildOverview();
  setOverviewOpen(true);
});

mobileAllNotesBtn.addEventListener('click', () => {
  const anyClosed = mobileNoteControls.some((item) => item.body.hidden);
  mobileNoteControls.forEach((item) => item.setSlideNoteOpen(anyClosed));
  syncAllNotesButtonLabel();
  setMobileMenuOpen(false);
});

mobileShareBtn.addEventListener('click', () => {
  handleShareClick(mobileShareBtn, 'Share Slide Link', () => setMobileMenuOpen(false));
});

mobileThemeSelect.addEventListener('change', (e) => {
  setTheme(e.target.value);
});

document.addEventListener('click', (e) => {
  if (mobileMenu.classList.contains('open') && !mobileNav.contains(e.target)) {
    setMobileMenuOpen(false);
  }
});

// Track scroll position & trigger slide animations on mobile pageless scroll
const animatedMobileSlides = new Set();
let mobileObserver = null;
if ('IntersectionObserver' in window) {
  mobileObserver = new IntersectionObserver((entries) => {
    if (!isMobileScrollMode()) return;
    entries.forEach((entry) => {
      if (!entry.isIntersecting) return;
      const idx = slides.indexOf(entry.target);
      if (idx === -1) return;
      index = idx;
      mobileCounter.textContent = `${idx + 1} / ${publicSlideCount}`;
      mobileCounter.classList.toggle('is-secret-ready', idx >= publicSlideCount - 1);
      updateSecretHint(idx === publicSlideCount - 1);
      progress.style.width = `${Math.min(100, ((idx + 1) / publicSlideCount) * 100)}%`;
      try {
        history.replaceState(null, '', '#slide=' + (idx + 1));
      } catch (_) {}
      if (entry.target.dataset.act || slides[idx + 1]?.dataset.act) {
        loadFlightMedia();
      }
      if (!animatedMobileSlides.has(entry.target)) {
        animatedMobileSlides.add(entry.target);
        animateSlide(entry.target);
      }
    });
  }, { threshold: 0.35 });

  slides.forEach((slide) => {
    if (slide.dataset.secret !== 'true') {
      mobileObserver.observe(slide);
    }
  });

  if (isMobileScrollMode()) {
    loadFlightMedia();
    if (index > 0 && slides[index]) {
      if (slides[index].dataset.secret === 'true') {
        slides[index].classList.add('secret-revealed');
        loadFlightMedia();
        mobileObserver.observe(slides[index]);
      }
      requestAnimationFrame(() => {
        slides[index].scrollIntoView({ block: 'start' });
      });
    }
  }
}

mobileCounter.addEventListener('click', () => {
  if (!isMobileScrollMode()) return;
  if (index < publicSlideCount - 1) {
    setMobileMenuOpen(false);
    toggleOverview();
    return;
  }
  const secretSlide = slides.find((s) => s.dataset.secret === 'true');
  if (!secretSlide) return;
  secretSlide.classList.add('secret-revealed');
  updateSecretHint(false);
  loadFlightMedia();
  if (mobileObserver) mobileObserver.observe(secretSlide);
  requestAnimationFrame(() => {
    secretSlide.scrollIntoView({ behavior: 'smooth', block: 'start' });
  });
});

function fitHeroTitle() {
  const box = document.querySelector('#heroSlide .title-hero-text');
  const h1 = box?.querySelector('h1');
  if (!box || !h1) return;
  h1.style.fontSize = '';
  if (isMobileScrollMode()) return;
  const available = box.clientWidth;
  if (available > 0 && h1.scrollWidth > available) {
    const current = parseFloat(getComputedStyle(h1).fontSize);
    h1.style.fontSize = Math.floor(current * (available / h1.scrollWidth) * 10) / 10 + 'px';
  }
}
window.addEventListener('resize', fitHeroTitle);
document.fonts?.ready?.then(fitHeroTitle);
fitHeroTitle();
