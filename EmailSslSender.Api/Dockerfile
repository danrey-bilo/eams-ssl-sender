# Контекст сборки — корень репозитория.

# ⚠ Именно `-extra`: в нём ICU и tzdata; в образе нет оболочки — `RUN` в последней стадии невозможен.
# ⚠ Собирать с `--pull`, иначе базовый образ возьмётся из кэша со старыми уязвимостями.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS base
WORKDIR /app
EXPOSE 8001

# Веб-панель; манифесты отдельно — слой `npm ci` кэшируется.
FROM node:24-alpine AS web
WORKDIR /web
COPY ["EmailSslSender.Web/package.json", "EmailSslSender.Web/package-lock.json", "./"]
RUN npm ci --no-audit --no-fund
COPY ["EmailSslSender.Web/", "./"]
RUN PANEL_OUT_DIR=/web/dist npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
# Тег релиза без «v»; пусто — версия из Directory.Build.props.
ARG VERSION=
WORKDIR /src

# Сначала файлы проектов — слой restore кэшируется. Подмодули `modules/` — целиком (нужен `git submodule update --init`).
# ⚠ Каждый свой проект — строкой COPY ниже, иначе publish --no-restore падает.
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
# Непривилегированный пользователь.
USER $APP_UID
ENTRYPOINT ["dotnet", "EmailSslSender.Api.dll"]
