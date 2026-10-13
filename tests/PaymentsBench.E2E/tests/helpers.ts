import { expect, type APIRequestContext } from '@playwright/test';
import { partnerUrl } from '../playwright.config';

export const run = Date.now().toString(36);

export function transfer(tag: string, amount = 100) {
  return {
    endToEndId: `E2E-${tag}-${run}`,
    amount,
    currency: 'EUR',
    debtorAccount: 'DE89370400440532013000',
    creditorAccount: 'GB33BUKB20201555555555',
    scheme: 'sepa',
  };
}

export async function initiate(api: APIRequestContext, key: string, body: ReturnType<typeof transfer>) {
  const response = await api.post('/payments', { headers: { 'Idempotency-Key': `${key}-${run}` }, data: body });
  expect(response.status(), await response.text()).toBe(201);
  return response.json() as Promise<{ id: string; status: string; endToEndId: string }>;
}

export async function setFault(api: APIRequestContext, fault: Record<string, unknown>) {
  const response = await api.put(`${partnerUrl}/faults`, { data: fault });
  expect(response.ok(), await response.text()).toBeTruthy();
}

export async function clearFault(api: APIRequestContext) {
  await setFault(api, { kind: 'None' });
}

export async function settleAtPartner(api: APIRequestContext, endToEndId: string, valueDate: string) {
  const response = await api.post(`${partnerUrl}/transfers/${endToEndId}/settle?valueDate=${valueDate}`);
  expect(response.ok(), await response.text()).toBeTruthy();
}

export async function notify(api: APIRequestContext, notificationId: string, endToEndId: string, kind: string, extra: Record<string, unknown> = {}) {
  const response = await api.post('/partner/notifications', { data: { notificationId, endToEndId, kind, ...extra } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return response.json() as Promise<{ outcome: string }>;
}

export async function payment(api: APIRequestContext, id: string) {
  const response = await api.get(`/payments/${id}`);
  expect(response.ok()).toBeTruthy();
  return response.json() as Promise<{ status: string; partnerReference: string | null; valueDate: string | null; reasonCode: string | null }>;
}

export async function balance(api: APIRequestContext, account: string, currency = 'EUR') {
  const response = await api.get(`/ledger/balances/${account}/${currency}`);
  expect(response.ok()).toBeTruthy();
  return (await response.json()).balance as number;
}
