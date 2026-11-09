# راهنمای نصب راوی روی سرور ویندوز (IIS + یک سرویس برای workers)

> برای نسخه‌های 0.13.x نوشته شده (۱۴۰۵/۰۸/۱۷). هنوز هیچ نصب واقعی انجام نشده است؛ این راهنما را فهرست بررسی بدانید و هر چه در اجرای اول غلط بود گزارش کنید. نسخهٔ انگلیسی: `INSTALL_WINDOWS_IIS.md`.

## ۰. شکل کلی نصب

```
مرورگر ──https──► سایت IIS به نام RaaviWeb  (مسیر D:\Raavi\web، bindingها: admin.<دامنه> و *.<دامنه>)
                   ├─ فایل‌های استاتیک رابط کاربری (و بازگشت به index.html)
                   └─ مسیرهای api/ hubs/ health/ ──پروکسی (ARR با حفظ Host)──►  سایت IIS به نام RaaviApi
                                                                                 (مسیر D:\Raavi\api، آدرس http://127.0.0.1:5180، in-process)
سرویس ویندوز RaaviWorkers (با NSSM) – outbox، اعلان‌ها، کارهای زمان‌بندی‌شده (بدون وب و بدون پورت)
SQL Server ◄── API و workers (لاگین raavi_app)      migrator (لاگین raavi_migrator، دستی اجرا می‌شود)
D:\Raavi\sandbox\ – تنظیمات، کلیدهای رمزنگاری، رازها (مال شماست؛ در ارتقا حفظ می‌شود)
```

چرا دو سایت IIS؟ رابط کاربری و API باید با **یک نام میزبان** در دسترس باشند (کوکی‌ها فقط برای همان میزبان معتبرند و فضای کاری از روی نام میزبان تعیین می‌شود). پس سایت عمومی، مسیرهای API را به سایت داخلی API می‌فرستد.

## ۱. پیش‌نیازهای سرور

- IIS با این موارد: **URL Rewrite**، **Application Request Routing (ARR)**، قابلیت **WebSocket Protocol** و **.NET 10 Hosting Bundle** (ماژول ASP.NET Core را هم نصب می‌کند). با `dotnet --list-runtimes` باید `Microsoft.AspNetCore.App 10.x` ببینید. بعد از نصب Hosting Bundle دستور `iisreset` را بزنید.
- SQL Server نسخهٔ 2016 یا بالاتر (2022 پیشنهاد می‌شود). مقدار `Raavi:Database:CompatibilityLevel` را برابر سطح سازگاری واقعی دیتابیس بگذارید (۱۳۰ برای 2016، ۱۴۰، ۱۵۰، ۱۶۰ برای 2022).
- **NSSM** (از `nssm.cc`) فقط برای سرویس workers.
- Node.js نسخهٔ 22.18 یا بالاتر، فقط اگر بعداً ویرایش هم‌زمان را روشن کنید.
- DNS برای `admin.<دامنه>` و `*.<دامنه>`، و گواهی TLS wildcard که در IIS import شده باشد.

## ۲. باز کردن بسته و نصب اول

```powershell
Expand-Archive raavi-0.13.2.zip C:\Deploy\raavi-0.13.2
powershell -ExecutionPolicy Bypass -File C:\Deploy\raavi-0.13.2\scripts\release\Update-Raavi.ps1 `
  -PackageDirectory C:\Deploy\raavi-0.13.2 -InstallDirectory D:\Raavi -SkipNpm
