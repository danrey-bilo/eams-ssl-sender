# Образ хоста Email SSL Sender.
#
# Многоэтапная сборка: SDK нужен только чтобы собрать, в рантайм уезжает ASP.NET-образ.
#
# Контекст сборки — каталог решения (корень репозитория), а не каталог проекта:
# `Directory.Packages.props` и `Directory.Build.props` лежат в корне.

# ⚠ ⚠ Обрезанный образ, но именно `-extra`, а не просто `-chiseled`. Разница — ICU: у голого
# `-chiseled` стоит `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true`, и сравнение строк, регистр и
# разбор дат перестают зависеть от культуры. Письма на трёх языках такого молча не переживут.
#
# ⚠ В обрезанном образе НЕТ ОБОЛОЧКИ: `RUN` в последней стадии невозможен.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS base
WORKDIR /app
EXPOSE 8001

# Веб-панель (React + TypeScript): собирается своей стадией, в образ уезжает только результат.
# Сначала манифесты — слой `npm ci` переиспользуется, пока зависимости не менялись.
FROM node:24-alpine AS web
WORKDIR /web
COPY ["EmailSslSender.Web/package.json", "EmailSslSender.Web/package-lock.json", "./"]
RUN npm ci --no-audit --no-fund
COPY ["EmailSslSender.Web/", "./"]
RUN PANEL_OUT_DIR=/web/dist npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
# Версия установки (тег релиза без «v»). Пусто — версия из Directory.Build.props.
ARG VERSION=
WORKDIR /src

# Сначала только файлы проектов — слой с восстановлением пакетов переиспользуется, пока
# зависимости не менялись.
#
# ⚠ СПИСОК ЗДЕСЬ ОБЯЗАН СОДЕРЖАТЬ КАЖДЫЙ МОДУЛЬ. Забытый не восстанавливается, и сборка падает
# на `dotnet publish --no-restore`: «Assets file … not found».
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["EmailSslSender.Api/EmailSslSender.Api.csproj", "EmailSslSender.Api/"]
COPY ["EmailSslSender.Shared/EmailSslSender.Shared.csproj", "EmailSslSender.Shared/"]
COPY ["EmailSslSender.Modules.Identity/EmailSslSender.Modules.Identity.csproj", "EmailSslSender.Modules.Identity/"]
COPY ["EmailSslSender.Modules.Tokens/EmailSslSender.Modules.Tokens.csproj", "EmailSslSender.Modules.Tokens/"]
COPY ["EmailSslSender.Modules.Settings/EmailSslSender.Modules.Settings.csproj", "EmailSslSender.Modules.Settings/"]
COPY ["EmailSslSender.Modules.Mail/EmailSslSender.Modules.Mail.csproj", "EmailSslSender.Modules.Mail/"]
COPY ["EmailSslSender.Modules.KeyManager/EmailSslSender.Modules.KeyManager.csproj", "EmailSslSender.Modules.KeyManager/"]
RUN dotnet restore "EmailSslSender.Api/EmailSslSender.Api.csproj"

COPY . .
RUN dotnet publish "EmailSslSender.Api/EmailSslSender.Api.csproj" \
    -c "$BUILD_CONFIGURATION" -o /publish --no-restore /p:UseAppHost=false ${VERSION:+/p:Version=$VERSION}

FROM base AS final
WORKDIR /app
COPY --from=build /publish .
COPY --from=web /web/dist ./wwwroot
# Непривилегированный пользователь из базового образа.
USER $APP_UID
ENTRYPOINT ["dotnet", "EmailSslSender.Api.dll"]
