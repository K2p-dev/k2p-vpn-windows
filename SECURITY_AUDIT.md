# Отчёт по аудиту безопасности K2P VPN Windows (`v0.3.5`)

Дата аудита: 2026-11-27  
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
  - Конфиги записываются строго в UTF-8 без BOM (`new UTF8Encoding(false)`).
  - При логировании в `AppLogger` поля `id`, `uuid`, `publicKey`, `public_key`, `auth`, `user`, `pass`, `username`, `password`, `token` и `Bearer ...` автоматически маскируются (`***`) для `[SING-BOX]`, `[XRAY]`, `[PING]`, `[OTA]` и при экспорте в `.txt`.
  - В `SingBoxConfigBuilder` все локальные `socks`, `http` и пингер-порты привязаны строго к `127.0.0.1`; каскадный мост `singbox-tun -> Xray` защищён случайным одноразовым логином/паролем (`RandomNumberGenerator`), а утечки DNS заблокированы (`hijack-dns`, `strict_route: true`, `strategy: "ipv4_only"`).
  - Установщик `installer.iss` устанавливает приложение и бинарники `Core/` в защищённую директорию `{autopf}\K2pVpn` (`Program Files`), предотвращая подмену бинарников непривилегированными пользователями, и корректно завершает процессы перед обновлением/удалением.
  - OTA-обновление (`WindowsIntegrationService.DownloadAndApplyUpdateAsync`): разрешает загрузку `K2pVpn-Update-*.zip` строго по `https://` с доверенных доменов GitHub (`github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`, `codeload.github.com`), проверяет SHA-256 контрольную сумму (при наличии `.sha256`), защищено от **Zip Slip** (`Path.GetFullPath` внутри `staging\`) и запускает изолированный `updater.ps1` с экранированными путями.
  - В `WindowsIntegrationService.OpenUrlInBrowser` разрешены только схемы `https://` и `http://` (защита от инъекции сторонних URI-схем).
  - В `MainWindow.TryGetSafeMediaPath` пути фоновых изображений/видео проверяются на локальность (запрещены UNC/сетевые URI), допустимые расширения и лимит размера (25 МБ для фото, 150 МБ для видео); изображения декодируются с `DecodePixelWidth = 920` и `CacheOption = OnLoad` без блокировки файла.
  - В TUN-режиме весь IPv6-трафик (`::/0`) блокируется через `block`, чтобы исключить утечку IPv6 мимо IPv4-туннеля.
