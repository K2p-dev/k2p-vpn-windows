# Отчёт по аудиту безопасности K2P VPN Windows (`v0.2.0 BETA`)

Дата аудита: 2026-11-25  
Область проверки: `K2pVpnWindows` (.NET 8 WPF, `Services/`, `Config/`, `Views/`)

---

## 1. Изоляция локального ядра Xray и сетевых портов
- **Проверка:** Проверены все `inbounds` в генераторе `XrayEngineService.BuildXrayConfig`.
- **Меры защиты:**
  - В режиме **Системный прокси** локальные `socks` (`10808`) и `http` (`10809`) inbound-интерфейсы привязаны **строго** к `127.0.0.1` (`"listen": "127.0.0.1"`). Привязка к `0.0.0.0` или внешним интерфейсам запрещена, что исключает превращение ПК пользователя в открытый прокси в локальной сети.
  - При отключении VPN или выходе из приложения `SystemProxyService.DisableSystemProxy()` гарантированно сбрасывает `ProxyEnable = 0` в реестре Windows (`HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`) и уведомляет систему через `InternetSetOptionW`.

## 2. Защита от Command Injection и подмены бинарников
- **Проверка:** Проверен запуск `xray.exe` и вспомогательных процессов (`WindowsIntegrationService`, `XrayEngineService`).
- **Меры защиты:**
  - Запуск `Core/xray.exe` выполняется через `ProcessStartInfo` с `UseShellExecute = false`, `CreateNoWindow = true` и передачей аргументов исключительно через `ArgumentList.Add("run")`, `ArgumentList.Add("-c")`, `ArgumentList.Add(configPath)` — конкатенация командной строки полностью исключена.
  - Перед запуском выполняется каноническая проверка путей (`Path.GetFullPath`): `xray.exe` и `wintun.dll` должны находиться строго внутри доверенной директории приложения `AppConfig.CoreDir`.
  - Для предотвращения деградации сетевых пакетов процессу `xray.exe` устанавливается `PriorityClass = ProcessPriorityClass.AboveNormal`.

## 3. Строгий TLS и защита учётных данных (DPAPI)
- **Проверка:** Проверены `K2pApiClient.cs` и `SettingsStorageService.cs`.
- **Меры защиты:**
  - Отсутствуют любые переопределения или отключения проверки TLS-сертификатов (`ServerCertificateCustomValidationCallback` не используется).
  - Единый пул соединений `SocketsHttpHandler` разделён на два изолированных `HttpClient`:
    - `_apiHttp` — отправляет `Authorization: Bearer <JWT>`, `X-Device-HWID`, `X-Device-Name` **исключительно** при обращении к доменам `*.proksbro.space` (проверка хоста перед добавлением заголовков).
    - `_publicHttp` — используется для `api.github.com`, `jsdelivr.net` и загрузки подписок без каких-либо авторизационных заголовков.
  - JWT-токен авторизации и ссылка подписки (`SubscriptionUrl`) шифруются на диске через **Windows DPAPI** (`ProtectedData.Protect` с `DataProtectionScope.CurrentUser`) в `%LocalAppData%\K2pVpn\config.json` и никогда не выводятся в логи.

## 4. Санитизация конфигурации Xray и защита от JSON-инъекций
- **Проверка:** Проверен `XrayEngineService.BuildXrayConfig` и `SubscriptionParser`.
- **Меры защиты:**
  - Конфиг `xray-runtime.json` формируется исключительно через AST-объекты `System.Text.Json.Nodes` (`JsonObject`, `JsonArray`), что автоматически экранирует любые спецсимволы в строках, комментариях к серверам и параметрах подписки, исключая JSON-инъекции и внедрение сторонних `outbound`/правил маршрутизации.
  - Перед генерацией конфига выполняется валидация `Host` (непустой, без управляющих символов) и `Port` (`1..65535`).
