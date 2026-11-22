let heartbeatTimer;

function reportActiveSite() {
  if (document.visibilityState !== "visible" || !document.hasFocus()) {
    stopReporting();
    return;
  }

  const host = window.location.hostname.toLowerCase().replace(/^www\./, "");
  if (host) {
    chrome.runtime.sendMessage({
      type: "active-site",
      host,
      shortForm: isShortFormPage(host, window.location.pathname)
    });
  }
}

function isShortFormPage(host, path) {
  if (host === "tiktok.com" || host.endsWith(".tiktok.com")) {
    return true;
  }

  if (host === "youtube.com" || host.endsWith(".youtube.com")) {
    return /^\/shorts(?:\/|$)/i.test(path);
  }

  if (host === "instagram.com" || host.endsWith(".instagram.com")) {
    return /^\/reels?(?:\/|$)/i.test(path);
  }

  if (host === "facebook.com" || host.endsWith(".facebook.com")) {
    return /^\/reels?(?:\/|$)/i.test(path);
  }

  if (host === "snapchat.com" || host.endsWith(".snapchat.com")) {
    return /^\/spotlight(?:\/|$)/i.test(path);
  }

  return false;
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
