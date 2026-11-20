chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type !== "active-site" || typeof message.host !== "string" || !sender.tab) {
    return false;
  }

  fetch("http://127.0.0.1:43183/site", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ host: message.host, timestamp: Date.now() })
  })
    .then((response) => {
      if (!response.ok) {
        throw new Error(`Local tracker returned HTTP ${response.status}`);
      }

      return response.json();
    })
    .then((result) => {
      if (result.blocked && Number.isInteger(sender.tab.id)) {
        const query = new URLSearchParams({
          site: message.host,
          reason: result.reason || "website"
        });
        chrome.tabs.update(sender.tab.id, {
          url: `${chrome.runtime.getURL("blocked.html")}?${query}`
        });
      }

      sendResponse({ ok: true });
    })
    .catch((error) => {
      console.error("Could not send website activity to Screen Time Tracker:", error);
      sendResponse({ ok: false });
    });

  return true;
});
