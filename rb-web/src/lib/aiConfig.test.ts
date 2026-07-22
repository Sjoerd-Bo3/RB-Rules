import { describe, expect, it } from 'vitest';
import {
	AI_CREDENTIAL_MAX_LENGTH,
	AI_CREDENTIAL_MIN_LENGTH,
	authTypesFor,
	sanitizeAiConfiguration,
	sanitizeAiDeviceLogin,
	validAiCredentialLength
} from './aiConfig';

const config = {
	generation: 3,
	models: [{
		alias: 'codex', provider: 'codex-sdk', model: 'gpt-5.6-sol', capabilities: ['extract'],
		credential: 'model-secret'
	}],
	providers: [{
		id: 'codex-sdk', configured: true, configuredAccounts: 1, availableAccounts: 1,
		inFlight: 0, status: 'ready', token: 'provider-secret'
	}],
	pools: [{
		id: 'pool-1', provider: 'codex-sdk', label: 'Primair', enabled: true, priority: 10,
		weight: 2, source: 'managed', editable: true, accountCount: 1, availableAccounts: 1,
		status: 'ready', credentials: ['pool-secret']
	}],
	accounts: [{
		id: 'account-1', poolId: 'pool-1', label: 'Codex 1', enabled: true,
		authType: 'access-token', status: 'ready', lastTestedAt: '2026-07-22T10:00:00Z',
		credentialConfigured: true, editable: true, credential: 'account-secret', authJson: '{}'
	}],
	secret: 'root-secret'
};

describe('AI-config browsergrens', () => {
	it('handhaaft voor credentials exact 8 tot en met 32.768 tekens', () => {
		expect(AI_CREDENTIAL_MIN_LENGTH).toBe(8);
		expect(AI_CREDENTIAL_MAX_LENGTH).toBe(32_768);
		expect(validAiCredentialLength('1234567')).toBe(false);
		expect(validAiCredentialLength('12345678')).toBe(true);
		expect(validAiCredentialLength('x'.repeat(32_768))).toBe(true);
		expect(validAiCredentialLength('x'.repeat(32_769))).toBe(false);
	});

	it('staat een leeg credential alleen toe wanneer het veld optioneel is', () => {
		expect(validAiCredentialLength('')).toBe(false);
		expect(validAiCredentialLength('   ', true)).toBe(true);
		expect(validAiCredentialLength(' 1234567 ', true)).toBe(false);
		expect(validAiCredentialLength(' 12345678 ', true)).toBe(true);
	});

	it('projecteert uitsluitend publieke velden en laat ieder secretveld vallen', () => {
		const sanitized = sanitizeAiConfiguration(config);
		expect(sanitized).not.toBeNull();
		const serialized = JSON.stringify(sanitized);
		for (const secret of ['root-secret', 'model-secret', 'provider-secret', 'pool-secret', 'account-secret'])
			expect(serialized).not.toContain(secret);
		expect(sanitized?.accounts[0]).toEqual({
			id: 'account-1', poolId: 'pool-1', label: 'Codex 1', enabled: true,
			authType: 'access-token', status: 'ready', lastTestedAt: '2026-07-22T10:00:00Z',
			credentialConfigured: true, editable: true
		});
	});

	it('behoudt accounts die nog nooit zijn getest', () => {
		const untested = structuredClone(config);
		delete (untested.accounts[0] as Partial<(typeof config.accounts)[number]>).lastTestedAt;
		const sanitized = sanitizeAiConfiguration(untested);
		expect(sanitized?.accounts).toHaveLength(1);
		expect(sanitized?.accounts[0]?.lastTestedAt).toBeNull();
	});

	it('laat alleen de gesloten authmethodes van de gekozen provider zien', () => {
		expect(authTypesFor('claude-agent-sdk')).toEqual(['oauth-token', 'api-key']);
		expect(authTypesFor('codex-sdk')).toEqual(['access-token', 'chatgpt-device']);
	});

	it('accepteert alleen een officiële Codex-device-URL en laat extra velden vallen', () => {
		const login = sanitizeAiDeviceLogin({
			verificationUri: 'https://auth.openai.com/codex/device?token=nooit-tonen#secret',
			userCode: 'ABCD-EFGH', intervalSeconds: 5, token: 'nooit-tonen'
		}, 'account-1');
		expect(login).toEqual({
			accountId: 'account-1',
			verificationUri: 'https://auth.openai.com/codex/device',
			userCode: 'ABCD-EFGH',
			expiresAt: null,
			intervalSeconds: 5,
			status: 'pending'
		});
		expect(sanitizeAiDeviceLogin({
			verificationUri: 'https://voorbeeld.invalid/device', userCode: 'ABCD-EFGH'
		}, 'account-1')).toBeNull();
		expect(sanitizeAiDeviceLogin({
			verificationUri: 'https://token@auth.openai.com/device', userCode: 'ABCD-EFGH'
		}, 'account-1')).toBeNull();
		expect(sanitizeAiDeviceLogin({
			verificationUri: 'https://chatgpt.com/device', userCode: 'ABCD-EFGH'
		}, 'account-1')?.verificationUri).toBe('https://chatgpt.com/device');
	});
});
