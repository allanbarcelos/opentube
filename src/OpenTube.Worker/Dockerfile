# Imagem do worker. Leva o FFmpeg porque é ele quem faz o trabalho pesado do pipeline.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/OpenTube.Shared/OpenTube.Shared.csproj src/OpenTube.Shared/
COPY src/OpenTube.Domain/OpenTube.Domain.csproj src/OpenTube.Domain/
COPY src/OpenTube.Infrastructure/OpenTube.Infrastructure.csproj src/OpenTube.Infrastructure/
COPY src/OpenTube.Worker/OpenTube.Worker.csproj src/OpenTube.Worker/

RUN dotnet restore src/OpenTube.Worker/OpenTube.Worker.csproj

COPY src/ src/
RUN dotnet publish src/OpenTube.Worker/OpenTube.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install --no-install-recommends -y ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 1001 --create-home opentube

USER 1001

COPY --from=build /app .

ENTRYPOINT ["dotnet", "OpenTube.Worker.dll"]
