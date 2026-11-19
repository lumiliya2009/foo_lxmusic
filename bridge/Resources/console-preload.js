'use strict';

// The bridge has no interactive console. Keep diagnostics on its optional log
// sink, never stdout, which carries the playback protocol.
globalThis.console = (() => {
  const hostLog = globalThis.__hostLog;
  delete globalThis.__hostLog;
  const counts = new Map();
  const timers = new Map();
  let depth = 0;
  const text = value => {
    try { return String(value); } catch (_) { return '[unprintable]'; }
  };
  const label = value => value === undefined ? 'default' : text(value);
  const log = (...args) => hostLog('  '.repeat(Math.min(depth, 20)) + args.map(text).join(' '));
  const group = (...args) => { log(...args); depth++; };
  const timeLog = (name, ...args) => {
    name = label(name);
    if (timers.has(name)) log(name + ': ' + Math.max(0, Date.now() - timers.get(name)) + ' ms', ...args);
    else log('Timer does not exist:', name);
  };
  return {
    log, info: log, warn: log, error: log, debug: log,
    dir: log, dirxml: log, table: log, trace: log,
    group, groupCollapsed: group,
    groupEnd() { if (depth > 0) depth--; },
    clear() { depth = 0; },
    assert(condition, ...args) { if (!condition) log('Assertion failed:', ...args); },
    count(name) {
      name = label(name);
      const count = (counts.get(name) || 0) + 1;
      counts.set(name, count);
      log(name + ': ' + count);
    },
    countReset(name) { counts.delete(label(name)); },
    time(name) {
      name = label(name);
      if (!timers.has(name)) timers.set(name, Date.now());
      else log('Timer already exists:', name);
    },
    timeLog,
    timeEnd(name) { name = label(name); timeLog(name); timers.delete(name); },
  };
})();
