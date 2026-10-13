import { test, expect } from '@playwright/test';
import { partnerUrl } from '../playwright.config';
import { balance, clearFault, initiate, notify, payment, setFault, settleAtPartner, transfer } from './helpers';

// Every test leaves the simulator with no fault profile, so a failing test cannot poison the next.
test.afterEach(async ({ request }) => {
  await clearFault(request);
});

test('happy path: initiate, submit, settle, notify twice, reconcile clean', async ({ request }) => {
  const valueDate = '2026-10-14';
  const nostroBefore = await balance(request, 'nostro');
  const created = await initiate(request, 'happy', transfer('HAPPY'));
  expect(created.status).toBe('Initiated');

  const submitted = await request.post(`/payments/${created.id}/submit`);
  expect(submitted.ok(), await submitted.text()).toBeTruthy();
  expect((await submitted.json()).status).toBe('Accepted');

  await settleAtPartner(request, created.endToEndId, valueDate);
  const first = await notify(request, `N-HAPPY-${created.endToEndId}`, created.endToEndId, 'Settled', { valueDate });
  const second = await notify(request, `N-HAPPY-${created.endToEndId}`, created.endToEndId, 'Settled', { valueDate });
  expect(first.outcome).toBe('Applied');
  expect(second.outcome).toBe('Duplicate');

  const settled = await payment(request, created.id);
  expect(settled.status).toBe('Settled');
  expect(settled.valueDate).toBe(valueDate);
  expect(await balance(request, 'nostro')).toBeCloseTo(nostroBefore - 100, 2);

  const ledger = await (await request.get(`/payments/${created.id}/ledger`)).json();
  expect(ledger.map((e: { description: string }) => e.description.split(' ')[0])).toEqual(['Reserve', 'Settle']);
});

test('the same idempotency key with a different body is a conflict, not a second payment', async ({ request }) => {
  const body = transfer('DUPREQ', 10);
  const first = await initiate(request, 'dupreq', body);
  const replay = await request.post('/payments', { headers: { 'Idempotency-Key': `dupreq-${body.endToEndId.split('-').pop()}` }, data: body });
  expect(replay.status()).toBe(201);
  expect((await replay.json()).id).toBe(first.id);

  const conflict = await request.post('/payments', { headers: { 'Idempotency-Key': `dupreq-${body.endToEndId.split('-').pop()}` }, data: { ...body, amount: 11 } });
  expect(conflict.status()).toBe(409);
});

test('a missing Idempotency-Key header is refused before anything is written', async ({ request }) => {
  const response = await request.post('/payments', { data: transfer('NOKEY') });
  expect(response.status()).toBe(400);
});

test('timeout after accept: Submitted, then resolved by asking the partner, never resubmitted', async ({ request }) => {
  const created = await initiate(request, 'timeout', transfer('TIMEOUT', 50));
  await setFault(request, { kind: 'TimeoutAfterAccept', delayMilliseconds: 8000 });

  const submitted = await request.post(`/payments/${created.id}/submit`);
  expect(submitted.ok(), await submitted.text()).toBeTruthy();
  expect((await submitted.json()).status).toBe('Submitted');

  await clearFault(request);
  const resolved = await request.post(`/payments/${created.id}/resolve`);
  expect(resolved.ok(), await resolved.text()).toBeTruthy();
  expect((await resolved.json()).outcome).toBe('Accepted');

  const after = await payment(request, created.id);
  expect(after.status).toBe('Accepted');
  const atPartner = await (await request.get(`${partnerUrl}/transfers/${created.endToEndId}`)).json();
  expect(atPartner.partnerReference).toBe(after.partnerReference);
});

test('503 before the partner recorded anything: the resolver finds no record and resubmits once', async ({ request }) => {
  const created = await initiate(request, 'err', transfer('ERR', 75));
  await setFault(request, { kind: 'ServerError' });

  const submitted = await request.post(`/payments/${created.id}/submit`);
  expect((await submitted.json()).status).toBe('Submitted');

  await clearFault(request);
  const resolved = await request.post(`/payments/${created.id}/resolve`);
  expect((await resolved.json()).outcome).toBe('Accepted');
  expect((await payment(request, created.id)).status).toBe('Accepted');
});

test('reject-all releases the reservation back to customer funds', async ({ request }) => {
  const pendingBefore = await balance(request, 'pending-outbound');
  const created = await initiate(request, 'reject', transfer('REJECT', 20));
  await setFault(request, { kind: 'RejectAll', reasonCode: 'AC01' });

  const submitted = await request.post(`/payments/${created.id}/submit`);
  const body = await submitted.json();
  expect(body.status).toBe('Rejected');
  expect(body.reasonCode).toBe('AC01');
  expect(await balance(request, 'pending-outbound')).toBeCloseTo(pendingBefore, 2);
});

test('statement drift of one cent is an AmountMismatch break, nothing else', async ({ request }) => {
  const valueDate = '2026-10-15';
  const created = await initiate(request, 'drift', transfer('DRIFT', 100));
  await request.post(`/payments/${created.id}/submit`);
  await settleAtPartner(request, created.endToEndId, valueDate);
  await notify(request, `N-DRIFT-${created.endToEndId}`, created.endToEndId, 'Settled', { valueDate });

  const clean = await (await request.post(`/reconciliation/runs/${valueDate}`)).json();
  expect(clean.breaks.filter((b: { endToEndId: string }) => b.endToEndId === created.endToEndId)).toHaveLength(0);

  await setFault(request, { kind: 'StatementDrift', driftAmount: 0.01 });
  const drifted = await (await request.post(`/reconciliation/runs/${valueDate}`)).json();
  const mine = drifted.breaks.find((b: { endToEndId: string }) => b.endToEndId === created.endToEndId);
  expect(mine).toMatchObject({ kind: 'AmountMismatch', statementAmount: 100.01, ledgerAmount: 100 });
});

test('a return after settlement gives the customer the money back', async ({ request }) => {
  const customerBefore = await balance(request, 'customer-funds');
  const created = await initiate(request, 'ret', transfer('RET', 30));
  await request.post(`/payments/${created.id}/submit`);
  await notify(request, `N-RET-1-${created.endToEndId}`, created.endToEndId, 'Settled', { valueDate: '2026-10-14' });
  await notify(request, `N-RET-2-${created.endToEndId}`, created.endToEndId, 'Returned', { reasonCode: 'AC04' });

  const returned = await payment(request, created.id);
  expect(returned.status).toBe('Returned');
  expect(returned.reasonCode).toBe('AC04');
  expect(await balance(request, 'customer-funds')).toBeCloseTo(customerBefore, 2);
});
