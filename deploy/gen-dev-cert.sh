#!/bin/sh
# Self-signed certificate for local testing ONLY. In production use a certificate from a real CA
# (Let's Encrypt via certbot, or your company's certificate) placed in deploy/certs/.
set -eu
cd "$(dirname "$0")/certs"
openssl req -x509 -newkey rsa:2048 -sha256 -days 30 -nodes -keyout privkey.pem -out fullchain.pem \
  -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" 2>/dev/null
chmod 600 privkey.pem
echo "dev certificate written to deploy/certs (valid 30 days)"
