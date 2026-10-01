# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

# Imagem da aplicação. A restauração vem antes do código para que uma alteração de fonte não
# invalide o cache das dependências. O csproj pede RequiresAspNetWebAssets porque, sem os
# .razor ainda no disco, o SDK não baixaria o blazor.web.js e a publicação sem restore o deixaria de fora.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/OpenTube.Shared/OpenTube.Shared.csproj src/OpenTube.Shared/
COPY src/OpenTube.Domain/OpenTube.Domain.csproj src/OpenTube.Domain/
COPY src/OpenTube.Infrastructure/OpenTube.Infrastructure.csproj src/OpenTube.Infrastructure/
COPY src/OpenTube.Web/OpenTube.Web.csproj src/OpenTube.Web/

RUN dotnet restore src/OpenTube.Web/OpenTube.Web.csproj

COPY src/ src/
RUN dotnet publish src/OpenTube.Web/OpenTube.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl atende o healthcheck; nc espera o banco e o storage no entrypoint do Swarm.
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl netcat-openbsd \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 1001 --create-home opentube

COPY scripts/swarm-entrypoint.sh /entrypoint.sh
RUN chmod 755 /entrypoint.sh

# Não roda como administrador: se algo der errado no processo, o estrago fica contido.
USER 1001

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["/entrypoint.sh"]
CMD ["dotnet", "OpenTube.Web.dll"]
