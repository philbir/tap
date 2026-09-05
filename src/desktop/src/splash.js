/**
 * Splash controller.
 *
 * The Rust shell (src-tauri/src/lib.rs) pushes a status object into this page with
 * `window.eval` on every startup phase change and once a second while waiting, so
 * the window always says what it is doing instead of spinning forever on
 * "Spawning sidecar". The push can land before this script has run — the shell
 * spawns the sidecar from `setup()`, which races the webview's own load — so it
 * parks the payload on `window.__tapPending` and we drain it here.
 *
 * The two buttons talk back through `__TAURI_INTERNALS__.invoke` (the same channel
 * `@tauri-apps/api` uses; there is no bundler on this page). They are granted to
 * this local origin only — see src-tauri/capabilities/splash.json.
 */
;(function () {
  // Registered before anything else in this file can throw. The splash is the
  // shell's only way to say what went wrong, so a splash that breaks silently
  // turns every backend failure back into the white screen this page exists to
  // replace — a spinner that never resolves and never explains itself.
  window.addEventListener(
    'error',
    function (e) {
      var phase = document.getElementById('phase')
      var detail = document.getElementById('detail')
      var wrap = document.getElementById('wrap')
      if (wrap) wrap.className = 'wrap failed'
      if (phase) phase.textContent = 'The Tap Studio startup screen failed'
      if (detail) {
        detail.textContent =
          (e && (e.message || (e.target && e.target.src ? 'failed to load ' + e.target.src : ''))) ||
          'unknown error'
      }
    },
    true,
  )

  var el = {
    wrap: document.getElementById('wrap'),
    phase: document.getElementById('phase'),
    detail: document.getElementById('detail'),
    elapsed: document.getElementById('elapsed'),
    hint: document.getElementById('hint'),
    logPath: document.getElementById('logpath'),
    logBox: document.getElementById('logbox'),
    retry: document.getElementById('retry'),
    toggleLog: document.getElementById('toggle-log'),
    openLog: document.getElementById('open-log'),
    copy: document.getElementById('copy'),
  }

  /** Last status the shell sent — also what "Copy diagnostics" serialises. */
  var current = null

  function invoke(cmd) {
    var internals = window.__TAURI_INTERNALS__
    if (!internals || typeof internals.invoke !== 'function') {
      return Promise.reject(new Error('not running inside the desktop shell'))
    }
    return internals.invoke(cmd, {})
  }

  /** Write `text` to a node the shell may have renamed out from under us. */
  function set(node, text) {
    if (node) node.textContent = text
  }

  function render(status) {
    if (!status) return
    current = status
    var failed = status.state === 'failed'
    if (el.wrap) el.wrap.className = failed ? 'wrap failed' : 'wrap'

    var phase = status.phase || 'Starting Tap Studio'
    set(el.phase, failed ? phase : phase + '…')
    // While things look normal the detail line stays hidden: it is noise until
    // the launch is either slow or broken.
    set(el.detail, failed || status.slow ? status.detail || '' : '')
    set(el.hint, status.hint || '')
    set(
      el.elapsed,
      !failed && status.elapsed_ms >= 3000 ? Math.round(status.elapsed_ms / 1000) + 's' : '',
    )
    set(el.logPath, status.log_path ? 'Log: ' + status.log_path : '')
    set(el.logBox, (status.log_tail || []).join('\n'))
  }

  /**
   * The shell calls this through `window.eval`, so a throw here surfaces nowhere:
   * `eval` discards the exception and the page keeps whatever it was showing. Catch
   * it and put at least the phase on screen.
   */
  function apply(status) {
    try {
      render(status)
    } catch (e) {
      set(el.phase, (status && status.phase) || 'Tap Studio could not start')
      set(el.detail, 'The startup screen could not render this status: ' + e)
    }
  }

  function diagnostics() {
    if (!current) return ''
    return [
      'Tap Studio startup diagnostics',
      'phase:   ' + current.phase,
      'detail:  ' + current.detail,
      'elapsed: ' + current.elapsed_ms + 'ms',
      'log:     ' + current.log_path,
      '',
      (current.log_tail || []).join('\n'),
    ].join('\n')
  }

  function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).catch(fallbackCopy)
      return
    }
    fallbackCopy()

    // Custom-scheme pages are not always treated as a secure context, which is
    // where the async clipboard API is gated.
    function fallbackCopy() {
      var ta = document.createElement('textarea')
      ta.value = text
      ta.style.position = 'fixed'
      ta.style.opacity = '0'
      document.body.appendChild(ta)
      ta.select()
      try {
        document.execCommand('copy')
      } catch (e) {
        /* nothing else to try — the text is on screen and selectable */
      }
      document.body.removeChild(ta)
    }
  }

  function flash(button, label) {
    var original = button.textContent
    button.textContent = label
    setTimeout(function () {
      button.textContent = original
    }, 1400)
  }

  /** `addEventListener` on a missing node throws and aborts the rest of this file. */
  function on(node, handler) {
    if (node) node.addEventListener('click', handler)
  }

  on(el.retry, function () {
    el.logBox.classList.remove('open')
    el.toggleLog.textContent = 'Show details'
    invoke('studio_retry').catch(function (e) {
      el.hint.textContent = 'Could not restart the service: ' + e
    })
  })

  on(el.toggleLog, function () {
    var open = el.logBox.classList.toggle('open')
    el.toggleLog.textContent = open ? 'Hide details' : 'Show details'
  })

  on(el.openLog, function () {
    invoke('studio_open_log').catch(function (e) {
      el.hint.textContent = 'Could not open the log folder: ' + e
    })
  })

  on(el.copy, function () {
    copyText(diagnostics())
    flash(el.copy, 'Copied')
  })

  window.__tapApply = apply
  if (window.__tapPending) {
    apply(window.__tapPending)
    window.__tapPending = null
  }
})()
