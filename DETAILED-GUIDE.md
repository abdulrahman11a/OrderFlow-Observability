# ShopFlow Observability — الدليل التفصيلي الكامل

الملف ده بيشرح **كل سطر كود** في المشروع، وليه مكتوب كده، وهيحصل إيه لو شيلته.
الهدف إن الطالب يقدر يفتح أي ملف ويعرف بالظبط بيعمل إيه من غير ما يحفظ.

> ملاحظة: الـ README.md التاني (المختصر) فيه خطوات التشغيل بس.
> الملف ده هو "اتعلم ليه" مش "شغّل بسرعة".

---

## 1. `Telemetry/ShopFlowTelemetry.cs` — نقطة البداية الحقيقية

```csharp
public const string ServiceName = "ShopFlow.Api";

public static readonly ActivitySource ActivitySource = new(ServiceName);
public static readonly Meter Meter = new(ServiceName);
```

فكّر في `ActivitySource` و `Meter` كـ **"مصنع"**:
- `ActivitySource` = المصنع اللي بيطلع منه كل الـ Spans (أجزاء الـ Trace).
- `Meter` = المصنع اللي بيطلع منه كل الـ Metrics (الأرقام).

ليه عاملينهم `static readonly` وفي كلاس منفصل؟
لأن OpenTelemetry بيشتغل بمبدأ: "أي span/metric بيتعمل من `ActivitySource` اسمه X،
هعرضه بس لو قلت للـ Pipeline (في `Program.cs`) `.AddSource("X")`".
يعني الاسم `ServiceName` هنا لازم يتطابق حرفيًا مع اللي هتكتبه في `Program.cs`،
عشان كده حطيناه `const` في مكان واحد بدل ما نكتبه Hardcoded في كل ملف ونغلط في حرف.

```csharp
public static readonly Counter<long> OrdersCreated =
    Meter.CreateCounter<long>("orders.created", description: "...");
```

`Counter<long>` = رقم بيزيد بس ومبينزلش (زي عداد السيارات في الطريق). كل مرة Order
تتعمل بنجاح بتكتب `OrdersCreated.Add(1, ...)` واللي بيحصل فعليًا:
1. الرقم ده بيتجمع جوه الـ Meter.
2. لما Prometheus يعمل scrape لـ `/metrics`، هيلاقي:
   ```
   orders_created_total{payment_method="card"} 1240
   ```
3. في Grafana تقدر تعمل عليه Graph مباشرة.

```csharp
public static readonly Histogram<double> PaymentDuration =
    Meter.CreateHistogram<double>("payment.duration", unit: "ms", ...);
```

`Histogram` مختلف عن `Counter`: مش رقم بيزيد بس، ده بيسجل **توزيع** قيم
(كل عملية دفع أخدت كام ميلي ثانية) عشان تقدر تحسب منه P50/P95/P99 بعدين في
Prometheus/Grafana. لو استخدمنا `Counter` هنا كنا هنعرف "عدد مرات الدفع" بس،
مش "قد إيه كل مرة استغرقت".

**لو شيلت الملف ده كله:** الكود هيكمل يشتغل عادي (لأن `AddAspNetCoreInstrumentation()`
لوحدها بتديك traces/metrics تلقائية للـ HTTP)، لكن هتفقد أي رؤية على منطق
الـ Business بتاعك (orders.created, payment.duration, والـ spans المسماة زي
"CheckInventory"). يعني هترجع لـ Monitoring عادي بدل Observability حقيقي.

---

## 2. `Models/Models.cs` — العقود بين أجزاء النظام

```csharp
public record Product(int Id, string Name, decimal Price, int Stock);
```

استخدمنا `record` مش `class` عمدًا: الـ `record` بيديك `Equals`/`ToString` مجانًا،
ومهم هنا لأن الـ Logging بيطبع الـ object أحيانًا — تجربة أنضف مع `record`.

