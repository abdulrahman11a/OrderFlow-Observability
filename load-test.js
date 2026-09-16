import http from 'k6/http';
import { sleep, check } from 'k6';

// Run:  k6 run load-test.js
// This mixes browsing (GET /products) with checkout (POST /orders) traffic
// so you get a realistic RED-method view in Grafana: Rate, Errors, Duration.
export const options = {
  vus: 20,
  duration: '30s',
};

export default function () {
  const base = 'http://localhost:5000';

  const productsRes = http.get(`${base}/api/products`);
  check(productsRes, { 'GET /products is 200': (r) => r.status === 200 });

  const orderPayload = JSON.stringify({
    customerId: Math.floor(Math.random() * 1000),
    productId: 1,
    quantity: 1,
    paymentMethod: 'card',
  });

  const orderRes = http.post(`${base}/api/orders`, orderPayload, {
    headers: { 'Content-Type': 'application/json' },
  });
  check(orderRes, { 'POST /orders is 201 or handled error': (r) => r.status < 500 });

  sleep(1);
}
