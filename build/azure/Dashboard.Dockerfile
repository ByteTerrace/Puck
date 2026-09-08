FROM nginx:stable-alpine
COPY build/azure/nginx.conf.template /etc/nginx/templates/default.conf.template
COPY artifacts/azure/dashboard /usr/share/nginx/html
COPY artifacts/azure/official /usr/share/nginx/html/official
COPY artifacts/azure/release.json /usr/share/nginx/html/release.json
EXPOSE 8080
