#!/usr/bin/env bash
# Starts two throwaway MongoDB servers for the integration tests: one plain and one that requires TLS
# with a client certificate. Prints the environment variables the tests read.
#
# Usage: scripts/start-test-mongo.sh [image]      (default image: mongo:7.0)
#        scripts/start-test-mongo.sh --stop
set -euo pipefail

IMAGE="${1:-mongo:7.0}"
PLAIN_NAME=serviceconnect-test-mongo
TLS_NAME=serviceconnect-test-mongo-tls
PLAIN_PORT="${PLAIN_PORT:-27117}"
TLS_PORT="${TLS_PORT:-27118}"
CERT_DIR="${CERT_DIR:-${TMPDIR:-/tmp}/serviceconnect-test-mongo}"
CLIENT_CERT_PASSWORD=serviceconnect-test

if [[ "${1:-}" == "--stop" ]]; then
  docker rm -f "$PLAIN_NAME" "$TLS_NAME" >/dev/null 2>&1 || true
  exit 0
fi

mkdir -p "$CERT_DIR"
cd "$CERT_DIR"

if [[ ! -f client.pfx ]]; then
  openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=ServiceConnect Test CA" \
    -keyout ca.key -out ca.pem 2>/dev/null

  openssl req -newkey rsa:2048 -nodes -subj "/CN=localhost" -keyout server.key -out server.csr 2>/dev/null
  printf 'subjectAltName=DNS:localhost,IP:127.0.0.1\nextendedKeyUsage=serverAuth\n' > server.ext
  openssl x509 -req -in server.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 30 \
    -extfile server.ext -out server.crt 2>/dev/null
  cat server.crt server.key > server.pem

  openssl req -newkey rsa:2048 -nodes -subj "/CN=serviceconnect-test-client" -keyout client.key -out client.csr 2>/dev/null
  printf 'extendedKeyUsage=clientAuth\n' > client.ext
  openssl x509 -req -in client.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 30 \
    -extfile client.ext -out client.crt 2>/dev/null
  openssl pkcs12 -export -in client.crt -inkey client.key -out client.pfx -passout "pass:$CLIENT_CERT_PASSWORD"

  chmod 644 ./*
fi

docker rm -f "$PLAIN_NAME" "$TLS_NAME" >/dev/null 2>&1 || true

docker run -d --name "$PLAIN_NAME" -p "$PLAIN_PORT:27017" "$IMAGE" >/dev/null

# Copy the certificates in rather than bind-mount them, so this also works from inside a dev container.
docker create --name "$TLS_NAME" -p "$TLS_PORT:27017" "$IMAGE" \
  --tlsMode requireTLS --tlsCertificateKeyFile /certs/server.pem --tlsCAFile /certs/ca.pem >/dev/null
docker cp "$CERT_DIR/." "$TLS_NAME:/certs"
docker start "$TLS_NAME" >/dev/null

for name in "$PLAIN_NAME" "$TLS_NAME"; do
  for _ in $(seq 1 30); do
    if docker logs "$name" 2>&1 | grep -q 'Waiting for connections'; then
      break
    fi
    sleep 1
  done
done

cat <<EOF
# MongoDB test servers are running. Export these, then run the integration tests:
export SERVICECONNECT_MONGODB="mongodb://localhost:$PLAIN_PORT"
export SERVICECONNECT_MONGODB_SSL="nodes=localhost:$TLS_PORT,certpath=$CERT_DIR/client.pfx,certpassword=$CLIENT_CERT_PASSWORD"
# Trusts the test CA for this shell only.
export SSL_CERT_FILE="$CERT_DIR/ca.pem"
EOF
