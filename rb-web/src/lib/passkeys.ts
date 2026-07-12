// WebAuthn-hulpjes voor de passkey-login (#109). navigator.credentials werkt
// met ArrayBuffers, rb-api (fido2-net-lib) praat base64url-JSON — dit bestand
// is de vertaling daartussen. Pure functies, unit-getest in passkeys.test.ts.

// Expliciet Uint8Array<ArrayBuffer>: sinds TS 5.7 zijn typed arrays generiek
// over hun buffer, en WebAuthn's BufferSource accepteert geen SharedArrayBuffer.
export function b64uDecode(value: string): Uint8Array<ArrayBuffer> {
	const b64 = value.replace(/-/g, '+').replace(/_/g, '/');
	const padded = b64 + '='.repeat((4 - (b64.length % 4)) % 4);
	const raw = atob(padded);
	const bytes = new Uint8Array(raw.length);
	for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
	return bytes;
}

export function b64uEncode(data: ArrayBuffer | Uint8Array): string {
	const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
	let raw = '';
	for (const b of bytes) raw += String.fromCharCode(b);
	return btoa(raw).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// De JSON-vormen zoals fido2-net-lib ze serialiseert (base64url-velden).
interface ServerCredentialRef {
	type: string;
	id: string;
	transports?: string[] | null;
}

export interface ServerCreationOptions {
	rp: { id?: string; name: string };
	user: { id: string; name: string; displayName: string };
	challenge: string;
	pubKeyCredParams?: { type: string; alg: number }[];
	timeout?: number;
	attestation?: string;
	authenticatorSelection?: {
		authenticatorAttachment?: string | null;
		residentKey?: string;
		requireResidentKey?: boolean;
		userVerification?: string;
	} | null;
	excludeCredentials?: ServerCredentialRef[];
}

export interface ServerRequestOptions {
	challenge: string;
	timeout?: number;
	rpId?: string | null;
	allowCredentials?: ServerCredentialRef[];
	userVerification?: string | null;
}

const toDescriptors = (refs: ServerCredentialRef[] | undefined) =>
	(refs ?? []).map(
		(c): PublicKeyCredentialDescriptor => ({
			type: 'public-key',
			id: b64uDecode(c.id),
			...(c.transports?.length
				? { transports: c.transports as AuthenticatorTransport[] }
				: {})
		})
	);

/** Server-opties → navigator.credentials.create. Bouwt bewust een schoon
 *  object op (geen passthrough): null-velden uit de .NET-serialisatie zouden
 *  de WebAuthn-API laten struikelen. */
export function toCreationOptions(
	options: ServerCreationOptions
): PublicKeyCredentialCreationOptions {
	const sel = options.authenticatorSelection;
	return {
		rp: options.rp,
		user: { ...options.user, id: b64uDecode(options.user.id) },
		challenge: b64uDecode(options.challenge),
		pubKeyCredParams: (options.pubKeyCredParams ?? []).map((p) => ({
			type: 'public-key',
			alg: p.alg
		})),
		...(options.timeout ? { timeout: options.timeout } : {}),
		attestation: (options.attestation ?? 'none') as AttestationConveyancePreference,
		...(sel
			? {
					authenticatorSelection: {
						...(sel.authenticatorAttachment
							? { authenticatorAttachment: sel.authenticatorAttachment as AuthenticatorAttachment }
							: {}),
						...(sel.residentKey
							? { residentKey: sel.residentKey as ResidentKeyRequirement }
							: {}),
						...(sel.requireResidentKey !== undefined
							? { requireResidentKey: sel.requireResidentKey }
							: {}),
						...(sel.userVerification
							? { userVerification: sel.userVerification as UserVerificationRequirement }
							: {})
					}
				}
			: {}),
		excludeCredentials: toDescriptors(options.excludeCredentials)
	};
}

/** Server-opties → navigator.credentials.get (login met discoverable
 *  credentials: allowCredentials is dan leeg en het apparaat kiest zelf). */
export function toRequestOptions(
	options: ServerRequestOptions
): PublicKeyCredentialRequestOptions {
	return {
		challenge: b64uDecode(options.challenge),
		...(options.timeout ? { timeout: options.timeout } : {}),
		...(options.rpId ? { rpId: options.rpId } : {}),
		allowCredentials: toDescriptors(options.allowCredentials),
		...(options.userVerification
			? { userVerification: options.userVerification as UserVerificationRequirement }
			: {})
	};
}

// De attestation-/assertion-JSON die rb-api verwacht (fido2-net-lib's
// AuthenticatorAttestationRawResponse / AuthenticatorAssertionRawResponse).
export function registrationToJson(cred: PublicKeyCredential) {
	const response = cred.response as AuthenticatorAttestationResponse;
	return {
		id: cred.id,
		rawId: b64uEncode(cred.rawId),
		type: cred.type,
		clientExtensionResults: cred.getClientExtensionResults(),
		response: {
			attestationObject: b64uEncode(response.attestationObject),
			clientDataJSON: b64uEncode(response.clientDataJSON),
			// Oudere browsers missen getTransports; rb-api gebruikt het alleen
			// als hint, dus leeg is prima.
			transports: response.getTransports?.() ?? []
		}
	};
}

export function assertionToJson(cred: PublicKeyCredential) {
	const response = cred.response as AuthenticatorAssertionResponse;
	return {
		id: cred.id,
		rawId: b64uEncode(cred.rawId),
		type: cred.type,
		clientExtensionResults: cred.getClientExtensionResults(),
		response: {
			authenticatorData: b64uEncode(response.authenticatorData),
			clientDataJSON: b64uEncode(response.clientDataJSON),
			signature: b64uEncode(response.signature),
			userHandle: response.userHandle ? b64uEncode(response.userHandle) : null
		}
	};
}
