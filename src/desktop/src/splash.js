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

  function render(status) {
    current = status
    var failed = status.state === 'failed'
    el.wrap.className = failed ? 'wrap failed' : 'wrap'

    el.phase.textContent = failed ? status.phase : status.phase + '…'
    // While things look normal the detail line stays hidden: it is noise until
    // the launch is either slow or broken.
    el.detail.textContent = failed || status.slow ? status.detail : ''
    el.hint.textContent = status.hint || ''
    el.elapsed.textContent =
      !failed && status.elapsed_ms >= 3000 ? Math.round(status.elapsed_ms / 1000) + 's' : ''
    el.logPath.textContent = status.log_path ? 'Log: ' + status.log_path : ''
    el.logBox.textContent = (status.log_tail || []).join('\n')
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

  el.retry.addEventListener('click', function () {
    el.logBox.classList.remove('open')
    el.toggleLog.textContent = 'Show details'
    invoke('studio_retry').catch(function (e) {
      el.hint.textContent = 'Could not restart the service: ' + e
    })
  })

  el.toggleLog.addEventListener('click', function () {
    var open = el.logBox.classList.toggle('open')
    el.toggleLog.textContent = open ? 'Hide details' : 'Show details'
  })

  el.openLog.addEventListener('click', function () {
    invoke('studio_open_log').catch(function (e) {
      el.hint.textContent = 'Could not open the log folder: ' + e
    })
  })

  el.copy.addEventListener('click', function () {
    copyText(diagnostics())
    flash(el.copy, 'Copied')
  })

  window.__tapApply = render
  if (window.__tapPending) {
    render(window.__tapPending)
    window.__tapPending = null
  }
})()