```csharp
public class InsufficientStockException(int productId)
    : Exception($"Product {productId} is out of stock");

public class PaymentFailedException(string reason)
    : Exception($"Payment failed: {reason}");
```

دي "Primary Constructor" بتاعة C# 12 — نفس معنى:
```csharp
public class InsufficientStockException : Exception
{
    public InsufficientStockException(int productId)
        : base($"Product {productId} is out of stock") { }
}
```
لكن أقصر. **ليه عاملين Exception مخصوصة بدل `throw new Exception("...")` عادي؟**
عشان في `Program.cs` نقدر نـ `catch` كل نوع لوحده ونرجّع الـ HTTP status code
المناسب له (409 Conflict للـ stock، 402 Payment Required للدفع). لو استخدمنا
`Exception` عام، كنا هنرجع 500 لكل حاجة وده غلط منطقيًا.

---

## 3. `Services/ProductService.cs` — الـ "قاعدة بيانات" المزيفة

```csharp
private readonly List<Product> _products = new()
{
    new Product(1, "Wireless Mouse", 250, 40),
    new Product(2, "Mechanical Keyboard", 950, 15),
    new Product(3, "USB-C Hub", 400, 0), // out of stock عمدًا
};
```

منتج رقم 3 مخزونه صفر **عن قصد** — ده مش خطأ. بيخليك تجرب مسار الفشل
(`InsufficientStockException`) من غير ما تحتاج تعدّل داتا أو تعمل setup إضافي.

```csharp
public Product? GetById(int id)
{
    using var activity = ShopFlowTelemetry.ActivitySource.StartActivity("ProductService.GetById");
    activity?.SetTag("product.id", id);
    Thread.Sleep(10);
    ...
}
```

- `using var activity = ...StartActivity(...)`: بيفتح Span اسمه `ProductService.GetById`.
  الـ `using` معناها لما الكود يخرج من الـ method (نهاية الـ scope)، الـ Span يقفل
  نفسه تلقائيًا ويسجل مدته. مفيش `activity.Stop()` محتاج تكتبه يدويًا.
- `activity?.SetTag(...)`: بتضيف "خاصية" على الـ Span زي `product.id = 3`.
  في Jaeger لما تفتح الـ Span هتلاقي الـ tag ده تحت "Tags" — بيساعدك تعرف مين
  الطلب اللي كان بيدور على منتج مين وقت المشكلة.
- الـ `?` بعد `activity`: لو مفيش حد بيسمع (Listener) على الـ ActivitySource
  ده، `StartActivity` بترجع `null` (توفير performance)، فـ `?.` بتمنع
  NullReferenceException.
- `Thread.Sleep(10)`: مزيّف عمدًا، بيحاكي "استعلام قاعدة بيانات حقيقي بياخد وقت".
  لو استبدلت الكلاس ده بـ EF Core حقيقي، السطر ده يتشال والـ instrumentation
  التلقائية لـ EF Core هي اللي هتظهر المدة الحقيقية.

---

## 4. `Services/PaymentService.cs` — "القنبلة الموقوتة" بتاعة الـ Incident

```csharp
_delayMs = int.TryParse(Environment.GetEnvironmentVariable("SHOPFLOW_PAYMENT_DELAY_MS"), out var d) ? d : 150;
_failRate = double.TryParse(Environment.GetEnvironmentVariable("SHOPFLOW_PAYMENT_FAIL_RATE"), out var f) ? f : 0.0;
```

بنقرأ الإعدادات من الـ Environment Variables مش من الكود نفسه. ده اللي بيخليك
تعمل "Production Incident" وأنت شغّال — تغيّر قيمة في `docker-compose.yml`،
تعمل `docker compose up -d` تاني، وبس، من غير ما تلمس أي سطر C#. ده Practice
حقيقي — في الشغل الفعلي كتير من الإعدادات بتتغير بالـ config/env مش بإعادة نشر الكود.

```csharp
using var activity = ShopFlowTelemetry.ActivitySource.StartActivity("PaymentService.Charge", ActivityKind.Client);
```

