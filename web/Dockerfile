# -----------------------------------------------------------------------------
# Web image. Build context is ./web.
#
# `npm ci` rather than `npm install`: it installs exactly what the lockfile
# says, so the image is reproducible and a transitive patch release cannot
# change what ships.
# -----------------------------------------------------------------------------
FROM node:20-alpine AS build
WORKDIR /src

COPY package.json package-lock.json* ./
RUN npm ci --no-audit --no-fund

COPY . .
RUN npm run build

# The built app is static files. Serving them from nginx rather than from `ng
# serve` means no Node process in production, a far smaller image, and gzip and
# caching handled by something built for it.
FROM nginx:1.27-alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/order-intake-web/browser /usr/share/nginx/html

EXPOSE 80
