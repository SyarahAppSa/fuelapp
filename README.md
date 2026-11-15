# منظومة فيول آب — FULL APP

منظومة لضبط نقل الوقود ومطابقة الكميات: من التعبئة (محطة / حوش ميسان / أرامكو) حتى التسليم للعميل، مع العداد التراكمي، الاعتماد، والتدقيق.

| الجزء | التقنية | المجلد |
|---|---|---|
| الخادم المركزي وقواعد العمل | C# · .NET 8 · ASP.NET Core · EF Core | `backend/` |
| لوحة التحكم على الويب | TypeScript · React 18 · Vite | `web/` |
| تطبيق السائق (آيفون وأندرويد) | Dart · Flutter | `mobile/` |
| قاعدة البيانات | PostgreSQL 16 | `db/schema.sql` + ترحيلات EF Core |

كل الأطراف تتصل بالخادم عبر API واحدة. الخادم هو من يطبّق القواعد (رفض قراءة العداد الأقل، ترتيب خطوات الرحلة، المطابقة، الصلاحيات)، فلا يمكن تجاوزها من أي واجهة.

---

## البيئة التجريبية والإطلاق

- `docs/RELEASE.md` يشرح:
  - الخادم والنطاق؛
  - أول تشغيل؛
  - النشر بأرقام نسخ (`deploy/deploy.sh`) مع نسخة احتياطية قبل كل نشر، وفحص آلي، ورجوع تلقائي عند الفشل؛
  - الرجوع اليدوي (`deploy/rollback.sh`)؛
  - المراقبة (`deploy/monitor.sh`) والتنبيهات؛
  - بناء تطبيق الجوال للاختبار وللمتاجر؛
  - خطة الإطلاق التدريجي.
- **بيئة تجريبية ببيانات اختبار:**

  ```bash
  COMPOSE_FILES="-f docker-compose.yml -f docker-compose.staging.yml" deploy/deploy.sh 1.0.2
  ```

- **نتائج الاختبارات وحكم الجاهزية:** `docs/تقرير-الجاهزية.pdf`، والمخرجات الأصلية في `docs/test-results/`.

## التشغيل (Docker)

يحتاج Docker Engine أو Docker Desktop.

```bash
cp .env.example .env
```

املأ كل قيمة في `.env` بقيمة عشوائية (`openssl rand -hex 32`)، واكتب في `BOOTSTRAP_*` اسم الشركة الأولى ومديرها. ضع شهادة TLS في `deploy/certs/` (`fullchain.pem` و `privkey.pem`)، أو لتجربة محلية فقط:

```bash
sh deploy/gen-dev-cert.sh
```

```bash
docker compose up -d --build
```

- اللوحة على https://localhost، والطلبات عبر HTTP تُحوَّل إلى HTTPS.
- عند أول تشغيل تُنشأ الجداول، وتُطبَّق حماية قاعدة البيانات، وتُنشأ الشركة الأولى ومديرها من `BOOTSTRAP_*`. احذف هذه القيم بعد أول تشغيل ناجح.
- أول دخول للمدير يطلب تفعيل التحقق بخطوتين: امسح رمز QR بتطبيق Google أو Microsoft Authenticator، واحفظ رموز الاسترداد.
- لا يُنشر للخارج إلا nginx. الخادم وقاعدة البيانات على شبكات داخلية، وصفحة Swagger مغلقة للعامة.

### بيانات تجريبية (بيئة اختبار فقط)

`SEED_DEMO=true` و `SEED_DEMO_ALLOW=true` تضيفان شركتين تجريبيتين لاختبار العزل بينهما. كلمة المرور لكل الحسابات `FuelUp@2026`، والخادم يرفض البيانات التجريبية في الإنتاج ما لم يُسمح بها صراحة.

| المستخدم | الشركة | الدور |
|---|---|---|
| `admin` | فيول آب | مدير المنظومة (يطلب التحقق بخطوتين) |
| `supervisor` | فيول آب | مشرف التشغيل (يطلب التحقق بخطوتين) |
| `driver1` / `driver2` | فيول آب | سائقان |
| `gulf.admin` / `gulf.driver` | الخليج للنقل | شركة ثانية منفصلة |

### الأمان والنسخ الاحتياطي

