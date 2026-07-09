FROM node:22-alpine
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm install --omit=dev && npm install typescript @types/node --no-save && echo done
COPY . .
RUN npx tsc --noEmit
ENV NODE_ENV=production PORT=8090
EXPOSE 8090
CMD ["npx", "tsx", "src/server.ts"]
