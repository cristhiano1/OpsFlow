# =============================================================================
# OpsFlow Web — multi-stage production image (nginx)
#
# Build context: repository root
#   docker build -f src/frontend/Dockerfile -t opsflow-web .
# =============================================================================

# --- build ---
FROM node:22-alpine AS build
WORKDIR /app

COPY src/frontend/opsflow-web/package.json src/frontend/opsflow-web/package-lock.json ./
RUN npm ci

COPY src/frontend/opsflow-web/ .
RUN npm run build

# --- runtime ---
FROM nginx:stable-alpine AS runtime

COPY docker/nginx/default.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80