لاحظ الفرق عن `ProductService`: هنا حاطين `ActivityKind.Client` صراحةً.
ده بيقول لـ OpenTelemetry "الـ Span ده معناه إحنا بنستنى رد من حاجة برانية"
(زي API خارجي). في أدوات الـ Tracing المتقدمة، الفرق ده بيتلوّن بشكل مختلف
وبيتفهرس مختلف، عشان تقدر تفلتر "ورّيني كل الـ Client calls البطيئة" لوحدها.

```csharp
await Task.Delay(_delayMs); // this line IS the "External Payment API"
```

السطر ده حرفيًا هو الـ "External Payment API" اللي شايفينه في كل الديجرامات.
مفيش API حقيقي هنا — إحنا بس بنستنى وقت. ده كافي تمامًا عشان يعلّم المفهوم،
ومفيش داعي لعمل API تاني كامل بس عشان الديمو.

```csharp
if (_random.NextDouble() < _failRate)
{
    ...
    activity?.SetStatus(ActivityStatusCode.Error, "Payment declined");
    ...
    throw new PaymentFailedException("card declined");
}
```

- `_random.NextDouble()` بيرجع رقم بين 0 و 1. لو `_failRate = 0.2` يبقى
  فرصة 20% إن الشرط يتحقق ويفشل الدفع — زي رمي نرد لكل عملية دفع.
- `activity?.SetStatus(ActivityStatusCode.Error, ...)`: **مهم جدًا.** ده اللي
  بيخلي الـ Span يظهر **أحمر** في Jaeger. من غير السطر ده، الـ Span هيظهر
  عادي حتى لو فيه Exception، وهتضيع وقت تدور على المشكلة بعينك بدل ما
  Jaeger يوريهالك مباشرة.

```csharp
ShopFlowTelemetry.PaymentDuration.Record(
    sw.Elapsed.TotalMilliseconds,
    new KeyValuePair<string, object?>("outcome", "failed"));
```

بنسجل المدة **حتى لو فشلت العملية**، ومعاها Tag اسمه `outcome=failed`.
كده في Grafana تقدر تعمل query يفلتر بس على `outcome="failed"` ويقولك
"عمليات الدفع اللي فشلت كانت بتاخد قد إيه بالظبط" — مش بس "كام مرة فشلت".

---

## 5. `Services/OrderService.cs` — قلب الـ Trace كله

```csharp
using var activity = ShopFlowTelemetry.ActivitySource.StartActivity("OrderService.CreateOrder");
```

ده الـ **Parent Span**. كل الـ spans التانية (Validate, CheckInventory,
PaymentService.Charge, SaveOrder) هتتحط تلقائيًا **جوه** الـ Span ده في شجرة
الـ Trace، لأن .NET بيتتبع الـ "current activity" تلقائيًا من غير ما تربطهم
يدويًا. ده اللي بيطلع لك الشكل الشجري في Jaeger:

```
OrderService.CreateOrder
  ├── Validate
  ├── ProductService.GetById
  ├── CheckInventory
  ├── PaymentService.Charge
  └── SaveOrder
```

```csharp
using (ShopFlowTelemetry.ActivitySource.StartActivity("Validate"))
{
    if (request.Quantity <= 0)
        throw new ArgumentException("Quantity must be positive");
}
```

لاحظ هنا استخدمنا `using (...)` بقوسين، مش `using var`. الفرق: لما تحط
قوسين، الـ Span بيتقفل في نهاية الـ block ده بالظبط (بعد الـ `}` اللي بعد
الشرط) — مش لما الـ method كلها تخلص. ده بيدّيك تحكم أدق في "امتى الخطوة
دي فعليًا بدأت وخلصت" داخل الـ Trace.

