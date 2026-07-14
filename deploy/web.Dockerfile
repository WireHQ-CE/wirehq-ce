# Build context is the repository root.

# ---- build ----
FROM node:22-alpine AS build
WORKDIR /app
COPY web/package.json web/package-lock.json* ./
RUN npm install
COPY web/ ./
ARG VITE_API_BASE_URL=""
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
# Stamp __APP_VERSION__ from the release tag (I2), matching the api's stamped version so the update modal's
# partial-upgrade check compares like for like. Empty in a local build → falls back to web/package.json.
ARG WIREHQ_VERSION=""
ENV WIREHQ_VERSION=$WIREHQ_VERSION
RUN npm run build

# ---- runtime ----
FROM nginx:alpine AS final
COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