```

این دستور `D:\Raavi\{api,workers,migrator,web,scripts,...}` و `D:\Raavi\sandbox\{config,data,secrets}` را می‌سازد. پوشهٔ `config` از تنظیمات پیش‌فرض نسخه پر می‌شود. برنامه‌ها `D:\Raavi\sandbox\config` را با نگاه به بالاتر از پوشهٔ خودشان پیدا می‌کنند؛ فقط اگر آن را جای دیگری گذاشتید، متغیر `RAAVI_SANDBOX_PATH` را بگذارید. **از `sandbox\` همراه دیتابیس backup بگیرید.** بدون `data\keys` مقادیر رمزشده (TOTP، کلیدهای AI، رازهای وب‌هوک) خوانا نیستند.

## ۳. تنظیمات (`D:\Raavi\sandbox\config\appsettings.json`)

| کلید | مقدار |
|---|---|
| `Raavi:Hosting:SystemHost` | `admin.example.com` |
| `Raavi:Hosting:TenantBaseDomains` | `["example.com"]` (فضاهای کاری می‌شوند `<slug>.example.com`) |
| `Raavi:Hosting:PublicScheme` | `https` |
| `Raavi:Hosting:KnownProxies` | `["127.0.0.1"]` (پروکسی از loopback وصل می‌شود؛ loopback به‌طور پیش‌فرض هم مورد اعتماد است) |
| `Raavi:Database:CompatibilityLevel` | مثلاً `160` |
| در `notifications.json` ← `Raavi:Email` | `Mode: Smtp` و تنظیمات SMTP؛ وگرنه ایمیل اعلان فرستاده نمی‌شود |

رازها **در فایل نوشته نمی‌شوند.** آن‌ها متغیر محیطی App Pool مربوط به API و سرویس workers هستند (بخش‌های ۵ و ۶): `RAAVI_ConnectionStrings__System`، `RAAVI_Raavi__Database__CompatibilityLevel` و `ASPNETCORE_ENVIRONMENT=Production`. فقط migrator از `RAAVI_ConnectionStrings__Migrator` استفاده می‌کند.

فهرست‌های مجاز اختیاری (خالی یعنی آن قابلیت خاموش است): `Raavi:Ai:AllowedHosts`، `Raavi:Integrations:AllowedWebhookHosts`، `Raavi:Integrations:AllowedConnectorHosts` و `Raavi:Migration:AllowedSourceHosts`.

## ۳ب. یک آدرس برای همه چیز (بدون DNS و TLS wildcard) — آدرس‌دهی مسیری و فضای پیش‌فرض

با `Addressing = Path` همهٔ فضاهای کاری زیر **یک نام میزبان** (`SystemHost`) قرار می‌گیرند؛ پس یک رکورد DNS و یک گواهی کافی است:

| چه چیزی | آدرس |
|---|---|
| فضای پیش‌فرض (اگر تنظیم شده باشد) | `https://raavi.example.com/` |
| هر فضای کاری | `https://raavi.example.com/t/<زیردامنه‌ی-فضا>/` (مثلاً `/t/dev/`) |
| بخش مدیریت سیستم | `https://raavi.example.com/system/login` |

```json
"Hosting": {
  "SystemHost": "raavi.example.com",
  "Addressing": "Path",
  "PathPrefix": "t",
  "DefaultTenantCode": "DEVELOPMENT",
  "PublicScheme": "https",
  "KnownProxies": [ "127.0.0.1" ]
}
```

