const params = new URLSearchParams(window.location.search);
const site = params.get("site");
const reason = params.get("reason");
const siteLabel = document.getElementById("site");
const title = document.getElementById("title");
const message = document.getElementById("message");

if (reason === "daily" && siteLabel) {
  siteLabel.textContent = "today's overall screen time";
} else if (site && siteLabel) {
  siteLabel.textContent = site;
}

if (reason === "blocked") {
  if (title) {
    title.textContent = "Website blocked";
  }
  if (message) {
    message.textContent = "This website is on your blocked-sites list.";
  }
} else if (reason === "short-form") {
  if (title) {
    title.textContent = "Short-form content blocked";
  }
  if (message) {
    message.textContent = "This short-form video page is blocked by Screen Time Tracker.";
  }
} else if (reason === "focus") {
  if (title) {
    title.textContent = "Blocked during Focus Mode";
  }
  if (message) {
    message.textContent = "This entertainment website is on your Focus Mode list.";
  }
} else if (reason === "daily-entertainment") {
  if (title) {
    title.textContent = "Daily entertainment limit reached";
  }
  if (message) {
    message.textContent = "This entertainment website is blocked until your daily limit resets.";
  }
}
