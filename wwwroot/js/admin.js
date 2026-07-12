(() => {
  const clockEl = document.getElementById('clock');
  const registerForm = document.getElementById('registerForm');
  const employeeTableWrap = document.getElementById('employeeTableWrap');
  const reportTableWrap = document.getElementById('reportTableWrap');
  const reportDate = document.getElementById('reportDate');
  const exportBtn = document.getElementById('exportBtn');

  const badgeOverlay = document.getElementById('badgeOverlay');
  const badgeCode = document.getElementById('badgeCode');
  const badgeName = document.getElementById('badgeName');
  const badgeQr = document.getElementById('badgeQr');
  const printBtn = document.getElementById('printBtn');
  const closeBadgeBtn = document.getElementById('closeBadgeBtn');

  const faceOverlay = document.getElementById('faceOverlay');
  const enrollName = document.getElementById('enrollName');
  const enrollVideo = document.getElementById('enrollVideo');
  const enrollCaptureBtn = document.getElementById('enrollCaptureBtn');
  const closeEnrollBtn = document.getElementById('closeEnrollBtn');
  const enrollHint = document.getElementById('enrollHint');

  let enrollStream = null;
  let enrollEmployeeId = null;
  let modelsReady = false;

  setInterval(() => {
    clockEl.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }, 1000);

  const todayStr = new Date().toISOString().slice(0, 10);
  reportDate.value = todayStr;

  // ---------- Directory ----------
  async function loadEmployees() {
    const res = await fetch('/api/employees');
    const employees = await res.json();
    if (!employees.length) {
      employeeTableWrap.innerHTML = '<div class="empty">No employees registered yet.</div>';
      return;
    }
    employeeTableWrap.innerHTML = `
      <table>
        <thead><tr><th>Code</th><th>Name</th><th>Department</th><th>Face</th><th></th></tr></thead>
        <tbody>
          ${employees.map(e => `
            <tr>
              <td class="mono">${escapeHtml(e.employeeCode)}</td>
              <td>${escapeHtml(e.name)}</td>
              <td>${escapeHtml(e.department || '—')}</td>
              <td><span class="badge-tag ${e.faceEnrolled ? 'yes' : 'no'}">${e.faceEnrolled ? 'Enrolled' : 'Missing'}</span></td>
              <td style="white-space:nowrap;">
                <button class="btn" data-badge="${e.id}">Badge</button>
                <button class="btn" data-enroll="${e.id}" data-name="${escapeHtml(e.name)}">Face</button>
                <button class="btn danger" data-delete="${e.id}">Delete</button>
              </td>
            </tr>`).join('')}
        </tbody>
      </table>`;

    employeeTableWrap.querySelectorAll('[data-badge]').forEach(b =>
      b.addEventListener('click', () => openBadge(b.dataset.badge)));
    employeeTableWrap.querySelectorAll('[data-enroll]').forEach(b =>
      b.addEventListener('click', () => openEnroll(b.dataset.enroll, b.dataset.name)));
    employeeTableWrap.querySelectorAll('[data-delete]').forEach(b =>
      b.addEventListener('click', () => deleteEmployee(b.dataset.delete)));
  }

  registerForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const code = document.getElementById('code').value.trim();
    const name = document.getElementById('name').value.trim();
    const dept = document.getElementById('dept').value.trim();
    const res = await fetch('/api/employees', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ employeeCode: code, name, department: dept })
    });
    const data = await res.json();
    if (!res.ok) {
      alert(data.message || 'Could not register employee.');
      return;
    }
    registerForm.reset();
    await loadEmployees();
    openBadge(data.id);
  });

  async function deleteEmployee(id) {
    if (!confirm('Remove this employee and their attendance history?')) return;
    await fetch(`/api/employees/${id}`, { method: 'DELETE' });
    await loadEmployees();
  }

  // ---------- Badge modal ----------
  async function openBadge(id) {
    const employees = await (await fetch('/api/employees')).json();
    const emp = employees.find(e => String(e.id) === String(id));
    if (!emp) return;
    badgeCode.textContent = emp.employeeCode;
    badgeName.textContent = emp.name;
    badgeQr.src = `/api/employees/${id}/qr?ts=${Date.now()}`;
    badgeOverlay.style.display = 'flex';
  }
  closeBadgeBtn.addEventListener('click', () => badgeOverlay.style.display = 'none');
  printBtn.addEventListener('click', () => window.print());

  // ---------- Face enrollment modal ----------
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

  async function openEnroll(id, name) {
    enrollEmployeeId = id;
    enrollName.textContent = name;
    enrollHint.textContent = 'Loading camera and models…';
    faceOverlay.style.display = 'flex';
    enrollCaptureBtn.disabled = true;
    try {
      enrollStream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'user' } });
      enrollVideo.srcObject = enrollStream;
      await ensureModels();
      enrollHint.textContent = 'Center the face in frame, then capture. Good lighting improves match accuracy.';
      enrollCaptureBtn.disabled = false;
    } catch (err) {
      enrollHint.textContent = 'Could not access the camera: ' + err;
    }
  }

  function closeEnroll() {
    if (enrollStream) {
      enrollStream.getTracks().forEach(t => t.stop());
      enrollStream = null;
    }
    enrollVideo.srcObject = null;
    faceOverlay.style.display = 'none';
    enrollEmployeeId = null;
  }
  closeEnrollBtn.addEventListener('click', closeEnroll);

  enrollCaptureBtn.addEventListener('click', async () => {
    enrollCaptureBtn.disabled = true;
    enrollCaptureBtn.textContent = 'Capturing…';
    const detection = await faceapi
      .detectSingleFace(enrollVideo, new faceapi.TinyFaceDetectorOptions())
      .withFaceLandmarks()
      .withFaceDescriptor();

    if (!detection) {
      enrollHint.textContent = 'No face detected — try again with better lighting and a centered face.';
      enrollCaptureBtn.disabled = false;
      enrollCaptureBtn.textContent = 'Capture Face';
      return;
    }

    await fetch(`/api/employees/${enrollEmployeeId}/face`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ descriptor: Array.from(detection.descriptor) })
    });

    enrollCaptureBtn.textContent = 'Capture Face';
    closeEnroll();
    await loadEmployees();
  });

  // ---------- Report ----------
  async function loadReport() {
    const date = reportDate.value || todayStr;
    const res = await fetch(`/api/attendance/report?date=${date}`);
    const rows = await res.json();
    if (!rows.length) {
      reportTableWrap.innerHTML = '<div class="empty">No attendance logs for this date.</div>';
      return;
    }
    reportTableWrap.innerHTML = `
      <table>
        <thead><tr><th>Code</th><th>Name</th><th>Type</th><th>Time</th></tr></thead>
        <tbody>
          ${rows.map(r => `
            <tr>
              <td class="mono">${escapeHtml(r.employeeCode)}</td>
              <td>${escapeHtml(r.name)}</td>
              <td><span class="log-chip ${r.logType.toLowerCase()}">${r.logType}</span></td>
              <td class="mono">${new Date(r.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</td>
            </tr>`).join('')}
        </tbody>
      </table>`;
  }
  reportDate.addEventListener('change', loadReport);

  exportBtn.addEventListener('click', async () => {
    const date = reportDate.value || todayStr;
    const res = await fetch(`/api/attendance/report?date=${date}`);
    const rows = await res.json();
    const header = 'EmployeeCode,Name,Department,LogType,Timestamp\n';
    const body = rows.map(r =>
      [r.employeeCode, r.name, r.department, r.logType, r.timestamp].map(csvEscape).join(',')
    ).join('\n');
    const blob = new Blob([header + body], { type: 'text/csv' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `attendance-${date}.csv`;
    a.click();
  });

  function csvEscape(v) {
    const s = String(v ?? '');
    return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  }
  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
  }

  loadEmployees();
  loadReport();
})();