```csharp
try
{
    await _payments.ChargeAsync(request.CustomerId, total, request.PaymentMethod);
}
catch (PaymentFailedException)
{
    ShopFlowTelemetry.OrdersFailed.Add(1, new KeyValuePair<string, object?>("reason", "payment_failed"));
    throw;
}
```

هنا بنعمل حاجتين: (1) نسجل الـ metric إن Order فشلت بسبب الدفع تحديدًا
(مش "فشلت" بس، ده كان هيبقى مفيد أقل)، و(2) `throw;` من غير ما نكتب
`throw ex;` — الفرق مهم جدًا: `throw;` بتحافظ على الـ Stack Trace الأصلي،
بينما `throw ex;` بتمسحه وتخليك تشوف بس السطر اللي فيه `throw ex` مش
مكان الخطأ الحقيقي. تفصيلة صغيرة بتفرق كتير وقت الـ Debugging الفعلي.

```csharp
_logger.LogInformation("Order {OrderId} created successfully for Customer {CustomerId}, Total {Total}",
    order.Id, order.CustomerId, order.Total);
```

لاحظ إننا مكتبناش:
```csharp
_logger.LogInformation($"Order {order.Id} created for {order.CustomerId}");
```
ده الفرق بين **Structured Logging** و **String Interpolation** العادي:
- بالطريقة اللي استخدمناها، Serilog بيحتفظ بـ `OrderId` و `CustomerId` كـ
  **حقول منفصلة** جوه الـ log entry (مش نص واحد مدموج). فتقدر بعدين تعمل
  `WHERE OrderId = 456` في أداة زي Seq أو Loki.
- لو استخدمت `$"..."`، كل حاجة بتتحول لـ نص واحد ثابت، وتفقد القدرة على
  الفلترة/البحث الدقيق. ده بالظبط اللي شرحناه في نقطة "إيه اللي مينفعش
  يتسجل" — الفرق بين Log عشوائي وLog قابل للتحليل.

---

## 6. `Program.cs` — سلك التوصيل بين كل حاجة

### أ. Serilog

```csharp
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", ShopFlowTelemetry.ServiceName)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] ({TraceId}) {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
```

- `.Enrich.FromLogContext()`: ده اللي بيخلي `{TraceId}` في الـ template يظهر
  فعليًا. من غيره، Serilog مش هيعرف إن فيه TraceId أصلًا، والـ placeholder
  هيطبع فاضي.
- `outputTemplate`: بيتحكم في **شكل** الـ log لما يتطبع في الـ Console.
  جرّب تغيّره وشوف الفرق — مثلًا لو شلت `({TraceId})` هتفقد القدرة إنك
  تربط الـ log بالـ trace بمجرد نظرة.
- `builder.Host.UseSerilog();` (السطر اللي بعدها): ده اللي بيقول لـ ASP.NET
  Core "استخدم Serilog بدل الـ Logger الافتراضي بتاعك في كل حتة".

### ب. OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(ShopFlowTelemetry.ServiceName))
    .WithTracing(tracing => { ... })
    .WithMetrics(metrics => { ... });
```

- `.ConfigureResource(r => r.AddService(...))`: بيحط "بطاقة تعريف" على كل
  حاجة بيطلعها السيرفر ده، عشان لما يوصل لـ Jaeger/Prometheus، تعرف إنها
  جاية من `ShopFlow.Api` بالتحديد (مهم جدًا لو عندك أكتر من سيرفيس).

```csharp
tracing
    .AddSource(ShopFlowTelemetry.ServiceName)
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddOtlpExporter(o => { o.Endpoint = new Uri(...); });
```

- `.AddSource(...)`: هنا بالظبط بنقول "اسمعني على أي Span بيتعمل من
  الـ ActivitySource بتاعنا". لو نسيت السطر ده، كل الـ spans اليدوية
  (Validate, CheckInventory...) مش هتظهر خالص في Jaeger — بس اللي تلقائي
  هيفضل شغال.
- `.AddAspNetCoreInstrumentation()`: دي مكتبة جاهزة بتعمل Span تلقائي
  لكل HTTP request داخل، من غير ما تكتب كود إضافي.
- `.AddHttpClientInstrumentation()`: نفس الفكرة بس لأي `HttpClient` بتستخدمه
  عشان تكلم API خارجي حقيقي — دلوقتي مش مستخدمة فعليًا (لأن Payment مزيفة)،
  بس لو ربطت Payment API حقيقي بـ `HttpClient`، هتاخد الـ span تلقائيًا.
- `.AddOtlpExporter(...)`: ده اللي بيبعت كل الـ traces فعليًا لـ Jaeger،
  عن طريق بروتوكول اسمه OTLP (OpenTelemetry Protocol) على البورت 4317.

```csharp
metrics
    .AddMeter(ShopFlowTelemetry.ServiceName)
    .AddAspNetCoreInstrumentation()
    .AddRuntimeInstrumentation()
    .AddPrometheusExporter();