- `Addressing`: `Subdomain` (پیش‌فرض: `slug.<دامنهٔ پایه>` و نیازمند DNS و TLS wildcard)، `Path` (همین بالا؛ دیگر به `TenantBaseDomains` نیاز نیست) یا `Both` (هر دو پذیرفته می‌شوند؛ لینک‌ها زیردامنه‌ای ساخته می‌شوند). دامنهٔ سفارشی تأییدشده در هر حالت کار می‌کند.
- `DefaultTenantCode`: **کد** فضایی که به هر درخواست روی همین میزبان که فضایی را نام نبرد (یعنی ریشهٔ `/`) پاسخ می‌دهد. خالی یعنی ریشه همان بخش مدیریت سیستم است (مثل قبل). وقتی پر باشد، بخش مدیریت فقط از `/system/...` در دسترس است (API آن `/system/api/...` است)؛ اگر کد وجود نداشته باشد، ریشه «فضای کاری یافت نشد» می‌دهد ولی `/system/login` همچنان کار می‌کند.
- `PathPrefix` (پیش‌فرض `t`) قابل تغییر است؛ واژه‌های `system`، `api`، `hubs`، `health`، `internal` و `assets` پذیرفته نمی‌شوند.
- لینک‌هایی که سیستم می‌سازد (دعوت‌نامه، ایمیل، «باز کردن فضا») در حالت `Path` به شکل `https://raavi.example.com/t/<slug>/` است.
- **IIS:** فایل `web.config` همراه بسته در `web\` از قبل مسیرهای `t/<slug>/api|hubs|health` و `system/api` را با مسیر اصلی به API می‌فرستد؛ قاعدهٔ اضافه یا binding اضافه لازم نیست. بعد از تغییر تنظیمات، pool مربوط به API را ری‌استارت کنید.

بهایی که می‌پردازید (مرورگر دیگر فضاها را از هم جدا نمی‌کند): همهٔ فضاها یک origin مشترک دارند، پس **یک کوکی نشست**: مرورگر در هر لحظه فقط به یک فضا (یا به بخش مدیریت) وارد است و ورود به فضای دیگر نشست قبلی را جایگزین می‌کند. همچنین حفرهٔ XSS در یک فضا می‌تواند صفحات فضای دیگر را برای همان کاربرِ واردشده بخواند. فقط وقتی `Path` را بگذارید که همهٔ فضاها متعلق به سازمان‌هایی باشند که به یک اندازه به آن‌ها اعتماد دارید (ADR-045).

## ۴. دیتابیس و migrator

۱. یک دیتابیس **خالی** بسازید (migrator جدول می‌سازد، خود دیتابیس را نمی‌سازد).

۲. migrator را **از داخل پوشهٔ خودش** اجرا کنید (تنظیمات را از پوشهٔ جاری پیدا می‌کند) با لاگینی که اجازهٔ ساخت جدول دارد:

```powershell
cd D:\Raavi\migrator
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:RAAVI_Raavi__Database__CompatibilityLevel = '160'
$env:RAAVI_ConnectionStrings__Migrator = 'Server=SQLHOST;Database=Raavi;User Id=raavi_migrator;Password=...;TrustServerCertificate=true'
dotnet .\Raavi.Migrator.dll       # در پایان می‌نویسد: Raavi database is up to date.
```

(برای Windows Authentication به‌جای کاربر و رمز بنویسید `Integrated Security=true`.) اجرای دوباره بی‌خطر است.

۳. اسکریپت‌های `D:\Raavi\scripts\sql\01-database-roles.sql` و `02-logins.sql` را اجرا کنید (توضیح در `DEPLOYMENT_GUIDE.md` بخش ۴). آن‌ها لاگین محدود `raavi_app` را می‌سازند. API و workers از **همین** لاگین استفاده می‌کنند، نه لاگین migrator.

۴. حالت FULL recovery را بگذارید و backup را زمان‌بندی کنید (`BACKUP_AND_RESTORE.md` و پوشهٔ `D:\Raavi\scripts\backup`).

## ۵. API به‌صورت سایت IIS

۱. **Application Pool** با نام `RaaviApi`: گزینهٔ .NET CLR version را روی **No Managed Code** بگذارید؛ Start Mode را `AlwaysRunning` و Idle Time-out را `0` کنید.

۲. **Site** با نام `RaaviApi`: مسیر فیزیکی `D:\Raavi\api`، همان pool، و binding از نوع **http با IP برابر 127.0.0.1 و پورت 5180** (فقط داخلی). فایل `web.config` همراه بسته برنامه را in-process اجرا می‌کند و `--urls` لازم نیست.

۳. **متغیرهای محیطی را روی خود pool بگذارید**، نه در `web.config` (چون در هر ارتقا جایگزین می‌شود):

```powershell
$a = "$env:windir\System32\inetsrv\appcmd.exe"
$cs = 'Server=SQLHOST;Database=Raavi;User Id=raavi_app;Password=...;TrustServerCertificate=true'
& $a set config -section:system.applicationHost/applicationPools /+"[name='RaaviApi'].environmentVariables.[name='ASPNETCORE_ENVIRONMENT',value='Production']" /commit:apphost
& $a set config -section:system.applicationHost/applicationPools /+"[name='RaaviApi'].environmentVariables.[name='RAAVI_Raavi__Database__CompatibilityLevel',value='160']" /commit:apphost
& $a set config -section:system.applicationHost/applicationPools /+"[name='RaaviApi'].environmentVariables.[name='RAAVI_ConnectionStrings__System',value='$cs']" /commit:apphost
```

۴. **دسترسی‌ها** برای `IIS AppPool\RaaviApi`: خواندن روی `D:\Raavi\api`، `D:\Raavi\sandbox\config` و `D:\Raavi\sandbox\secrets`؛ و **Modify** روی `D:\Raavi\sandbox\data` (کلیدهای رمزنگاری آنجا نوشته می‌شود). اگر با Windows Authentication به SQL وصل می‌شوید، pool را با یک حساب دامنه یا سرویس اجرا کنید و به آن حساب در SQL دسترسی بدهید.

۵. روی خود سرور امتحان کنید: `Invoke-WebRequest http://127.0.0.1:5180/health/ready -UseBasicParsing` باید `Healthy` بدهد. برای خطاهای شروع، Event Log ویندوز را ببینید یا موقتاً در `D:\Raavi\api\web.config` مقدار `stdoutLogEnabled="true"` بگذارید (لاگ در `D:\Raavi\api\logs` نوشته می‌شود).

