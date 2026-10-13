// Load and chaos for the payment rail (register R9, principle P13).
//
// Two scenarios run together against a running API and a running partner simulator:
//   payments - VUs initiate, submit and (for an unknown outcome) resolve payments.
//   faults   - one VU cycles the simulator's fault profiles through PUT /faults on a timer.
// Both derive the active profile from the same clock (setup's start time and PHASE_SECONDS),
// so every API request is tagged with the profile it ran under. A request that straddles a
// phase boundary keeps the tag it started with; the error is a request or two per boundary.
//
// Expected outcomes under faults are not errors: Submitted with an unknown outcome, Rejected,
// and a 409 on an idempotency key replayed with a different body. Errors are 5xx from the API,
// statuses outside the expected set, and transport failures.
//
// Everything here is simulated; nothing talks to a real bank.

import http from 'k6/http';
import { sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

const API = __ENV.API_URL || 'http://localhost:5100';
const SIM = __ENV.SIMULATOR_URL || 'http://localhost:5101';
const VUS = Number(__ENV.VUS || 10);
const PHASE_SECONDS = Number(__ENV.PHASE_SECONDS || 30);
const SUMMARY_FILE = __ENV.SUMMARY_FILE || 'k6-summary.json';

// The kinds are the simulator's FaultKind names; the delays are what the load run chooses.
// Latency (1.5 s) is the simulator default. TimeoutAfterAccept is held longer than the API's
// 4 s attempt timeout, so the API gives up on a transfer the simulator has already recorded.
const PROFILES = [
  { name: 'None', body: { kind: 'None' }, p95Ms: 500 },
  { name: 'Latency', body: { kind: 'Latency', delayMilliseconds: 1500 }, p95Ms: 3000 },
  { name: 'TimeoutAfterAccept', body: { kind: 'TimeoutAfterAccept', delayMilliseconds: 10000 }, p95Ms: 6000 },
  { name: 'ServerError', body: { kind: 'ServerError' }, p95Ms: 1000 },
  { name: 'RejectAll', body: { kind: 'RejectAll', reasonCode: 'AC01' }, p95Ms: 1000 },
];
const OUTCOMES = ['Accepted', 'Rejected', 'Submitted->Accepted', 'Submitted->Rejected', 'Submitted->SafeToResubmit', 'Submitted'];
const STEPS = ['initiate', 'replay', 'conflict', 'submit', 'resolve'];
const CYCLE_SECONDS = PROFILES.length * PHASE_SECONDS;
const DURATION_SECONDS = Number(__ENV.DURATION_SECONDS || CYCLE_SECONDS);

const flowDuration = new Trend('payment_flow_duration', true);
const outcomes = new Counter('payment_outcomes');
const realErrors = new Rate('real_errors');
const expectedConflicts = new Counter('expected_conflicts');
const realFailures = new Counter('real_failures');
const duplicatePayments = new Counter('duplicate_payments');
const simulatorMismatches = new Counter('simulator_mismatches');

const thresholds = {
  // Overall: real failures only.
  real_errors: ['rate<0.01'],
  duplicate_payments: ['count==0'],
  simulator_mismatches: ['count==0'],
  checks: ['rate>0.99'],
};
for (const p of PROFILES) {
  // Tag combinations only appear in the end-of-test summary when a threshold names them.
  thresholds[`http_req_duration{profile:${p.name},target:api}`] = [`p(95)<${p.p95Ms}`];
  thresholds[`http_reqs{profile:${p.name},target:api}`] = ['count>=0'];
  thresholds[`real_errors{profile:${p.name}}`] = ['rate<0.01'];
  thresholds[`payment_flow_duration{profile:${p.name}}`] = ['max>=0'];
  for (const step of STEPS) {
    thresholds[`real_failures{profile:${p.name},step:${step}}`] = ['count>=0'];
  }
  for (const o of OUTCOMES) {
    thresholds[`payment_outcomes{profile:${p.name},outcome:${o}}`] = ['count>=0'];
  }
}

export const options = {
  scenarios: {
    payments: {
      executor: 'constant-vus',
      exec: 'payments',
      vus: VUS,
      duration: `${DURATION_SECONDS}s`,
    },
    faults: {
      executor: 'constant-vus',
      exec: 'faults',
      vus: 1,
      duration: `${DURATION_SECONDS}s`,
    },
  },
  thresholds,
  summaryTrendStats: ['count', 'avg', 'med', 'p(90)', 'p(95)', 'max'],
};

function profileAt(startMs, nowMs) {
  const index = Math.floor((nowMs - startMs) / 1000 / PHASE_SECONDS) % PROFILES.length;
  return PROFILES[Math.max(0, index)];
}

function setProfile(profile) {
  return http.put(`${SIM}/faults`, JSON.stringify(profile.body), {
    headers: { 'Content-Type': 'application/json' },
    tags: { target: 'simulator', step: 'set-fault' },
  });
}

export function setup() {
  setProfile(PROFILES[0]);
  return { startMs: Date.now() };
}

export function teardown() {
  setProfile(PROFILES[0]);
}

export function faults(data) {
  let applied = -1;
  const end = data.startMs + DURATION_SECONDS * 1000;
  while (Date.now() < end) {
    const profile = profileAt(data.startMs, Date.now());
    const index = PROFILES.indexOf(profile);
    if (index !== applied) {
      setProfile(profile);
      applied = index;
    }
    sleep(0.25);
  }
}

function post(url, body, headers, tags) {
  return http.post(url, body === null ? null : JSON.stringify(body), {
    headers: Object.assign({ 'Content-Type': 'application/json' }, headers || {}),
    tags: Object.assign({ target: 'api' }, tags),
    responseCallback: http.expectedStatuses(200, 201, 409),
  });
}

// Records one API response: a status in `expected` is fine, anything else is a real failure.
function judge(res, expected, tags, step) {
  const ok = expected.includes(res.status);
  realErrors.add(!ok, tags);
  if (!ok) {
    realFailures.add(1, Object.assign({ step, status: String(res.status) }, tags));
  }
  return ok;
}

export function payments(data) {
  const profile = profileAt(data.startMs, Date.now());
  const tags = { profile: profile.name };
  const started = Date.now();

  const id = `${__VU}-${__ITER}-${started}`;
  const request = {
    endToEndId: `E2E-LOAD-${id}`,
    amount: Number((1 + Math.floor(Math.random() * 99999) / 100).toFixed(2)),
    currency: 'EUR',
    debtorAccount: 'DE00000000000000000001',
    creditorAccount: 'DE00000000000000000002',
    scheme: 'sepa',
  };
  const key = { 'Idempotency-Key': `load-${id}` };

  const created = post(`${API}/payments`, request, key, Object.assign({ step: 'initiate' }, tags));
  if (!judge(created, [201], tags, 'initiate')) {
    return;
  }
  const payment = created.json();

  // One in ten iterations replays the initiate: same key, same body must return the same payment.
  if (Math.random() < 0.1) {
    const replay = post(`${API}/payments`, request, key, Object.assign({ step: 'replay' }, tags));
    if (judge(replay, [201], tags, 'replay') && replay.json('id') !== payment.id) {
      duplicatePayments.add(1, tags);
    }
  }
  // One in twenty reuses the key with a different amount: a 409 is the right answer.
  if (Math.random() < 0.05) {
    const conflict = post(`${API}/payments`, Object.assign({}, request, { amount: Number((request.amount + 1).toFixed(2)) }), key, Object.assign({ step: 'conflict' }, tags));
    if (judge(conflict, [409], tags, 'conflict')) {
      expectedConflicts.add(1, tags);
    }
  }

  const submitted = post(`${API}/payments/${payment.id}/submit`, null, {}, Object.assign({ step: 'submit' }, tags));
  if (!judge(submitted, [200], tags, 'submit')) {
    return;
  }
  let status = submitted.json('status');
  let outcome = status;

  if (status === 'Submitted') {
    // The partner's answer was lost. Ask it, as the resolver does.
    const resolved = post(`${API}/payments/${payment.id}/resolve`, null, {}, Object.assign({ step: 'resolve' }, tags));
    if (!judge(resolved, [200], tags, 'resolve')) {
      return;
    }
    const resolution = resolved.json('outcome');
    outcome = `Submitted->${resolution}`;
    if (resolution === 'Accepted' || resolution === 'Settled') {
      status = 'Accepted';
    } else if (resolution === 'Rejected') {
      status = 'Rejected';
    }
  }

  // The simulator is the partner's record. An Accepted payment must have exactly the one
  // transfer the simulator keyed by end-to-end id; a Rejected one must have none.
  if (status === 'Accepted' || status === 'Rejected') {
    const seen = http.get(`${SIM}/transfers/${request.endToEndId}`, {
      tags: { target: 'simulator', step: 'verify', profile: profile.name },
      responseCallback: http.expectedStatuses(200, 404),
    });
    const expectedStatus = status === 'Accepted' ? 200 : 404;
    if (seen.status !== expectedStatus) {
      simulatorMismatches.add(1, tags);
    }
  }

  outcomes.add(1, Object.assign({ outcome }, tags));
  flowDuration.add(Date.now() - started, tags);
}

export function handleSummary(data) {
  return {
    [SUMMARY_FILE]: JSON.stringify(data, null, 2),
    stdout: overallTable(data) + perProfileTable(data),
  };
}

function metric(data, name, field) {
  const m = data.metrics[name];
  return m && m.values[field] !== undefined ? m.values[field] : null;
}

// No remote summary library: the run must work offline, so the text summary is written here.
function overallTable(data) {
  const rows = ['', `payments-bench load run: ${VUS} VUs, ${DURATION_SECONDS}s, ${PHASE_SECONDS}s per fault profile (simulated partner)`, ''];
  const line = (label, value) => rows.push(`  ${label.padEnd(28)}${value}`);
  line('iterations', String(metric(data, 'iterations', 'count')));
  line('http requests (all)', `${metric(data, 'http_reqs', 'count')} (${metric(data, 'http_reqs', 'rate').toFixed(1)}/s)`);
  line('real error rate', `${(100 * (metric(data, 'real_errors', 'rate') || 0)).toFixed(3)} %`);
  line('expected 409 conflicts', String(metric(data, 'expected_conflicts', 'count') || 0));
  line('duplicate payments', String(metric(data, 'duplicate_payments', 'count') || 0));
  line('simulator mismatches', String(metric(data, 'simulator_mismatches', 'count') || 0));
  rows.push('', '  thresholds:');
  for (const [name, m] of Object.entries(data.metrics)) {
    for (const [expr, result] of Object.entries(m.thresholds || {})) {
      if (!expr.includes('>=0')) {
        rows.push(`    ${result.ok ? 'pass' : 'FAIL'}  ${name}  ${expr}`);
      }
    }
  }
  return rows.join('\n') + '\n';
}

function perProfileTable(data) {
  const rows = ['', 'Per fault profile (API requests only; simulator calls excluded)', ''];
  rows.push('profile              req/s   p95 ms   flows  real-error %  outcomes');
  for (const p of PROFILES) {
    const count = metric(data, `http_reqs{profile:${p.name},target:api}`, 'count') || 0;
    const p95 = metric(data, `http_req_duration{profile:${p.name},target:api}`, 'p(95)');
    const flows = metric(data, `payment_flow_duration{profile:${p.name}}`, 'count') || 0;
    const errRate = metric(data, `real_errors{profile:${p.name}}`, 'rate');
    rows.push([
      p.name.padEnd(20),
      (count / PHASE_SECONDS / Math.max(1, DURATION_SECONDS / CYCLE_SECONDS)).toFixed(1).padStart(6),
      (p95 === null ? 'n/a' : p95.toFixed(0)).padStart(8),
      String(flows).padStart(7),
      (errRate === null ? 'n/a' : (errRate * 100).toFixed(2)).padStart(13),
      OUTCOMES.map((o) => [o, metric(data, `payment_outcomes{profile:${p.name},outcome:${o}}`, 'count') || 0])
        .filter(([, n]) => n > 0).map(([o, n]) => `${o}=${n}`).join(' '),
    ].join(' '));
  }
  rows.push('', 'Real failures by profile and step (a 5xx from the API or an unexpected status)');
  let any = false;
  for (const p of PROFILES) {
    for (const step of STEPS) {
      const n = metric(data, `real_failures{profile:${p.name},step:${step}}`, 'count') || 0;
      if (n > 0) {
        any = true;
        rows.push(`  ${p.name.padEnd(20)} ${step.padEnd(9)} ${n}`);
      }
    }
  }
  if (!any) {
    rows.push('  none');
  }
  rows.push('');
  return rows.join('\n') + '\n';
}