اقرأ `docs/SECURITY.md`. فيه:
- طبقات الحماية؛
- الأسرار وتدويرها؛
- النسخ الاحتياطي المشفر (`deploy/backup.sh`) واختبار الاستعادة (`deploy/restore-test.sh`)؛
- خطة الاستجابة للحوادث؛
- قائمة ما قبل الإطلاق.

---

## التشغيل للتطوير (دون Docker)

### 1) قاعدة البيانات: حساب مالك وحساب تشغيل محدود

```bash
sudo -u postgres psql -c "CREATE ROLE fuelup LOGIN PASSWORD 'fuelup'" -c "CREATE ROLE fuelup_app LOGIN PASSWORD 'fuelup_app_dev' NOBYPASSRLS" -c "CREATE DATABASE fuelup OWNER fuelup" -c "GRANT CONNECT ON DATABASE fuelup TO fuelup_app"
```

(كلمتا المرور هاتان للتطوير المحلي فقط، وموجودتان في `appsettings.Development.json`.)

### 2) الخادم

```bash
cd backend && dotnet run --project src/FuelUp.Api
```

يعمل على http://localhost:5004 مع Swagger. في التطوير يُولَّد مفتاح توقيع مؤقت في كل تشغيل، وتُضاف البيانات التجريبية.

### 3) الاختبارات

```bash
cd backend && dotnet test
```

```bash
cd tests && node e2e.mjs && node concurrency.mjs && node security.mjs
```

- `dotnet test`: اختبارات القواعد والحماية.
- `e2e.mjs`: دورة العمل كاملة.
- `concurrency.mjs`: تزامن العداد.
- `security.mjs`: هجمات فعلية على الخادم وقاعدة البيانات.
- شغّل الاختبارات على قاعدة بيانات جديدة.

### 4) لوحة الويب

```bash
cd web && npm install && npm run dev
```

### 5) تطبيق السائق

```bash
cd mobile && flutter pub get
```

```bash
flutter run --dart-define=API_URL=http://10.0.2.2:5004
```

نسخة الإصدار ترفض أي عنوان غير HTTPS:

```bash
flutter build apk --release --obfuscate --split-debug-info=build/symbols --dart-define=API_URL=https://fuelup.example.com
```

---

## هيكل المشروع

```
backend/
  src/FuelUp.Domain          الكيانات والحالات وقواعد العمل (بلا اعتماد على قاعدة بيانات)
  src/FuelUp.Infrastructure  EF Core وPostgreSQL والترحيلات وخدمات الرحلة والعداد والإسناد
  src/FuelUp.Api             نقاط الـAPI والدخول وتقرير التعبئة وExcel
  tests/FuelUp.Tests         اختبارات القواعد (xUnit)
web/src
  api.ts                     الاتصال بالخادم وتجديد الجلسة
  pages/                     لوحة التحكم، الرحلات، تقرير عمليات التعبئة
mobile/lib
  api.dart                   الاتصال بالخادم ورفع الصور
  screens/                   الدخول، الرئيسية، خطوات الرحلة
db/schema.sql                المخطط الكامل (35 جدولًا) لمن يريد إنشاءه يدويًا
deploy/                      nginx (TLS)، أدوار قاعدة البيانات، النسخ الاحتياطي المشفر واختبار الاستعادة
tests/                       الاختبار الوظيفي والهجومي
.github/workflows/           خط الفحص الأمني المستمر
docs/                        الوثيقة التقنية، وصف النظام، دليل الأمان وتقريره
```

## ما في هذه النسخة وما بعدها

هذه **المرحلة الأولى**. فيها: الدخول والصلاحيات لكل قسم، البيانات الأساسية، الإسناد، دورة الرحلة كاملة، العداد التراكمي مع القراءة التأسيسية والتصفير، الاعتماد والمراجعة، نقل الطلب بين السائقين، الإشعارات، التدقيق، تقرير عمليات التعبئة مع Excel، ولوحة التحكم.

المراحل التالية (جداولها موجودة في المخطط): ملف المركبة عبر QR، العُهد وقطع الغيار، الربط مع آفاقي، العمل دون إنترنت في التطبيق، إشعارات الجوال (Firebase)، تقارير PDF من الخادم. التفاصيل في `docs/الوثيقة-التقنية.pdf`.
