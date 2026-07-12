import { describe, expect, it } from 'vitest';
import {
	assertionToJson,
	b64uDecode,
	b64uEncode,
	registrationToJson,
	toCreationOptions,
	toRequestOptions,
	type ServerCreationOptions
} from './passkeys';

describe('base64url', () => {
	it('encodeert URL-veilig zonder padding', () => {
		// 0xfb 0xff levert in gewone base64 "+/8=" op — precies de tekens
		// die in base64url anders moeten.
		expect(b64uEncode(new Uint8Array([0xfb, 0xff]))).toBe('-_8');
		expect(b64uEncode(new Uint8Array([]))).toBe('');
	});

	it('decodeert met en zonder impliciete padding', () => {
		expect(Array.from(b64uDecode('-_8'))).toEqual([0xfb, 0xff]);
		expect(Array.from(b64uDecode('AQID'))).toEqual([1, 2, 3]);
		expect(Array.from(b64uDecode(''))).toEqual([]);
	});

	it('is een roundtrip voor willekeurige bytes', () => {
		const bytes = new Uint8Array(64).map((_, i) => (i * 37) % 256);
		expect(Array.from(b64uDecode(b64uEncode(bytes)))).toEqual(Array.from(bytes));
	});
});

const creationFixture: ServerCreationOptions = {
	// Vorm zoals fido2-net-lib hem serialiseert (base64url-velden, en een
	// expliciete null voor authenticatorAttachment).
	rp: { id: 'localhost', name: 'Riftbound Rules' },
	user: { id: 'AQID', name: 'speler@example.com', displayName: 'speler@example.com' },
	challenge: 'y7z_AAEC',
	pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
	timeout: 60000,
	attestation: 'none',
	authenticatorSelection: {
		authenticatorAttachment: null,
		residentKey: 'required',
		requireResidentKey: true,
		userVerification: 'preferred'
	},
	excludeCredentials: [{ type: 'public-key', id: 'BAUG', transports: null }]
};

describe('toCreationOptions', () => {
	it('decodeert de base64url-velden naar bytes', () => {
		const opts = toCreationOptions(creationFixture);
		expect(Array.from(opts.challenge as Uint8Array)).toEqual([0xcb, 0xbc, 0xff, 0, 1, 2]);
		expect(Array.from(opts.user.id as Uint8Array)).toEqual([1, 2, 3]);
		expect(Array.from(opts.excludeCredentials![0].id as Uint8Array)).toEqual([4, 5, 6]);
	});

	it('laat null-velden uit de .NET-serialisatie weg', () => {
		const opts = toCreationOptions(creationFixture);
		// Een letterlijke null zou navigator.credentials.create laten
		// struikelen; het veld hoort dan te ontbreken.
		expect('authenticatorAttachment' in opts.authenticatorSelection!).toBe(false);
		expect('transports' in opts.excludeCredentials![0]).toBe(false);
		expect(opts.authenticatorSelection!.residentKey).toBe('required');
	});
});

describe('toRequestOptions', () => {
	it('bouwt discoverable-login-opties (lege allowCredentials)', () => {
		const opts = toRequestOptions({
			challenge: 'AQID',
			timeout: 60000,
			rpId: 'localhost',
			allowCredentials: [],
			userVerification: 'preferred'
		});
		expect(Array.from(opts.challenge as Uint8Array)).toEqual([1, 2, 3]);
		expect(opts.rpId).toBe('localhost');
		expect(opts.allowCredentials).toEqual([]);
		expect(opts.userVerification).toBe('preferred');
	});
});

// Structurele nep-credentials: in node bestaat PublicKeyCredential niet, en
// de serializers gebruiken alleen de velden — precies wat we hier testen.
const fakeCredential = (response: object) =>
	({
		id: 'AQID',
		rawId: new Uint8Array([1, 2, 3]).buffer,
		type: 'public-key',
		getClientExtensionResults: () => ({}),
		response
	}) as unknown as PublicKeyCredential;

describe('serializers', () => {
	it('registrationToJson levert de vorm van AuthenticatorAttestationRawResponse', () => {
		const json = registrationToJson(
			fakeCredential({
				attestationObject: new Uint8Array([9]).buffer,
				clientDataJSON: new Uint8Array([8]).buffer,
				getTransports: () => ['internal']
			})
		);
		expect(json).toEqual({
			id: 'AQID',
			rawId: 'AQID',
			type: 'public-key',
			clientExtensionResults: {},
			response: { attestationObject: 'CQ', clientDataJSON: 'CA', transports: ['internal'] }
		});
	});

	it('registrationToJson overleeft browsers zonder getTransports', () => {
		const json = registrationToJson(
			fakeCredential({
				attestationObject: new Uint8Array([9]).buffer,
				clientDataJSON: new Uint8Array([8]).buffer
			})
		);
		expect(json.response.transports).toEqual([]);
	});

	it('assertionToJson levert de vorm van AuthenticatorAssertionRawResponse', () => {
		const json = assertionToJson(
			fakeCredential({
				authenticatorData: new Uint8Array([7]).buffer,
				clientDataJSON: new Uint8Array([8]).buffer,
				signature: new Uint8Array([9]).buffer,
				userHandle: new Uint8Array([1]).buffer
			})
		);
		expect(json.response).toEqual({
			authenticatorData: 'Bw',
			clientDataJSON: 'CA',
			signature: 'CQ',
			userHandle: 'AQ'
		});
	});

	it('assertionToJson geeft null userHandle door (niet-discoverable pad)', () => {
		const json = assertionToJson(
			fakeCredential({
				authenticatorData: new Uint8Array([7]).buffer,
				clientDataJSON: new Uint8Array([8]).buffer,
				signature: new Uint8Array([9]).buffer,
				userHandle: null
			})
		);
		expect(json.response.userHandle).toBeNull();
	});
});
