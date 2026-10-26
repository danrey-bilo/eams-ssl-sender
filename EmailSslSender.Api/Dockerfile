# Образ хоста EAMS ssl sender.
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
# ⚠ Модули EAMS (`modules/`, подмодули git) копируются ЦЕЛИКОМ и до восстановления: их проекты читают
# `module.json` и свои `Directory.*.props`, а меняется каталог только сменой закреплённой метки.
# Контекст сборки обязан нести их содержимое: `git clone --recurse-submodules` или
# `git submodule update --init` перед сборкой (README, «Модули»).
#
# ⚠ СПИСОК СВОИХ ПРОЕКТОВ ЗДЕСЬ ОБЯЗАН СОДЕРЖАТЬ КАЖДЫЙ. Забытый не восстанавливается, и сборка
# падает на `dotnet publish --no-restore`: «Assets file … not found».
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["modules/", "modules/"]
COPY ["EmailSslSender.Api/EmailSslSender.Api.csproj", "EmailSslSender.Api/"]
COPY ["EmailSslSender.Modules.KeyManager/EmailSslSender.Modules.KeyManager.csproj", "EmailSslSender.Modules.KeyManager/"]
RUN test -f modules/core/module.json || { echo "Подмодули modules/ пусты: git submodule update --init" >&2; exit 1; }
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