```

- `.AddMeter(...)`: زي `.AddSource` بالظبط بس للـ Metrics — من غيرها،
  `orders.created` و `payment.duration` مش هيظهروا في `/metrics`.
- `.AddRuntimeInstrumentation()`: بتديك مجانًا metrics عن الـ .NET runtime
  نفسه (GC, Threads, Memory) — مفيدة جدًا وقت الـ Incident عشان تستبعد
  "المشكلة في السيرفر نفسه" زي ما شرحنا في خطوة "CPU 35%, Memory 40%".
- `.AddPrometheusExporter()`: ده اللي بيفتح endpoint اسمه `/metrics`
  بصيغة يفهمها Prometheus مباشرة (بدون OTLP هنا — Prometheus بيعمل "Pull"
  مش "Push").

### ج. Middleware تسجيل الطلبات

```csharp
app.Use(async (context, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try { await next(); }
    finally
    {
        sw.Stop();
        Log.Information("HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms", ...);
    }
});
```

- ليه `try/finally` مش `try/catch`؟ عشان إحنا مش عايزين "نمسك" الخطأ
  ونمنعه — عايزينه يكمل يطلع طبيعي (عشان ASP.NET Core يرجع 500 مثلًا) —
  إحنا بس عايزين نضمن إننا **نسجل** الـ log حتى لو حصل Exception. الـ
  `finally` بيتنفذ في الحالتين (نجاح أو فشل).
- `await next();`: ده اللي بيكمل السلسلة لباقي الـ Middlewares لحد ما يوصل
  للـ Endpoint نفسه. لو مكتبتهاش، الطلب يقف هنا ومايوصلش للـ API خالص.

### د. الـ Endpoints

```csharp
app.MapPost("/api/orders", async (CreateOrderRequest request, IOrderService orders) =>
{
    try
    {
        var order = await orders.CreateOrderAsync(request);
        return Results.Created($"/api/orders/{order.Id}", order);
    }
    catch (InsufficientStockException ex) => Results.Conflict(new { error = ex.Message });
    ...
});
```

لاحظ إن الـ Endpoint نفسه **مالوش أي علاقة بالـ Telemetry مباشرة** — كله
مبني جوه `OrderService`. ده معماريًا مهم: الـ Endpoint شغلته الوحيدة إنه
يحوّل النتيجة/الخطأ لـ HTTP status code مناسب. أي حد يقرا الـ Endpoint
هيفهم "إيه اللي المفروض يرجع للـ Client" من غير ما يتغرق في تفاصيل
الـ Tracing.

---

## 7. `docker-compose.yml` — إزاي كل حاجة بتتكلم مع بعض

```yaml
api:
  build: ./ShopFlow.Api
  ports:
    - "5000:8080"
  environment:
    - Otlp__Endpoint=http://jaeger:4317
