# Extra sites behind this Caddy

Each `*.caddy` file here is imported by `../Caddyfile`, so one Caddy and one
pair of router forwards (80/443) can serve more than the Watch Party relay.
Caddy gets each site its own Let's Encrypt certificate.

The `*.caddy` files are not committed (see `.gitignore`): they name the owner's
own hostnames. A site file looks like:

```
media.example.com {
	reverse_proxy host.docker.internal:8096
}
```

`host.docker.internal` reaches services published on the host itself. The
hostname needs a DNS record pointing at the home's public address, **not**
proxied through a CDN (Cloudflare "DNS only"): Caddy must answer the ACME
challenge itself, and streaming video through Cloudflare's free proxy is
against its terms. After adding or changing a file:

```
docker compose exec caddy caddy reload --config /etc/caddy/Caddyfile
```
