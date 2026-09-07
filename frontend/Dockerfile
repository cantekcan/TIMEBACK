FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci
COPY . .
ARG VITE_API_BASE_URL=http://localhost:8080
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
RUN npm run build

FROM node:22-alpine
WORKDIR /app
RUN npm install -g serve@14.2.6
COPY --from=build --chown=node:node /app/dist ./dist
USER node
EXPOSE 5173
CMD ["serve", "-s", "dist", "-l", "5173"]
