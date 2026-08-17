FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npx svelte-kit sync && npx svelte-check && npm run build

FROM node:22-alpine
WORKDIR /app
COPY --from=build /app/build ./build
COPY --from=build /app/package.json /app/package-lock.json ./
# Vite externaliseert packages uit 'dependencies' in de SSR-build (#140):
# die moeten als node_modules in de runtime-image staan.
RUN npm ci --omit=dev && npm cache clean --force
# Regressiepoort (#140): elke server-chunk moet met déze node_modules laden —
# een geëxternaliseerd package dat hier ontbreekt faalt de image-build
# (en dus de publish), niet de route in productie.
COPY --from=build /app/scripts/check-server-chunks.mjs ./scripts/
RUN node scripts/check-server-chunks.mjs build/server
ENV NODE_ENV=production PORT=3000
EXPOSE 3000
CMD ["node", "build"]
