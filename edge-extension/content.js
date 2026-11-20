let heartbeatTimer;

function reportActiveSite() {
  if (document.visibilityState !== "visible" || !document.hasFocus()) {
    stopReporting();
    return;
  }

  const host = window.location.hostname.toLowerCase().replace(/^www\./, "");
  if (host) {
    chrome.runtime.sendMessage({ type: "active-site", host });
  }
}

function startReporting() {
  if (document.visibilityState !== "visible" || !document.hasFocus()) {
    return;
  }

  reportActiveSite();
  if (heartbeatTimer === undefined) {
    heartbeatTimer = window.setInterval(reportActiveSite, 5000);
  }
}

function stopReporting() {
  if (heartbeatTimer !== undefined) {
    window.clearInterval(heartbeatTimer);
    heartbeatTimer = undefined;
  }
}

startReporting();
document.addEventListener("visibilitychange", startReporting);
window.addEventListener("focus", startReporting);
window.addEventListener("blur", stopReporting);