```

- `"5000:8080"`: يعني "البورت 8080 جوه الـ container (اللي الـ API شغال
  عليه) هيبقى متاح على جهازك على البورت 5000". عشان كده بتفتح
  `localhost:5000` مش `localhost:8080`.
- `Otlp__Endpoint`: لاحظ الـ Double Underscore (`__`). دي طريقة .NET إنه
  يحوّل `Otlp:Endpoint` (اللي في `appsettings.json`) لصيغة تصلح كـ
  Environment Variable (لأن الـ `:` مش مسموح بيه في أسماء متغيرات
  البيئة على كل الأنظمة). .NET بيحولها تلقائي.
- `http://jaeger:4317`: هنا بنستخدم اسم الـ service (`jaeger`) مش
  `localhost`. جوه شبكة Docker Compose، كل service بيقدر يوصل للتاني
  باسمه مباشرة — Docker بيعمل DNS داخلي لوحده.

```yaml
jaeger:
  image: jaegertracing/all-in-one:1.57
  ports:
    - "16686:16686"
    - "4317:4317"
```

`all-in-one` معناها إن الـ image ده فيه كل مكونات Jaeger (Collector,
Query, UI, Storage in-memory) في container واحد — ممتاز للتعلم/الديمو،
مش هتستخدمه بالشكل ده في Production حقيقي (هناك بتفصل الـ Storage على
Elasticsearch/Cassandra مثلًا).

```yaml
prometheus:
  volumes:
    - ./prometheus.yml:/etc/prometheus/prometheus.yml:ro
```

بنـ"mount" ملف الإعدادات بتاعنا جوه الـ container. الـ `:ro` معناها
Read-Only — الـ container مايقدرش يعدّل الملف ده، بس يقراه.

---

## 8. `prometheus.yml`

```yaml
scrape_configs:
  - job_name: "shopflow-api"
    static_configs:
      - targets: ["api:8080"]
```

Prometheus بيشتغل بمبدأ **Pull** (عكس فكرة إنك تبعتله الداتا). كل
`scrape_interval` (5 ثواني هنا)، هو اللي بيروح بنفسه لـ `api:8080/metrics`
ويسحب القيم. لو الـ API واقع وقت الـ scrape، هتلاقي "gap" في الجراف —
وده في حد ذاته معلومة مفيدة (السيرفر كان down).

---

## 9. `load-test.js` (k6)

```javascript
export const options = {
  vus: 20,
  duration: '30s',
};
```

`vus` = Virtual Users، يعني k6 هيحاكي 20 مستخدم بيضربوا الـ API **في نفس
الوقت**، مستمرين لمدة 30 ثانية. ده اللي بيولّد الحمل الكافي عشان تشوف
فرق حقيقي بين Average وP95/P99.

```javascript
check(orderRes, { 'POST /orders is 201 or handled error': (r) => r.status < 500 });
```

`check` مش بتوقف الاختبار لو فشل — بس بتسجل نسبة نجاح في تقرير k6 النهائي.
هنا بنقبل أي status أقل من 500 (يعني حتى 409 Conflict أو 402 Payment
Required مقبولين) لأننا عارفين إن فيه سيناريوهات فشل متعمدة (المنتج
الناقص المخزون). الفشل الحقيقي اللي بندور عليه هو 500 (يعني السيرفر
اتعطل فعليًا).

---

## خلاصة: امتى تلمس أي ملف

| عايز تعمل إيه | تروح على |
|---|---|
| تضيف endpoint جديد | `Program.cs` |
| تضيف خطوة جديدة في الـ Order flow (زي Send Notification) | `OrderService.cs` |
| تضيف metric/counter جديد | `ShopFlowTelemetry.cs` ثم تستخدمه فين ما تحب |
| تغيّر سلوك الـ Incident (بطء/فشل) | `docker-compose.yml` → env vars بتاعة `api` |
| تغيّر شكل الـ logs | `Program.cs` → `outputTemplate` |
| تضيف dependency حقيقي (SQL/Redis) | `ProductService.cs` (تستبدلها بـ EF Core) + `docker-compose.yml` (تضيف الـ service) + Health Checks في `Program.cs` |
