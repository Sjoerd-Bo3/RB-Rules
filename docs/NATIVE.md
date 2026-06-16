# Native iOS/Android via Capacitor

De native apps zijn een dunne **schil rond de gehoste PWA** (`riftbound.bo3.dev`):
Capacitor laadt de live site, dus server components, API, Q&A en push werken
ongewijzigd. Geen aparte native build van de Next.js-app nodig.

## Eenmalig opzetten (op je Mac)
Vereist: Xcode (iOS) en/of Android Studio (Android).

```bash
npm install
# platformen toevoegen (maakt ./ios en ./android — niet in git nodig)
npx cap add ios
npx cap add android
npx cap sync
```

`capacitor.config.ts` staat al goed: `appId = dev.bo3.riftbound`, en
`server.url = https://riftbound.bo3.dev` (overschrijfbaar met `CAP_SERVER_URL`).

## Draaien / builden
```bash
npx cap open ios       # opent Xcode → Run op simulator/device
npx cap open android   # opent Android Studio → Run
```
Voor een store-release bouw je vanuit Xcode (Archive) resp. Android Studio
(Generate Signed Bundle).

## Na een config-wijziging
```bash
npx cap sync
```

## Notificaties
De PWA-webpush werkt binnen de schil op Android. Voor volwaardige **native push**
(APNs/FCM) is later `@capacitor/push-notifications` + FCM/APNs-config nodig — dat
is een aparte stap; de huidige web-push dekt web + Android-PWA.

## Camera (voor de toekomstige foto-vraag)
De WebView heeft camera/mic via de site (HTTPS). Wil je de **native** camera-API,
voeg dan `@capacitor/camera` toe; de webversie blijft als fallback werken.
```
