# App-image: bouwt de Next.js PWA en kan ook de ingest-/init-scripts draaien
# (tsx blijft beschikbaar omdat we alle deps installeren). Bewust simpel voor
# self-host; image-grootte is hier geen issue.
FROM node:22-bookworm-slim

WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

ENV NODE_ENV=production
EXPOSE 3000

COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["npm", "start"]
