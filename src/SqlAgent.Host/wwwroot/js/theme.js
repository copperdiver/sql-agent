// Loaded synchronously from <head>, before Blazor connects. A Blazor round trip would paint the
// light theme first and flash; a deferred script would do the same. Everything here must therefore
// be cheap and must not touch the DOM beyond <html>'s class list, which exists at this point.
(function () {
  var THEME_KEY = 'sqlagent.theme';
  var SIDEBAR_KEY = 'sqlagent.sidebar';

  // localStorage throws in some private-browsing modes rather than returning null, and a theme
  // preference is not worth breaking the page over.
  function read(key, fallback) {
    try {
      return window.localStorage.getItem(key) || fallback;
    } catch (e) {
      return fallback;
    }
  }

  function write(key, value) {
    try {
      window.localStorage.setItem(key, value);
    } catch (e) {
      /* ignore: the class is still applied for this page's lifetime */
    }
  }

  function applyTheme(theme) {
    var classes = document.documentElement.classList;
    classes.remove('light', 'dark');
    // 'system' intentionally adds nothing: app.css keys the OS preference off the absence of both.
    if (theme === 'light' || theme === 'dark') classes.add(theme);
  }

  function applySidebar(state) {
    document.documentElement.classList.toggle('sidebar-collapsed', state === 'collapsed');
  }

  applyTheme(read(THEME_KEY, 'system'));
  applySidebar(read(SIDEBAR_KEY, 'expanded'));

  window.sqlAgentUi = {
    getTheme: function () { return read(THEME_KEY, 'system'); },
    setTheme: function (theme) { write(THEME_KEY, theme); applyTheme(theme); },
    getSidebar: function () { return read(SIDEBAR_KEY, 'expanded'); },
    setSidebar: function (state) { write(SIDEBAR_KEY, state); applySidebar(state); }
  };
})();
