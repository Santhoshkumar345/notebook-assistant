// Runs before the page paints, so a remembered light or dark choice is on the page from the first frame instead of flashing the device's theme first.
// It is a separate file, not an inline script, because the content-security policy allows scripts only from this origin.
// Keep the key in step with THEME_KEY in src/app/shell/theme-store.ts (a test checks it). "System" is the absence of a choice: nothing to apply.
(function () {
  try {
    var stored = localStorage.getItem('notebook.theme');
    if (stored === 'light' || stored === 'dark') document.documentElement.setAttribute('data-theme', stored);
  } catch (error) {
    // storage blocked: the device's theme applies, which is the default anyway
  }
})();
