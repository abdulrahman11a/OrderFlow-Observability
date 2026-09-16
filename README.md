# ShopFlow Observability Lab

مشروع صغير Production-like يوضّح الـ 3 Pillars (Logs / Metrics / Traces) عمليًا،
باستخدام نفس الـ Stack اللي اتكلمنا عليه: ASP.NET Core + Serilog + OpenTelemetry +
Prometheus + Grafana + Jaeger + Docker Compose + k6 + Postman.

## 1. تشغيل الـ Stack

```bash
docker compose up --build
```

هيشتغل عندك:

| Service     | URL                          |
|-------------|-------------------------------|
| API         | http://localhost:5000        |
| Jaeger UI   | http://localhost:16686       |
| Prometheus  | http://localhost:9090        |
| Grafana     | http://localhost:3000 (admin/admin) |

## 2. جرّب الـ API (Postman collection مرفقة: `ShopFlow.postman_collection.json`)

```bash
curl http://localhost:5000/api/products
curl -X POST http://localhost:5000/api/orders \
  -H "Content-Type: application/json" \
  -d '{"customerId":1,"productId":1,"quantity":1,"paymentMethod":"card"}'
```

- المنتج `id=3` (USB-C Hub) متعمل عليه Stock=0 عشان تجرب مسار الفشل (`InsufficientStockException`).
- كل Request بيتسجله في الـ Console بصيغة Structured، ومعاه الـ TraceId — لو نسخته وحطيته
  في Jaeger UI هتلاقي نفس الـ Request بالضبط كـ waterfall.

## 3. شوف الـ Trace في Jaeger

افتح http://localhost:16686 → اختار Service = `ShopFlow.Api` → Find Traces.
هتلاقي كل POST /orders متقسم لـ spans زي بالظبط الديجرام اللي اتشرح:

```
OrderService.CreateOrder
  ├── Validate
  ├── ProductService.GetById
  ├── CheckInventory
  ├── PaymentService.Charge      <-- ده اللي هيبقى بطيء في الـ Incident
  └── SaveOrder
```

## 4. شوف الـ Metrics

- Raw: `curl http://localhost:5000/metrics`
- Prometheus: http://localhost:9090 → دور على `orders_created_total` أو
  `http_server_request_duration_seconds` وشوف الـ P95/P99 بنفسك.
- Grafana: وصّل Data Source جديد (Prometheus, URL = `http://prometheus:9090`) واعمل
  Dashboard بسيط: Request Rate, Error Rate, P95 Duration = نفس اللي في الديجرام.

## 5. اعمل الـ Production Incident بنفسك 🔥

في `docker-compose.yml` غيّر الـ env vars بتاعة الـ `api` service:

```yaml
- SHOPFLOW_PAYMENT_DELAY_MS=2500   # كل عملية دفع تاخد 2.5 ثانية
- SHOPFLOW_PAYMENT_FAIL_RATE=0.2   # 20% من عمليات الدفع تفشل
```

```bash
docker compose up --build -d
k6 run load-test.js
```

دلوقتي:
1. **Grafana** هيوريك P95/P99 طالعين وError Rate زايد — دي الـ *Monitoring* (فيه حاجة غلط).
2. **Jaeger** هيوريك بالظبط إن الوقت كله راكب على span اسمه `PaymentService.Charge` —
   دي الـ *Observability* (ليه غلط، وفين بالظبط).
3. **الـ Console logs** هتشوف فيها `Payment declined` أو مدة `ElapsedMs` عالية جدًا،
   مربوطة بنفس الـ TraceId بتاع الـ trace اللي شايفه في Jaeger.

ده بالظبط الـ Flow اللي اتشرح في الديجرام:
`Logs -> Metrics -> Trace -> Root Cause`.

## 6. Health Checks

```bash
curl http://localhost:5000/health
```

نقطة البداية بس — لو عايز تفرّق فعليًا بين Liveness وReadiness (زي ما اتشرح)،
ضيف `AddCheck` لكل dependency حقيقي (SQL/Redis) واستخدم
`.AddCheck(..., tags: ["ready"])` مع `MapHealthChecks("/health/ready", new(){ Predicate = c => c.Tags.Contains("ready") })`.

## بنية المشروع

```
ShopFlow.Api/
  Program.cs              # Serilog + OpenTelemetry wiring + endpoints
  Models/Models.cs
  Services/
    ProductService.cs     # in-memory "DB"
    PaymentService.cs     # الـ dependency اللي بنعمّلها Incident
    OrderService.cs        # Validate -> GetProduct -> CheckInventory -> Payment -> Save
  Telemetry/ShopFlowTelemetry.cs   # ActivitySource + Meter المشتركين
docker-compose.yml
prometheus.yml
load-test.js
ShopFlow.postman_collection.json
```
