#!/usr/bin/env python3
"""
Advanced Demo Shop target backend for Beesharp (listens on 127.0.0.1:5399).

Simulates realistic high-volume load-test behavior (~800+ bees/min):
- POST /api/auth/token          -> fast (15-35ms)
- GET  /api/products            -> bimodal: fast cache hit (20-55ms) + cache miss (220-480ms)
- GET  /api/products/{id}       -> fast (18-50ms), ~1.5% 404 SKU discontinued
- POST /api/cart                -> moderate (35-80ms), slows under inventory pressure
- POST /api/inventory/reserve   -> shared bottleneck! <= 14 concurrent: 45-100ms.
                                   Bursts (> 14 concurrent) ramp latency 280-1200ms and emit
                                   distinct cascading errors: 429 (rate limit), 503 (pool saturated),
                                   and 504 (DB lock wait timeout).
- POST /api/orders              -> 70-140ms normally; under pressure slows and emits 502/503.
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
_inv_pressure = 0.0


def _enter_inventory():
    global _inv_in_flight, _inv_pressure
    with _lock:
        _inv_in_flight += 1
        cur = _inv_in_flight
        if cur > 14:
            _inv_pressure = min(1.0, _inv_pressure + 0.06 * (cur - 14))
        return cur, _inv_pressure


def _leave_inventory():
    global _inv_in_flight
    with _lock:
        _inv_in_flight = max(0, _inv_in_flight - 1)


def _decay_pressure():
    global _inv_pressure
    while True:
        time.sleep(0.35)
        with _lock:
            if _inv_in_flight <= 14:
                _inv_pressure = max(0.0, _inv_pressure - 0.07)


class DemoShopHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, format, *args):
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
            miss_chance = 0.14 + pressure * 0.28
            if random.random() < miss_chance:
                time.sleep(random.uniform(0.22, 0.46) + pressure * 0.25)
                cache = "miss"
            else:
                time.sleep(random.uniform(0.02, 0.055))
                cache = "hit"
            self._send_json(200, {"items": [f"SKU-{i:03d}" for i in range(1, 13)], "cache": cache})
            return

        if path.startswith("/api/products/"):
            time.sleep(random.uniform(0.018, 0.05) + pressure * 0.05)
            if random.random() < 0.015:
                self._send_json(404, {"error": "SKU discontinued or out of catalog"})
                return
            sku = path.rsplit("/", 1)[-1]
            self._send_json(200, {"sku": sku, "price": 29.95, "inStock": True})
            return

        self._send_json(404, {"error": "Not found"})

    def do_POST(self):
        self._read_body()
        path = urlparse(self.path).path

        if path == "/api/auth/token":
            time.sleep(random.uniform(0.015, 0.038))
            self._send_json(200, {"token": f"tok-{random.randint(1000, 9999)}"})
            return

        if path == "/api/cart":
            with _lock:
                pressure = _inv_pressure
            time.sleep(random.uniform(0.035, 0.08) + pressure * random.uniform(0.16, 0.42))
            self._send_json(200, {"cartId": f"cart-{random.randint(100, 999)}", "pressure": round(pressure, 2)})
            return

        if path == "/api/inventory/reserve":
            cur, pressure = _enter_inventory()
            try:
                if cur <= 14 and pressure < 0.22:
                    time.sleep(random.uniform(0.045, 0.105))
                    self._send_json(200, {"reserved": True, "inFlight": cur})
                else:
                    extra = max(0, cur - 14) * 0.04 + pressure * 0.65
                    time.sleep(random.uniform(0.24, 0.58) + min(1.0, extra))
                    fail_prob = min(0.52, 0.10 + pressure * 0.36)
                    if random.random() < fail_prob:
                        r = random.random()
                        if pressure < 0.55 or r < 0.40:
                            self._send_json(429, {"error": "Inventory rate limit exceeded", "inFlight": cur})
                        elif r < 0.78:
                            self._send_json(503, {"error": "Inventory connection pool saturated", "inFlight": cur})
                        else:
                            self._send_json(504, {"error": "Inventory DB row lock wait timeout", "inFlight": cur})
                    else:
                        self._send_json(200, {"reserved": True, "inFlight": cur, "degraded": True})
            finally:
                _leave_inventory()
            return

        if path == "/api/orders":
            with _lock:
                pressure = _inv_pressure
            time.sleep(random.uniform(0.07, 0.145) + pressure * random.uniform(0.30, 0.85))
            if pressure > 0.32 and random.random() < pressure * 0.38:
                if random.random() < 0.55:
                    self._send_json(502, {"error": "Payment gateway upstream timeout"})
                else:
                    self._send_json(503, {"error": "Order circuit breaker open"})
                return
            self._send_json(201, {"orderId": f"ORD-{random.randint(10000, 99999)}"})
            return

        self._send_json(404, {"error": "Not found"})


if __name__ == "__main__":
    t = threading.Thread(target=_decay_pressure, daemon=True)
    t.start()
    server = ThreadingHTTPServer(("127.0.0.1", PORT), DemoShopHandler)
    print(f"Advanced Demo Shop backend listening on http://127.0.0.1:{PORT}", flush=True)
    server.serve_forever()
