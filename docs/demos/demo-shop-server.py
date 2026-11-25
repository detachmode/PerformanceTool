#!/usr/bin/env python3
"""
Demo Shop target backend for Beesharp (listens on 127.0.0.1:5299).

Simulates realistic load-test behavior:
- GET  /api/products            -> fast cache hit (25-60ms) + occasional cache miss (220-420ms) for bimodal latency
- GET  /api/products/{id}       -> fast (20-50ms), ~2% 404
- POST /api/auth/token          -> fast (15-35ms)
- POST /api/cart                -> moderate (40-85ms), slows slightly when inventory is saturated
- POST /api/inventory/reserve   -> shared bottleneck! Under <= 5 concurrent requests: 55-110ms.
                                   When a burst pushes concurrency > 5: latency climbs 350-1350ms
                                   and returns HTTP 503/429 under heavy contention.
- POST /api/orders              -> 80-150ms normally, slows and fails (503) when inventory is saturated.
"""

import json
import random
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

PORT = 5399
_lock = threading.Lock()
_inv_in_flight = 0
_inv_pressure = 0.0  # smoothed recent contention so cascades linger ~3-5s after a burst


def _enter_inventory():
    global _inv_in_flight, _inv_pressure
    with _lock:
        _inv_in_flight += 1
        cur = _inv_in_flight
        if cur > 5:
            _inv_pressure = min(1.0, _inv_pressure + 0.14 * (cur - 5))
        return cur, _inv_pressure


def _leave_inventory():
    global _inv_in_flight
    with _lock:
        _inv_in_flight = max(0, _inv_in_flight - 1)


def _decay_pressure():
    global _inv_pressure
    while True:
        time.sleep(0.4)
        with _lock:
            if _inv_in_flight <= 5:
                _inv_pressure = max(0.0, _inv_pressure - 0.08)


class DemoShopHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, format, *args):
        # Quiet stdout so daemon logs stay clean
        pass

    def _send_json(self, status: int, payload: dict):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("X-Demo-Pressure", f"{_inv_pressure:.2f}")
        self.end_headers()
        self.wfile.write(body)

    def _read_body(self):
        length = int(self.headers.get("Content-Length", "0") or "0")
        if length > 0:
            self.rfile.read(length)

    def do_GET(self):
        path = urlparse(self.path).path
        with _lock:
            pressure = _inv_pressure

        if path == "/health":
            self._send_json(200, {"status": "ok", "invInFlight": _inv_in_flight, "pressure": round(pressure, 2)})
            return

        if path == "/api/products":
            # Bimodal: 82% fast cache hit, 18% cache miss (more misses under pressure)
            miss_chance = 0.14 + pressure * 0.25
            if random.random() < miss_chance:
                time.sleep(random.uniform(0.24, 0.48) + pressure * 0.25)
                cache = "miss"
            else:
                time.sleep(random.uniform(0.025, 0.065))
                cache = "hit"
            self._send_json(200, {"items": [f"SKU-{i:03d}" for i in range(1, 9)], "cache": cache})
            return

        if path.startswith("/api/products/"):
            time.sleep(random.uniform(0.02, 0.055) + pressure * 0.06)
            if random.random() < 0.025:
                self._send_json(404, {"error": "Product out of catalog"})
                return
            sku = path.rsplit("/", 1)[-1]
            self._send_json(200, {"sku": sku, "price": 29.95, "inStock": True})
            return

        self._send_json(404, {"error": "Not found"})

    def do_POST(self):
        self._read_body()
        path = urlparse(self.path).path

        if path == "/api/auth/token":
            time.sleep(random.uniform(0.015, 0.04))
            self._send_json(200, {"token": f"tok-{random.randint(1000, 9999)}"})
            return

        if path == "/api/cart":
            with _lock:
                pressure = _inv_pressure
            delay = random.uniform(0.04, 0.085) + pressure * random.uniform(0.18, 0.45)
            time.sleep(delay)
            self._send_json(200, {"cartId": f"cart-{random.randint(100, 999)}", "pressure": round(pressure, 2)})
            return

        if path == "/api/inventory/reserve":
            cur, pressure = _enter_inventory()
            try:
                if cur <= 5 and pressure < 0.25:
                    time.sleep(random.uniform(0.055, 0.115))
                    self._send_json(200, {"reserved": True, "inFlight": cur})
                else:
                    # Contention spike: queue delay + 429/503 under heavy saturation
                    extra = max(0, cur - 5) * 0.09 + pressure * 0.65
                    time.sleep(random.uniform(0.28, 0.65) + min(1.1, extra))
                    fail_prob = min(0.55, 0.12 + pressure * 0.38)
                    if random.random() < fail_prob:
                        code = 503 if random.random() < 0.65 else 429
                        msg = "Inventory lock timeout (shared pool saturated)" if code == 503 else "Inventory rate limit exceeded"
                        self._send_json(code, {"error": msg, "inFlight": cur})
                    else:
                        self._send_json(200, {"reserved": True, "inFlight": cur, "degraded": True})
            finally:
                _leave_inventory()
            return

        if path == "/api/orders":
            with _lock:
                pressure = _inv_pressure
            delay = random.uniform(0.08, 0.16) + pressure * random.uniform(0.35, 0.95)
            time.sleep(delay)
            if pressure > 0.35 and random.random() < pressure * 0.42:
                self._send_json(503, {"error": "Order confirmation failed: upstream inventory service unavailable"})
                return
            self._send_json(201, {"orderId": f"ORD-{random.randint(10000, 99999)}"})
            return

        self._send_json(404, {"error": "Not found"})


if __name__ == "__main__":
    t = threading.Thread(target=_decay_pressure, daemon=True)
    t.start()
    server = ThreadingHTTPServer(("127.0.0.1", PORT), DemoShopHandler)
    print(f"Demo Shop backend listening on http://127.0.0.1:{PORT}", flush=True)
    server.serve_forever()