## ۶. workers به‌صورت سرویس ویندوز (NSSM)

```powershell
$n = 'C:\Tools\nssm.exe'; $cs = '<همان رشتهٔ اتصال raavi_app>'
& $n install RaaviWorkers "C:\Program Files\dotnet\dotnet.exe" "D:\Raavi\workers\Raavi.Workers.dll"
& $n set RaaviWorkers AppDirectory D:\Raavi\workers
& $n set RaaviWorkers AppEnvironmentExtra "ASPNETCORE_ENVIRONMENT=Production" "RAAVI_Raavi__Database__CompatibilityLevel=160" "RAAVI_ConnectionStrings__System=$cs"
New-Item -ItemType Directory -Force D:\Raavi\logs | Out-Null
& $n set RaaviWorkers AppStdout D:\Raavi\logs\workers.log
& $n set RaaviWorkers AppStderr D:\Raavi\logs\workers.err.log
& $n set RaaviWorkers AppRotateFiles 1
& $n set RaaviWorkers Start SERVICE_AUTO_START
Start-Service RaaviWorkers
```

مقدار `AppDirectory` باید پوشهٔ workers باشد (برنامه از همین‌جا `sandbox\config` را پیدا می‌کند). حساب سرویس روی `sandbox` همان دسترسی‌های pool را لازم دارد. workers **اجباری** است: بدون آن نه اعلان و ایمیل داریم، نه رویدادهای outbox، نه کارهای زمان‌بندی‌شده. بعد از شروع، فایل `workers.log` را از نظر خطا ببینید.

## ۷. سایت عمومی (رابط کاربری + پروکسی)

۱. تنظیمات یک‌بارهٔ سرور (روشن‌کردن پروکسی ARR، حفظ Host اصلی و اجازهٔ هدرهای forwarded):

```powershell
$a = "$env:windir\System32\inetsrv\appcmd.exe"
& $a set config -section:system.webServer/proxy /enabled:"True" /preserveHostHeader:"True" /commit:apphost
& $a set config -section:system.webServer/rewrite/allowedServerVariables /+"[name='HTTP_X_FORWARDED_HOST']" /commit:apphost
& $a set config -section:system.webServer/rewrite/allowedServerVariables /+"[name='HTTP_X_FORWARDED_PROTO']" /commit:apphost
```

این‌ها تنظیمات ARR در **سطح کل سرور** هستند. اگر سایت‌های دیگری روی همین سرور از ARR بدون حفظ Host استفاده می‌کنند (مثلاً سایت‌های دیگر شما)، آن‌ها را فقط برای این سایت بگذارید (`appcmd set config "RaaviWeb" -section:system.webServer/proxy ...` که آزموده نشده است).

