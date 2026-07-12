(() => {
  const statusPill = document.getElementById('statusPill');
  const statusText = document.getElementById('statusText');
  const stepTitle = document.getElementById('stepTitle');
  const badgeArea = document.getElementById('badgeArea');
  const faceVideo = document.getElementById('faceVideo');
  const qrReaderEl = document.getElementById('qr-reader');
  const faceControls = document.getElementById('faceControls');
  const captureBtn = document.getElementById('captureBtn');
  const cancelFaceBtn = document.getElementById('cancelFaceBtn');
  const clockEl = document.getElementById('clock');

  let html5Qr = null;
  let scanning = false;
  let currentEmployee = null;
  let faceStream = null;
  let modelsReady = false;

  // ---------- Clock ----------
  setInterval(() => {
    clockEl.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }, 1000);

  // ---------- Face-api model preload (runs once, in the background) ----------
  const MODEL_URL = 'https://cdn.jsdelivr.net/gh/justadudewhohacks/face-api.js@master/weights';
  async function ensureModels() {
    if (modelsReady) return;
    await Promise.all([
      faceapi.nets.tinyFaceDetector.loadFromUri(MODEL_URL),
      faceapi.nets.faceLandmark68Net.loadFromUri(MODEL_URL),
      faceapi.nets.faceRecognitionNet.loadFromUri(MODEL_URL),
    ]);
    modelsReady = true;
  }
  ensureModels().catch(err => console.error('Model preload failed:', err));

  // ---------- QR Scanner ----------
  async function startScanner() {
    scanning = true;
    setStatus('scanning', 'Scanning');
    stepTitle.textContent = 'Scan QR Badge';
    qrReaderEl.style.display = 'block';
    faceVideo.style.display = 'none';
    faceControls.style.display = 'none';

    html5Qr = new Html5Qrcode('qr-reader');
    const config = { fps: 10, qrbox: { width: 220, height: 220 } };
    try {
      await html5Qr.start({ facingMode: 'environment' }, config, onScanSuccess, () => {});
    } catch (e) {
      try {
        await html5Qr.start({ facingMode: 'user' }, config, onScanSuccess, () => {});
      } catch (e2) {
        setStatus('bad', 'Camera unavailable');
        badgeArea.innerHTML = `<div class="empty">Could not access a camera for QR scanning.<br>${escapeHtml(String(e2))}</div>`;
      }
    }
  }

  async function stopScanner() {
    if (html5Qr && scanning) {
      try { await html5Qr.stop(); } catch (_) {}
      try { html5Qr.clear(); } catch (_) {}
    }
    scanning = false;
  }

  async function onScanSuccess(decodedText) {
    if (!scanning) return;
    await stopScanner();
    setStatus('scanning', 'Looking up badge…');
    try {
      const res = await fetch(`/api/attendance/lookup/${encodeURIComponent(decodedText)}`);
      const data = await res.json();
      if (!data.found) {
        setStatus('bad', 'Not recognized');
        badgeArea.innerHTML = `<div class="empty">${escapeHtml(data.message || 'QR code not recognized.')}</div>`;
        return resetAfterDelay(3500);
      }
      currentEmployee = { ...data.employee, qrToken: decodedText };
      renderBadge(data.employee, data.todayLogs, null, null);
      await beginFaceCapture();
    } catch (err) {
      setStatus('bad', 'Lookup failed');
      badgeArea.innerHTML = `<div class="empty">Could not reach the server. ${escapeHtml(String(err))}</div>`;
      resetAfterDelay(4000);
    }
  }

  // ---------- Face capture ----------
  async function beginFaceCapture() {
    stepTitle.textContent = 'Face Verification';
    setStatus('scanning', 'Position your face');
    qrReaderEl.style.display = 'none';
    faceVideo.style.display = 'block';
    faceControls.style.display = 'flex';
    captureBtn.disabled = true;
    captureBtn.textContent = 'Loading camera…';

    try {
      faceStream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'user' } });
      faceVideo.srcObject = faceStream;
      await ensureModels();
      captureBtn.disabled = false;
      captureBtn.textContent = 'Capture & Verify Face';
    } catch (err) {
      setStatus('bad', 'Camera error');
      captureBtn.textContent = 'Camera unavailable';
    }
  }

  function stopFaceCapture() {
    if (faceStream) {
      faceStream.getTracks().forEach(t => t.stop());
      faceStream = null;
    }
    faceVideo.srcObject = null;
  }

  captureBtn.addEventListener('click', async () => {
    captureBtn.disabled = true;
    captureBtn.textContent = 'Verifying…';
    setStatus('scanning', 'Reading face…');

    const detection = await faceapi
      .detectSingleFace(faceVideo, new faceapi.TinyFaceDetectorOptions())
      .withFaceLandmarks()
      .withFaceDescriptor();

    if (!detection) {
      setStatus('bad', 'No face found');
      captureBtn.disabled = false;
      captureBtn.textContent = 'Capture & Verify Face';
      return;
    }

    try {
      const res = await fetch('/api/attendance/verify', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ qrToken: currentEmployee.qrToken, descriptor: Array.from(detection.descriptor) })
      });
      const result = await res.json();
      stopFaceCapture();
      faceControls.style.display = 'none';

      if (result.success) {
        setStatus('good', result.logType === 'In' ? 'In-time recorded' : 'Out-time recorded');
        const state = result.logType === 'In' ? 'in' : 'out';
        renderBadge(currentEmployee, null, result.message, state);
      } else {
        setStatus('bad', 'Not verified');
        renderBadge(currentEmployee, null, result.message, 'denied');
      }
      resetAfterDelay(5000);
    } catch (err) {
      setStatus('bad', 'Server error');
      captureBtn.disabled = false;
      captureBtn.textContent = 'Capture & Verify Face';
    }
  });

  cancelFaceBtn.addEventListener('click', () => {
    stopFaceCapture();
    resetAfterDelay(0);
  });

  // ---------- Badge rendering ----------
  function renderBadge(emp, todayLogs, message, state) {
    const stateClass = state ? `state-${state}` : '';
    let logsHtml = '';
    if (todayLogs && todayLogs.length) {
      logsHtml = `<div class="logs">${todayLogs.map(l =>
        `<span class="log-chip ${l.logType.toLowerCase()}">${l.logType} · ${formatTime(l.timestamp)}</span>`
      ).join('')}</div>`;
    }
    let msgHtml = '';
    if (message) {
      const cls = state === 'denied' ? 'bad' : 'good';
      msgHtml = `<div class="msg ${cls}">${escapeHtml(message)}</div>`;
    }
    badgeArea.innerHTML = `
      <div class="badge ${stateClass}">
        <div class="code">${escapeHtml(emp.employeeCode)}</div>
        <div class="name">${escapeHtml(emp.name)}</div>
        <div class="dept">${escapeHtml(emp.department || '')}</div>
        ${msgHtml}
        ${logsHtml}
      </div>`;
  }

  function resetAfterDelay(ms) {
    setTimeout(async () => {
      badgeArea.innerHTML = `<div class="empty">Waiting for a badge to be scanned…</div>`;
      currentEmployee = null;
      await startScanner();
    }, ms);
  }

  function setStatus(kind, text) {
    statusPill.className = `status-pill ${kind}`;
    statusText.textContent = text;
  }

  function formatTime(iso) {
    return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }
  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
  }

  startScanner();
})();
