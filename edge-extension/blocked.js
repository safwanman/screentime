const params = new URLSearchParams(window.location.search);
const site = params.get("site");
const reason = params.get("reason");
const siteLabel = document.getElementById("site");

if (reason === "daily" && siteLabel) {
  siteLabel.textContent = "today's overall screen time";
} else if (site && siteLabel) {
  siteLabel.textContent = site;
}