۲. **Site** با نام `RaaviWeb`: مسیر فیزیکی `D:\Raavi\web`، bindingهای https برای `admin.example.com` و `*.example.com` با گواهی wildcard. pool با No Managed Code کافی است.

۳. فایل `D:\Raavi\web\web.config` (همراه بسته و با منبع `deployment/iis/web.config`) بقیهٔ کار را می‌کند: مسیرهای `api/`، `hubs/` و `health/` را به `http://127.0.0.1:5180` می‌فرستد، برای هر آدرس دیگری که فایل نیست `index.html` می‌دهد، Content-Security-Policy را می‌گذارد و فقط فایل‌های هش‌دارِ پوشهٔ `assets/` را cache می‌کند. مسیر `/internal/*` **عمداً** پروکسی نمی‌شود. اگر API روی آدرس دیگری گوش می‌دهد، دو URL داخل همین فایل را عوض کنید (و بعد از هر ارتقا دوباره اعمالش کنید).

۴. هیچ‌وقت `sandbox\` را داخل پوشهٔ وب نگذارید؛ برنامه اگر داخل ریشهٔ وب باشد شروع نمی‌شود.

## ۸. اولین راه‌اندازی (مدیر پیش‌فرض)

۱. در `D:\Raavi\sandbox\config\authentication.json` حساب bootstrap را پر کنید (به‌طور پیش‌فرض چیزی همراه بسته نیست؛ مقدار خالی یعنی «حساب bootstrap نداریم»):

```json
"Bootstrap": { "SystemAdministrator": { "Email": "admin@vesta.local", "InitialPassword": "<گذرواژهٔ موقت>", "DisplayName": "Administrator" } }
```

«نام کاربری» یک نشانی شبیه ایمیل است (لازم نیست واقعاً وجود داشته باشد). گذرواژهٔ موقت را سخت‌حدس انتخاب کنید؛ تا وقتی وارد نشده‌اید سایت از اینترنت در دسترس است. بعد pool را ری‌استارت کنید (`Restart-WebAppPool RaaviApi`). API هنگام شروع حساب را می‌سازد، **فقط اگر هنوز هیچ مدیر سیستمی وجود نداشته باشد** (در لاگ می‌نویسد «waiting for its first sign-in»).

۲. آدرس `https://admin.example.com/system/login` را باز کنید، ایمیل و گذرواژهٔ موقت را بنویسید (فیلد کد را خالی بگذارید) و وارد شوید. خودکار به صفحهٔ راه‌اندازی می‌روید: **گذرواژهٔ تازه** (دست‌کم ۱۲ نویسه) انتخاب کنید، QR را با برنامهٔ احراز هویت اسکن کنید و کد ۶ رقمی را بنویسید. وارد می‌شوید و گذرواژهٔ موقت دیگر کار نمی‌کند. (مدیر سیستم بعد از آن همیشه به کد برنامهٔ احراز هویت هم نیاز دارد.)

۳. **مقادیر `Bootstrap` را از `authentication.json` پاک کنید** (دوباره خالی) و pool را ری‌استارت کنید. بعد از سامانه اولین فضای کاری را بسازید و مدیرش را دعوت کنید.

۴. اگر گذرواژهٔ موقت را قبل از اولین ورود گم کردید، آن را در تنظیمات عوض کنید و pool را ری‌استارت کنید: تا وقتی حساب اولین ورودش را کامل نکرده، گذرواژهٔ تنظیم‌شده دوباره اعمال می‌شود. کلید اپراتور و دعوت‌نامه (`Raavi:Security:SystemApiKeys` و `docs/api/IDENTITY_API.md`) هنوز به‌عنوان راه جایگزین هستند.

## ۹. چه چیزهایی لازم نیست یا در این نسخه نیست

S3/MinIO، OpenSearch و Redis برای قابلیت‌های پیاده‌شده لازم نیستند (رشتهٔ اتصال Redis خالی می‌ماند). ویرایش هم‌زمان به سرور collaboration نیاز دارد (Node نسخهٔ 22.18 یا بالاتر، اجرای `npm ci --omit=dev` در `D:\Raavi\collaboration`، فایل `sandbox\secrets\collaboration.env`، مقدار `Raavi:Collaboration:Enabled`، کلید امضای `.pem`، کلید سرویس حداقل ۳۲ نویسه در هر دو طرف و یک سرویس NSSM دیگر). برای اولین راه‌اندازی آن را خاموش بگذارید.

## ۱۰. ارتقا

```powershell
Expand-Archive raavi-<نسخهٔ-جدید>.zip C:\Deploy\raavi-<نسخهٔ-جدید>
powershell -ExecutionPolicy Bypass -File C:\Deploy\raavi-<نسخهٔ-جدید>\scripts\release\Update-Raavi.ps1 `
  -PackageDirectory C:\Deploy\raavi-<نسخهٔ-جدید> -InstallDirectory D:\Raavi `
  -AppPool RaaviApi -Service RaaviWorkers -RunMigrator
```

این اسکریپت pool و سرویس را متوقف می‌کند، بسته را بررسی می‌کند، یک کپی برای بازگشت در `D:\Raavi\previous\<زمان>` نگه می‌دارد، فقط پوشه‌های برنامه را عوض می‌کند (`sandbox\` حفظ می‌شود و تنظیمات تازه با `sandbox sync` اضافه می‌شود)، migrator دیتابیس را اجرا می‌کند و **فقط اگر همه‌چیز موفق بود** pool و سرویس را دوباره روشن می‌کند. (برای `-RunMigrator` قبلش در همان پنجرهٔ PowerShell متغیر `RAAVI_ConnectionStrings__Migrator` را بگذارید؛ یا `-RunMigrator` را نگذارید و migrator را دستی اجرا کنید.)

اگر خطایی رخ دهد، pool و سرویس خاموش می‌مانند: پیام را بخوانید، رفع کنید و دوباره اجرا کنید. اگر مایگریشن وسط کار شکست خورد، دیتابیس را از backup برگردانید (کپی بازگشت فقط فایل‌های برنامه را برمی‌گرداند). پیش از هر ارتقا از دیتابیس و پوشهٔ `sandbox\` backup بگیرید. بخش `-AppPool` و `-Service` به ابزار مدیریت IIS و PowerShell با دسترسی ادمین نیاز دارد و روی IIS واقعی آزموده نشده است.

## ۱۱. مشکلات رایج

| نشانه | کجا را ببینید |
|---|---|
| خطای 500.30 یا 502.5 از سایت API | Hosting Bundle نصب نیست، رشتهٔ اتصال غلط است، یا `sandbox\config` پیدا نشده؛ موقتاً `stdoutLogEnabled="true"` را در `D:\Raavi\api\web.config` بگذارید |
| `/health/ready` وضعیت Unhealthy می‌دهد | دیتابیس در دسترس نیست یا لاگین `raavi_app` دسترسی ندارد (بخش ۴ مرحلهٔ ۳) |
| ورود روی `admin.` کار می‌کند ولی میزبان فضای کاری «پیدا نشد» می‌دهد | `TenantBaseDomains` یا `SystemHost` با هم نمی‌خوانند، یا ARR نام میزبان را نگه نمی‌دارد |
| صفحهٔ رابط کاربری باز می‌شود ولی همهٔ فراخوانی‌های API شکست می‌خورند | URL Rewrite یا ARR نصب نیست، یا پروکسی روشن نشده؛ `https://admin.example.com/health/live` را امتحان کنید |
| کوکی نمی‌ماند یا ورود بی‌پایان است | `PublicScheme` برابر `https` نیست، یا سایت با نام میزبانی غیر از تنظیمات باز شده |
| ایمیل اعلان نمی‌آید و رویدادها اجرا نمی‌شوند | سرویس `RaaviWorkers` اجرا نمی‌شود (`workers.err.log`) یا `Mode` ایمیل `Disabled` است |
